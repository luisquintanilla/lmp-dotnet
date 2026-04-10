using System.Reflection;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace LMP.Cli.Infrastructure;

/// <summary>
/// A reflection-based <see cref="ILmpRunner"/> that eliminates the need for a
/// hand-written CliRunner in the user's project. When no explicit <c>ILmpRunner</c>
/// is found, the CLI falls back to this auto-wired runner.
/// </summary>
/// <remarks>
/// Auto-wiring discovers:
/// <list type="bullet">
///   <item><b>Module:</b> The first <see cref="LmpModule"/> subclass with <c>[AutoOptimize]</c>.
///   Its constructor must accept <see cref="IChatClient"/>.</item>
///   <item><b>Client:</b> Created from user secrets (<c>AzureOpenAI:Endpoint</c>,
///   <c>AzureOpenAI:Deployment</c>) using <see cref="DefaultAzureCredential"/>.
///   <c>LMP_MODEL</c> env var overrides deployment name.</item>
///   <item><b>Metric:</b> Discovered via: (1) static <c>Score(TOutput, TOutput) → float</c>
///   on the module type, (2) default keyword overlap metric on string properties.</item>
///   <item><b>Dataset:</b> Loaded via <see cref="Example.LoadFromJsonl{TInput,TLabel}"/>
///   with types discovered from <see cref="LmpModule{TInput,TOutput}"/> generic args.</item>
/// </list>
/// </remarks>
internal sealed class AutoWireRunner : ILmpRunner
{
    private readonly Type _moduleType;
    private readonly IChatClient _client;
    private readonly Type _inputType;
    private readonly Type _outputType;
    private readonly MethodInfo _loadFromJsonl;

    private AutoWireRunner(
        Type moduleType,
        IChatClient client,
        Type inputType,
        Type outputType,
        MethodInfo loadFromJsonl)
    {
        _moduleType = moduleType;
        _client = client;
        _inputType = inputType;
        _outputType = outputType;
        _loadFromJsonl = loadFromJsonl;
    }

    /// <summary>
    /// Attempts to create an auto-wired runner from the built assembly and project file.
    /// Returns null with an error message if auto-wiring is not possible.
    /// </summary>
    public static (AutoWireRunner? Runner, string? Error) TryCreate(
        string assemblyPath, string projectPath)
    {
        // Step 1: Find the [AutoOptimize] module type
        var context = new RunnerDiscovery.LmpAssemblyLoadContext(assemblyPath);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

        Type? moduleType = null;
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (type.GetCustomAttribute<AutoOptimizeAttribute>() is not null
                && typeof(LmpModule).IsAssignableFrom(type))
            {
                moduleType = type;
                break;
            }
        }

        if (moduleType is null)
            return (null, "No [AutoOptimize] module found in assembly.");

        // Step 2: Discover TInput/TOutput from LmpModule<TInput,TOutput>
        var (inputType, outputType) = DiscoverGenericArgs(moduleType);
        if (inputType is null || outputType is null)
            return (null, $"Could not discover TInput/TOutput from '{moduleType.Name}'. " +
                          "Ensure it extends LmpModule<TInput, TOutput>.");

        // Step 3: Verify constructor accepts IChatClient
        var ctor = moduleType.GetConstructor([typeof(IChatClient)]);
        if (ctor is null)
            return (null, $"'{moduleType.Name}' must have a constructor that accepts IChatClient.");

        // Step 4: Create IChatClient from user secrets
        var (client, clientError) = CreateClient(projectPath);
        if (client is null)
            return (null, clientError);

        // Step 5: Resolve Example.LoadFromJsonl<TInput,TOutput>
        var loadMethod = typeof(Example)
            .GetMethod(nameof(Example.LoadFromJsonl), BindingFlags.Public | BindingFlags.Static)
            ?.MakeGenericMethod(inputType, outputType);

        if (loadMethod is null)
            return (null, "Could not resolve Example.LoadFromJsonl<TInput,TOutput>.");

        return (new AutoWireRunner(moduleType, client, inputType, outputType, loadMethod), null);
    }

    public LmpModule CreateModule()
    {
        return (LmpModule)Activator.CreateInstance(_moduleType, _client)!;
    }

    public Func<Example, object, float> CreateMetric()
    {
        // Try: static float Score(TOutput, TOutput) on module type
        var scoreMethod = _moduleType.GetMethod("Score",
            BindingFlags.Public | BindingFlags.Static,
            [_outputType, _outputType]);

        if (scoreMethod is not null && scoreMethod.ReturnType == typeof(float))
        {
            return (example, predicted) =>
            {
                var expected = GetLabel(example);
                return (float)scoreMethod.Invoke(null, [predicted, expected])!;
            };
        }

        // Default: keyword overlap metric on string properties of the output type
        return CreateDefaultKeywordOverlapMetric();
    }

    public IReadOnlyList<Example> LoadDataset(string path)
    {
        return (IReadOnlyList<Example>)_loadFromJsonl.Invoke(null, [path])!;
    }

    public object DeserializeInput(string json)
    {
        return JsonSerializer.Deserialize(json, _inputType)
            ?? throw new JsonException($"Failed to deserialize input as {_inputType.Name}.");
    }

    private static (IChatClient? Client, string? Error) CreateClient(string projectPath)
    {
        // Read UserSecretsId from csproj
        string? userSecretsId = null;
        try
        {
            var csprojContent = File.ReadAllText(projectPath);
            var match = System.Text.RegularExpressions.Regex.Match(
                csprojContent, @"<UserSecretsId>([^<]+)</UserSecretsId>");
            if (match.Success)
                userSecretsId = match.Groups[1].Value;
        }
        catch (Exception ex)
        {
            return (null, $"Failed to read project file: {ex.Message}");
        }

        if (string.IsNullOrEmpty(userSecretsId))
        {
            return (null, "No UserSecretsId found in project file. Run:\n" +
                          "  dotnet user-secrets init\n" +
                          "  dotnet user-secrets set \"AzureOpenAI:Endpoint\" \"https://YOUR.openai.azure.com/\"\n" +
                          "  dotnet user-secrets set \"AzureOpenAI:Deployment\" \"gpt-4o-mini\"");
        }

        var config = new ConfigurationBuilder()
            .AddUserSecrets(userSecretsId)
            .Build();

        string? endpoint = config["AzureOpenAI:Endpoint"];
        if (string.IsNullOrEmpty(endpoint))
        {
            return (null, "AzureOpenAI:Endpoint not found in user secrets. Run:\n" +
                          $"  dotnet user-secrets --id {userSecretsId} set \"AzureOpenAI:Endpoint\" \"https://YOUR.openai.azure.com/\"");
        }

        // LMP_MODEL env var overrides user secrets deployment
        string? deployment = Environment.GetEnvironmentVariable("LMP_MODEL")
            ?? config["AzureOpenAI:Deployment"];

        if (string.IsNullOrEmpty(deployment))
        {
            return (null, "AzureOpenAI:Deployment not found in user secrets. Run:\n" +
                          $"  dotnet user-secrets --id {userSecretsId} set \"AzureOpenAI:Deployment\" \"gpt-4o-mini\"");
        }

        try
        {
            IChatClient client = new AzureOpenAIClient(
                    new Uri(endpoint), new DefaultAzureCredential())
                .GetChatClient(deployment)
                .AsIChatClient();

            return (client, null);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to create Azure OpenAI client: {ex.Message}");
        }
    }

    private static (Type? Input, Type? Output) DiscoverGenericArgs(Type moduleType)
    {
        // Walk up the type hierarchy looking for LmpModule<TInput,TOutput>
        var current = moduleType;
        while (current is not null)
        {
            if (current.IsGenericType &&
                current.GetGenericTypeDefinition() == typeof(LmpModule<,>))
            {
                var args = current.GetGenericArguments();
                return (args[0], args[1]);
            }
            current = current.BaseType;
        }
        return (null, null);
    }

    /// <summary>
    /// Extracts the Label property from a typed Example via the known generic type.
    /// </summary>
    private static object? GetLabel(Example example)
    {
        // Example<TInput,TLabel>.Label
        var labelProp = example.GetType().GetProperty("Label");
        return labelProp?.GetValue(example);
    }

    private Func<Example, object, float> CreateDefaultKeywordOverlapMetric()
    {
        // Find the best string property on the output type for comparison
        var stringProps = _outputType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .ToArray();

        if (stringProps.Length == 0)
        {
            // No string properties — fall back to ToString() comparison
            return (example, predicted) =>
                string.Equals(predicted?.ToString(), GetLabel(example)?.ToString(),
                    StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
        }

        return (example, predicted) =>
        {
            float totalScore = 0f;
            int count = 0;
            var label = GetLabel(example);

            foreach (var prop in stringProps)
            {
                var predictedVal = prop.GetValue(predicted) as string;
                var expectedVal = prop.GetValue(label) as string;

                if (string.IsNullOrEmpty(predictedVal) || string.IsNullOrEmpty(expectedVal))
                    continue;

                var expectedWords = expectedVal
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 3)
                    .Select(w => w.ToLowerInvariant())
                    .ToHashSet();

                if (expectedWords.Count == 0) continue;

                var predictedWords = predictedVal
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.ToLowerInvariant())
                    .ToHashSet();

                var overlap = expectedWords.Count(w => predictedWords.Contains(w));
                totalScore += (float)overlap / expectedWords.Count;
                count++;
            }

            return count > 0 ? totalScore / count : 0f;
        };
    }
}
