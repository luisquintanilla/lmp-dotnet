using LMP.Cli.Infrastructure;
using LMP.Optimizers;

namespace LMP.Cli.Commands;

/// <summary>
/// Implements the <c>dotnet lmp optimize</c> command.
/// Builds a user project, discovers an <see cref="ILmpRunner"/>,
/// runs optimization, and saves the optimized module state.
/// </summary>
internal static class OptimizeCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        string? project = null;
        string? train = null;
        string? output = null;
        string optimizer = "random";
        int numTrials = 8;
        int maxDemos = 4;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" when i + 1 < args.Length:
                    project = args[++i];
                    break;
                case "--train" when i + 1 < args.Length:
                    train = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--optimizer" when i + 1 < args.Length:
                    optimizer = args[++i];
                    break;
                case "--num-trials" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out numTrials) || numTrials < 1)
                    {
                        await Console.Error.WriteLineAsync("ERROR [args] --num-trials must be a positive integer.");
                        return Program.ExitCodes.InvalidArguments;
                    }
                    break;
                case "--max-demos" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out maxDemos) || maxDemos < 1)
                    {
                        await Console.Error.WriteLineAsync("ERROR [args] --max-demos must be a positive integer.");
                        return Program.ExitCodes.InvalidArguments;
                    }
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

        if (string.IsNullOrEmpty(train))
        {
            await Console.Error.WriteLineAsync("ERROR [args] Missing required option: --train <path>");
            PrintHelp();
            return Program.ExitCodes.InvalidArguments;
        }

        output ??= Path.Combine(Path.GetDirectoryName(project) ?? ".", "lmp-output", "module-state.json");

        return await RunOptimizeAsync(project, train, output, optimizer, numTrials, maxDemos, CancellationToken.None);
    }

    internal static async Task<int> RunOptimizeAsync(
        string project,
        string trainPath,
        string outputPath,
        string optimizerName,
        int numTrials,
        int maxDemos,
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

        // Step 3: Load training data
        if (!File.Exists(trainPath))
        {
            await Console.Error.WriteLineAsync($"ERROR [input] Training file not found: {trainPath}");
            return Program.ExitCodes.InputParseError;
        }

        IReadOnlyList<Example> trainSet;
        try
        {
            trainSet = discovery.Runner.LoadDataset(trainPath);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [input] Failed to load training data: {ex.Message}");
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

        // Step 5: Create optimizer
        IOptimizer opt = optimizerName.ToLowerInvariant() switch
        {
            "bootstrap" => new BootstrapFewShot(maxDemos),
            "random" => new BootstrapRandomSearch(numTrials, maxDemos),
            _ => new BootstrapRandomSearch(numTrials, maxDemos)
        };

        // Step 6: Run optimization
        await Console.Error.WriteLineAsync($"""
            LMP Optimize
            ════════════════════════════════════════
            Optimizer      : {optimizerName}
            Training set   : {trainSet.Count} examples
            Max demos      : {maxDemos}
            Num trials     : {numTrials}
            """);

        try
        {
            module = await opt.CompileAsync(module, trainSet, metric, cancellationToken);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [compile] Optimization failed: {ex.Message}");
            return Program.ExitCodes.CompilationFailed;
        }

        // Step 7: Save optimized state
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        try
        {
            await module.SaveAsync(outputPath, cancellationToken);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [artifact] Failed to save module state: {ex.Message}");
            return Program.ExitCodes.ArtifactError;
        }

        // Step 8: Print summary
        var predictors = module.GetPredictors();
        await Console.Error.WriteLineAsync($"""

            Optimization complete!
            ────────────────────────────────────────
            Output         : {outputPath}
            Predictors     : {predictors.Count}
            """);

        foreach (var (name, predictor) in predictors)
        {
            await Console.Error.WriteLineAsync($"  {name}: {predictor.Demos.Count} demos");
        }

        return Program.ExitCodes.Success;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Usage: dotnet lmp optimize --project <path> --train <path> [options]

            Builds a project, discovers an ILmpRunner implementation, runs optimization,
            and saves the optimized module state.

            Options:
              --project <path>     Required. Path to the .csproj file.
              --train <path>       Required. Path to training JSONL file.
              --output <path>      Output path for saved module state. Default: <project-dir>/lmp-output/module-state.json
              --optimizer <name>   Optimizer: "random" (default) or "bootstrap".
              --num-trials <int>   Number of trials for random search. Default: 8.
              --max-demos <int>    Max demos per predictor. Default: 4.
              --help, -h           Show this help message.
            """);
    }
}
