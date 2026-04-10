using LMP.Cli.Infrastructure;
using LMP.Optimizers;

namespace LMP.Cli.Commands;

/// <summary>
/// Implements the <c>dotnet lmp auto-optimize</c> command.
/// Builds a user project, discovers an <see cref="ILmpRunner"/>,
/// runs optimization, and writes a <c>Generated/{Module}.Optimized.g.cs</c> file
/// with the winning state as C# string literals.
/// </summary>
internal static class AutoOptimizeCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        string? project = null;
        string? train = null;
        string? dev = null;
        string optimizer = "random";
        int numTrials = 8;
        int maxDemos = 4;
        bool force = false;

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
                case "--dev" when i + 1 < args.Length:
                    dev = args[++i];
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
                case "--force":
                    force = true;
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

        var normalizedOptimizer = optimizer.ToLowerInvariant();
        if (normalizedOptimizer is not ("bootstrap" or "random"))
        {
            await Console.Error.WriteLineAsync(
                $"ERROR [args] Unknown optimizer '{optimizer}'. Supported: bootstrap, random.");
            return Program.ExitCodes.InvalidArguments;
        }

        return await RunAutoOptimizeAsync(
            project, train, dev, normalizedOptimizer,
            numTrials, maxDemos, force, CancellationToken.None);
    }

    internal static async Task<int> RunAutoOptimizeAsync(
        string project,
        string trainPath,
        string? devPath,
        string optimizerName,
        int numTrials,
        int maxDemos,
        bool force,
        CancellationToken cancellationToken)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(project)) ?? ".";

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

        // Step 3: Check staleness (skip if up-to-date unless --force)
        var moduleName = discovery.Runner.CreateModule().GetType().Name;
        var generatedPath = Path.Combine(projectDir, "Generated", $"{moduleName}.Optimized.g.cs");

        if (!force && await CSharpArtifactWriter.IsUpToDateAsync(generatedPath, trainPath, cancellationToken))
        {
            await Console.Error.WriteLineAsync($"Optimization up-to-date: {generatedPath}");
            await Console.Error.WriteLineAsync("Use --force to re-optimize.");
            return Program.ExitCodes.Success;
        }

        // Step 4: Load training data
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

        // Step 5: Create module and metric
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

        // Step 6: Create optimizer
        IOptimizer opt = optimizerName switch
        {
            "bootstrap" => new BootstrapFewShot(maxDemos),
            "random" => new BootstrapRandomSearch(numTrials, maxDemos),
            _ => throw new InvalidOperationException($"Unknown optimizer: {optimizerName}")
        };

        // Step 7: Run optimization
        await Console.Error.WriteLineAsync($"""
            LMP Auto-Optimize
            ════════════════════════════════════════
            Module         : {moduleName}
            Optimizer      : {optimizerName}
            Training set   : {trainSet.Count} examples
            Max demos      : {maxDemos}
            Num trials     : {numTrials}
            """);

        float bestScore;
        try
        {
            module = await opt.CompileAsync(module, trainSet, metric, cancellationToken);

            // Evaluate on training set to get score
            var evalResult = await Evaluator.EvaluateAsync(
                module, trainSet, metric, cancellationToken: cancellationToken);
            bestScore = evalResult.AverageScore;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [compile] Optimization failed: {ex.Message}");
            return Program.ExitCodes.CompilationFailed;
        }

        // Step 8: Write C# artifact
        string outputPath;
        try
        {
            outputPath = await CSharpArtifactWriter.WriteAsync(
                module, projectDir, bestScore, optimizerName, trainPath, cancellationToken);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [artifact] Failed to write C# artifact: {ex.Message}");
            return Program.ExitCodes.ArtifactError;
        }

        // Step 9: Print summary
        var predictors = module.GetPredictors();
        await Console.Error.WriteLineAsync($"""

            Auto-optimize complete!
            ────────────────────────────────────────
            Score          : {bestScore:F4}
            Output         : {outputPath}
            Predictors     : {predictors.Count}
            """);

        foreach (var (name, predictor) in predictors)
        {
            await Console.Error.WriteLineAsync($"  {name}: {predictor.Demos.Count} demos, instructions={predictor.Instructions?.Length ?? 0} chars");
        }

        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("Commit the generated file to source control:");
        await Console.Error.WriteLineAsync($"  git add {Path.GetRelativePath(projectDir, outputPath)}");

        // Step 10: Evaluate on dev set if provided
        if (!string.IsNullOrEmpty(devPath))
        {
            if (!File.Exists(devPath))
            {
                await Console.Error.WriteLineAsync($"ERROR [input] Dev file not found: {devPath}");
                return Program.ExitCodes.InputParseError;
            }

            try
            {
                var devSet = discovery.Runner.LoadDataset(devPath);
                var evalResult = await Evaluator.EvaluateAsync(
                    module, devSet, metric, cancellationToken: cancellationToken);

                await Console.Error.WriteLineAsync($"""

                    Validation ({devSet.Count} examples):
                      Average score  : {evalResult.AverageScore:F4}
                      Min score      : {evalResult.MinScore:F4}
                      Max score      : {evalResult.MaxScore:F4}
                    """);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"WARNING [eval] Dev set evaluation failed: {ex.Message}");
            }
        }

        return Program.ExitCodes.Success;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Usage: dotnet lmp auto-optimize --project <path> --train <path> [options]

            Builds a project, discovers an ILmpRunner implementation, runs optimization,
            and writes a Generated/{Module}.Optimized.g.cs file with the winning state.

            The generated file is a C# partial class with a partial void ApplyOptimizedState()
            method that embeds the optimized instructions, demos, and config. Commit it to
            source control for deterministic builds.

            Options:
              --project <path>     Required. Path to the .csproj file.
              --train <path>       Required. Path to training JSONL file.
              --dev <path>         Optional. Path to dev/validation JSONL file.
              --optimizer <name>   Optimizer: "random" (default) or "bootstrap".
              --num-trials <int>   Number of trials for random search. Default: 8.
              --max-demos <int>    Max demos per predictor. Default: 4.
              --force              Re-optimize even if existing artifact is up-to-date.
              --help, -h           Show this help message.
            """);
    }
}
