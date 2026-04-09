using System.Text.Json;
using LMP.Cli.Infrastructure;
using LMP.Optimizers;

namespace LMP.Cli.Commands;

/// <summary>
/// Implements the <c>dotnet lmp eval</c> command.
/// Builds a user project, discovers an <see cref="ILmpRunner"/>,
/// optionally loads saved module state, and evaluates on a dataset.
/// </summary>
internal static class EvalCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        string? project = null;
        string? dataset = null;
        string? artifact = null;
        bool jsonOutput = false;
        int maxConcurrency = 4;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" when i + 1 < args.Length:
                    project = args[++i];
                    break;
                case "--dataset" when i + 1 < args.Length:
                    dataset = args[++i];
                    break;
                case "--artifact" when i + 1 < args.Length:
                    artifact = args[++i];
                    break;
                case "--concurrency" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out maxConcurrency) || maxConcurrency < 1)
                    {
                        await Console.Error.WriteLineAsync("ERROR [args] --concurrency must be a positive integer.");
                        return Program.ExitCodes.InvalidArguments;
                    }
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    return Program.ExitCodes.Success;
                default:
                    await Console.Error.WriteLineAsync($"Unknown option: '{args[i]}'");
                    PrintHelp();
                    return Program.ExitCodes.InvalidArguments;
            }
        }

        if (string.IsNullOrEmpty(project))
        {
            await Console.Error.WriteLineAsync("ERROR [args] Missing required option: --project <path>");
            PrintHelp();
            return Program.ExitCodes.InvalidArguments;
        }

        if (string.IsNullOrEmpty(dataset))
        {
            await Console.Error.WriteLineAsync("ERROR [args] Missing required option: --dataset <path>");
            PrintHelp();
            return Program.ExitCodes.InvalidArguments;
        }

        return await RunEvalAsync(project, dataset, artifact, jsonOutput, maxConcurrency, CancellationToken.None);
    }

    internal static async Task<int> RunEvalAsync(
        string project,
        string datasetPath,
        string? artifactPath,
        bool jsonOutput,
        int maxConcurrency,
        CancellationToken cancellationToken)
    {
        // Step 1: Build the project
        await Console.Error.WriteLineAsync($"Building project: {project}");
        var buildResult = await ProjectBuilder.BuildAsync(project, cancellationToken);

        if (!buildResult.Success)
        {
            await Console.Error.WriteLineAsync($"ERROR [project] Build failed:\n{buildResult.DiagnosticOutput}");
            return Program.ExitCodes.ProjectNotFound;
        }

        await Console.Error.WriteLineAsync($"Build succeeded: {buildResult.OutputAssembly}");

        // Step 2: Discover ILmpRunner
        var discovery = RunnerDiscovery.Discover(buildResult.OutputAssembly);
        if (discovery.Runner is null)
        {
            await Console.Error.WriteLineAsync($"ERROR [discovery] {discovery.Error}");
            return Program.ExitCodes.ProjectNotFound;
        }

        // Step 3: Load dataset
        if (!File.Exists(datasetPath))
        {
            await Console.Error.WriteLineAsync($"ERROR [input] Dataset file not found: {datasetPath}");
            return Program.ExitCodes.InputParseError;
        }

        IReadOnlyList<Example> devSet;
        try
        {
            devSet = discovery.Runner.LoadDataset(datasetPath);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [input] Failed to load dataset: {ex.Message}");
            return Program.ExitCodes.InputParseError;
        }

        // Step 4: Create module and metric
        LmpModule module;
        Func<Example, object, float> metric;
        try
        {
            module = discovery.Runner.CreateModule();
            metric = discovery.Runner.CreateMetric();
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [discovery] Failed to create module or metric: {ex.Message}");
            return Program.ExitCodes.ProjectNotFound;
        }

        // Step 5: Load artifact state if provided
        if (!string.IsNullOrEmpty(artifactPath))
        {
            if (!File.Exists(artifactPath))
            {
                await Console.Error.WriteLineAsync($"ERROR [artifact] Artifact file not found: {artifactPath}");
                return Program.ExitCodes.ArtifactError;
            }

            try
            {
                await module.LoadAsync(artifactPath, cancellationToken);
                await Console.Error.WriteLineAsync($"Loaded artifact: {artifactPath}");
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"ERROR [artifact] Failed to load artifact: {ex.Message}");
                return Program.ExitCodes.ArtifactError;
            }
        }

        // Step 6: Run evaluation
        if (!jsonOutput)
        {
            await Console.Error.WriteLineAsync($"""
                LMP Eval
                ════════════════════════════════════════
                Dataset        : {devSet.Count} examples
                Artifact       : {artifactPath ?? "(none — using defaults)"}
                Concurrency    : {maxConcurrency}
                """);
        }

        EvaluationResult result;
        try
        {
            result = await Evaluator.EvaluateAsync(module, devSet, metric, maxConcurrency, cancellationToken);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [eval] Evaluation failed: {ex.Message}");
            return Program.ExitCodes.EvaluationFailed;
        }

        // Step 7: Print results
        if (jsonOutput)
        {
            PrintJsonResult(result);
        }
        else
        {
            PrintFormattedResult(result);
        }

        return Program.ExitCodes.Success;
    }

    private static void PrintFormattedResult(EvaluationResult result)
    {
        Console.WriteLine();
        Console.WriteLine("Results:");
        Console.WriteLine($"  Examples       : {result.Count}");
        Console.WriteLine($"  Average score  : {result.AverageScore:F4}");
        Console.WriteLine($"  Min score      : {result.MinScore:F4}");
        Console.WriteLine($"  Max score      : {result.MaxScore:F4}");
    }

    private static void PrintJsonResult(EvaluationResult result)
    {
        var output = new EvalJsonOutput
        {
            Command = "eval",
            DatasetSize = result.Count,
            AverageScore = result.AverageScore,
            MinScore = result.MinScore,
            MaxScore = result.MaxScore,
            ExitCode = 0
        };

        var json = JsonSerializer.Serialize(output, EvalJsonContext.Default.EvalJsonOutput);
        Console.WriteLine(json);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Usage: dotnet lmp eval --project <path> --dataset <path> [options]

            Builds a project, discovers an ILmpRunner implementation, and evaluates
            the module against a labeled dataset.

            Options:
              --project <path>     Required. Path to the .csproj file.
              --dataset <path>     Required. Path to evaluation JSONL file.
              --artifact <path>    Optional. Path to saved module state to load before evaluation.
              --concurrency <int>  Max concurrent evaluations. Default: 4.
              --json               Output machine-readable JSON instead of formatted text.
              --help, -h           Show this help message.
            """);
    }
}

/// <summary>
/// JSON output model for the eval command.
/// </summary>
internal sealed class EvalJsonOutput
{
    public required string Command { get; init; }
    public required int DatasetSize { get; init; }
    public required float AverageScore { get; init; }
    public required float MinScore { get; init; }
    public required float MaxScore { get; init; }
    public required int ExitCode { get; init; }
}

/// <summary>
/// Source-generated JSON context for eval command output.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(EvalJsonOutput))]
internal partial class EvalJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
