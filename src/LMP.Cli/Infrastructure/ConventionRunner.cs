using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LMP.Cli.Infrastructure;

/// <summary>
/// Convention-based <see cref="ILmpRunner"/> that discovers client and metric
/// from static methods on the <c>[AutoOptimize]</c> module type.
/// </summary>
/// <remarks>
/// <para>
/// Modeled after EF Core's <c>IDesignTimeDbContextFactory&lt;T&gt;</c> pattern:
/// the user provides a well-known factory method and the tooling discovers it
/// via reflection. The CLI remains provider-agnostic — no Azure, Ollama, or
/// other provider packages required.
/// </para>
/// <para>Discovery conventions:</para>
/// <list type="bullet">
///   <item><b>Client:</b> <c>public static IChatClient CreateClient()</c> on the module type.
///   The user owns all provider packages and config. CLI calls this via reflection.</item>
///   <item><b>Metric:</b> <c>public static float Score(TOutput predicted, TOutput expected)</c>
///   on the module type. Optional — falls back to keyword overlap on string properties.</item>
///   <item><b>Dataset:</b> Loaded via <see cref="Example.LoadFromJsonl{TInput,TLabel}"/>
///   with types discovered from <see cref="LmpModule{TInput,TOutput}"/> generic args.</item>
/// </list>
/// </remarks>
internal sealed class ConventionRunner : ILmpRunner
{
    private readonly Type _moduleType;
    private readonly MethodInfo _createClient;
    private readonly Type _inputType;
    private readonly Type _outputType;
    private readonly MethodInfo _loadFromJsonl;

    private ConventionRunner(
        Type moduleType,
        MethodInfo createClient,
        Type inputType,
        Type outputType,
        MethodInfo loadFromJsonl)
    {
        _moduleType = moduleType;
        _createClient = createClient;
        _inputType = inputType;
        _outputType = outputType;
        _loadFromJsonl = loadFromJsonl;
    }

    /// <summary>
    /// Attempts to create a convention-based runner from the built assembly.
    /// Returns null with a descriptive error if conventions are not met.
    /// </summary>
    public static (ConventionRunner? Runner, string? Error) TryCreate(string assemblyPath)
    {
        // Step 1: Load assembly and find [AutoOptimize] module
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

        // Step 4: Find static CreateClient() convention
        var createClient = moduleType.GetMethod("CreateClient",
            BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);

        if (createClient is null || !typeof(IChatClient).IsAssignableFrom(createClient.ReturnType))
        {
            return (null,
                $"'{moduleType.Name}' needs a static factory method for CLI tooling.\n\n" +
                "Add this to your module (like EF Core's IDesignTimeDbContextFactory):\n\n" +
                $"    public static IChatClient CreateClient()\n" +
                "    {\n" +
                "        var config = new ConfigurationBuilder()\n" +
                $"            .AddUserSecrets<{moduleType.Name}>()\n" +
                "            .Build();\n" +
                "        return new AzureOpenAIClient(\n" +
                "                new Uri(config[\"AzureOpenAI:Endpoint\"]!),\n" +
                "                new DefaultAzureCredential())\n" +
                "            .GetChatClient(config[\"AzureOpenAI:Deployment\"]!)\n" +
                "            .AsIChatClient();\n" +
                "    }\n\n" +
                "Or register IChatClient in DI with builder.Services.AddChatClient(...).\n" +
                "Or implement ILmpRunner for full control.");
        }

        // Step 5: Resolve Example.LoadFromJsonl<TInput,TOutput>
        var loadMethod = typeof(Example)
            .GetMethod(nameof(Example.LoadFromJsonl), BindingFlags.Public | BindingFlags.Static)
            ?.MakeGenericMethod(inputType, outputType);

        if (loadMethod is null)
            return (null, "Could not resolve Example.LoadFromJsonl<TInput,TOutput>.");

        return (new ConventionRunner(moduleType, createClient, inputType, outputType, loadMethod), null);
    }

    public LmpModule CreateModule()
    {
        var client = (IChatClient)_createClient.Invoke(null, null)!;
        return (LmpModule)Activator.CreateInstance(_moduleType, client)!;
    }

    public Func<Example, object, float> CreateMetric()
    {
        // Convention: static float Score(TOutput predicted, TOutput expected)
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

        return CreateDefaultKeywordOverlapMetric();
    }

    public IReadOnlyList<Example> LoadDataset(string path)
    {
        return (IReadOnlyList<Example>)_loadFromJsonl.Invoke(null, [path, null])!;
    }

    public object DeserializeInput(string json)
    {
        return JsonSerializer.Deserialize(json, _inputType)
            ?? throw new JsonException($"Failed to deserialize input as {_inputType.Name}.");
    }

    private static (Type? Input, Type? Output) DiscoverGenericArgs(Type moduleType)
    {
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

    private static object? GetLabel(Example example)
    {
        var labelProp = example.GetType().GetProperty("Label");
        return labelProp?.GetValue(example);
    }

    private Func<Example, object, float> CreateDefaultKeywordOverlapMetric()
    {
        var stringProps = _outputType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .ToArray();

        if (stringProps.Length == 0)
        {
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
