using LMP.Cli.Infrastructure;
using LMP.Optimizers;
using Microsoft.Extensions.AI;

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
        string? model = null;
        string? output = null;
        string optimizer = "random";
        int numTrials = 8;
        int maxDemos = 4;
        int? budgetSeconds = null;
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
                case "--model" when i + 1 < args.Length:
                    model = args[++i];
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
                case "--budget" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var b) || b < 1)
                    {
                        await Console.Error.WriteLineAsync("ERROR [args] --budget must be a positive integer (seconds).");
                        return Program.ExitCodes.InvalidArguments;
                    }
                    budgetSeconds = b;
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

        var normalizedOptimizer = optimizer.ToLowerInvariant();
        if (normalizedOptimizer is not ("bootstrap" or "random" or "mipro" or "gepa"))
        {
            await Console.Error.WriteLineAsync(
                $"ERROR [args] Unknown optimizer '{optimizer}'. Supported: bootstrap, random, mipro, gepa.");
            return Program.ExitCodes.InvalidArguments;
        }

        return await RunAutoOptimizeAsync(
            project, train, dev, model, output, normalizedOptimizer,
            numTrials, maxDemos, budgetSeconds, force, CancellationToken.None);
    }

    internal static async Task<int> RunAutoOptimizeAsync(
        string project,
        string? trainPath,
        string? devPath,
        string? model,
        string? outputDir,
        string optimizerName,
        int numTrials,
        int maxDemos,
        int? budgetSeconds,
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

        // Step 2: Discover [AutoOptimize] attribute values (train/dev/budget)
        var attrConfig = RunnerDiscovery.DiscoverAutoOptimizeConfig(buildResult.OutputAssembly);
        if (attrConfig is not null)
        {
            // CLI args override attribute values
            trainPath ??= attrConfig.TrainSet is not null
                ? Path.Combine(projectDir, attrConfig.TrainSet)
                : null;
            devPath ??= attrConfig.DevSet is not null
                ? Path.Combine(projectDir, attrConfig.DevSet)
                : null;
            // BudgetSeconds from attribute is used to set numTrials if not explicitly overridden
            budgetSeconds ??= attrConfig.BudgetSeconds > 0 ? attrConfig.BudgetSeconds : null;
        }

        if (string.IsNullOrEmpty(trainPath))
        {
            await Console.Error.WriteLineAsync(
                "ERROR [args] No training data specified. Either:\n" +
                "  - Set TrainSet in [AutoOptimize] attribute: [AutoOptimize(TrainSet = \"data/train.jsonl\")]\n" +
                "  - Pass --train <path> on the command line");
            return Program.ExitCodes.InvalidArguments;
        }

        // Step 3: Discover ILmpRunner (or fall back to auto-wiring)
        // Set LMP_MODEL env var if --model was specified, so runner can read it
        if (!string.IsNullOrEmpty(model))
            Environment.SetEnvironmentVariable("LMP_MODEL", model);

        ILmpRunner runner;
        var discovery = RunnerDiscovery.Discover(buildResult.OutputAssembly);
        if (discovery.Runner is not null)
        {
            runner = discovery.Runner;
        }
        else
        {
            // No ILmpRunner found — try convention-based discovery (static CreateClient())
            await Console.Error.WriteLineAsync("No ILmpRunner found. Discovering via conventions (static CreateClient())...");
            var (conventionRunner, conventionError) = ConventionRunner.TryCreate(buildResult.OutputAssembly);
            if (conventionRunner is null)
            {
                await Console.Error.WriteLineAsync($"ERROR [discovery] {conventionError}");
                return Program.ExitCodes.ProjectNotFound;
            }
            runner = conventionRunner;
        }

        // Step 4: Check staleness (skip if up-to-date unless --force)
        var moduleName = runner.CreateModule().GetType().Name;
        var generatedDir = outputDir ?? Path.Combine(projectDir, "Generated");
        var generatedPath = Path.Combine(generatedDir, $"{moduleName}.Optimized.g.cs");

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
            trainSet = runner.LoadDataset(trainPath);
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
            module = runner.CreateModule();
            metric = runner.CreateMetric();
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [discovery] Failed to create module or metric: {ex.Message}");
            return Program.ExitCodes.ProjectNotFound;
        }

        // Step 5b: Measure baseline score (before optimization)
        float baselineScore;
        try
        {
            var baselineResult = await Evaluator.EvaluateAsync(
                module, trainSet, metric, cancellationToken: cancellationToken);
            baselineScore = baselineResult.AverageScore;
            await Console.Error.WriteLineAsync($"Baseline score: {baselineScore:F4}");
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"WARNING [baseline] Could not measure baseline: {ex.Message}");
            baselineScore = 0f;
        }

        // Step 6: Create optimizer
        // For MIPROv2/GEPA, we need an IChatClient for proposal/reflection generation.
        // ConventionRunner exposes CreateClient(); for ILmpRunner users we reuse CreateModule's client.
        IOptimizer opt = optimizerName switch
        {
            "bootstrap" => new BootstrapFewShot(maxDemos),
            "random" => new BootstrapRandomSearch(numTrials, maxDemos),
            "mipro" => new MIPROv2(
                proposalClient: GetProposalClient(runner),
                numTrials: numTrials,
                maxDemos: maxDemos),
            "gepa" => new GEPA(
                reflectionClient: GetProposalClient(runner),
                maxIterations: numTrials),
            _ => throw new InvalidOperationException($"Unknown optimizer: {optimizerName}")
        };

        // Step 7: Run optimization
        string budgetDisplay = budgetSeconds.HasValue ? $"{budgetSeconds}s" : "unlimited";
        await Console.Error.WriteLineAsync($"""
            LMP Auto-Optimize
            ════════════════════════════════════════
            Module         : {moduleName}
            Optimizer      : {optimizerName}
            Training set   : {trainSet.Count} examples
            Baseline score : {baselineScore:F4}
            Max demos      : {maxDemos}
            Num trials     : {numTrials}
            Budget         : {budgetDisplay}
            """);

        float bestScore;
        string outputPath;
        try
        {
            using var budgetCts = budgetSeconds.HasValue
                ? new CancellationTokenSource(TimeSpan.FromSeconds(budgetSeconds.Value))
                : new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, budgetCts.Token);

            var compileOptions = new CompileOptions
            {
                OutputDir = generatedDir,
                TrainDataPath = trainPath,
                Baseline = baselineScore
            };

            try
            {
                module = await opt.CompileAsync(module, trainSet, metric, compileOptions, linked.Token);
            }
            catch (OperationCanceledException) when (budgetCts.IsCancellationRequested)
            {
                await Console.Error.WriteLineAsync($"Budget of {budgetSeconds}s reached. Using best result so far.");
            }

            // Evaluate on training set to get score
            var evalResult = await Evaluator.EvaluateAsync(
                module, trainSet, metric, cancellationToken: cancellationToken);
            bestScore = evalResult.AverageScore;
            outputPath = Path.Combine(generatedDir, $"{module.GetType().Name}.Optimized.g.cs");
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"ERROR [compile] Optimization failed: {ex.Message}");
            return Program.ExitCodes.CompilationFailed;
        }

        // Step 9: Print summary with baseline comparison
        float improvement = bestScore - baselineScore;
        float improvementPct = baselineScore > 0 ? (improvement / baselineScore) * 100f : 0f;
        var predictors = module.GetPredictors();
        await Console.Error.WriteLineAsync($"""

            Auto-optimize complete!
            ────────────────────────────────────────
            Baseline       : {baselineScore:F4}
            Optimized      : {bestScore:F4}  ({(improvement >= 0 ? "+" : "")}{improvement:F4}, {(improvement >= 0 ? "+" : "")}{improvementPct:F1}%)
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
                var devSet = runner.LoadDataset(devPath);
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

    /// <summary>
    /// Gets an IChatClient for proposal/reflection LMs (MIPROv2, GEPA).
    /// ConventionRunner exposes CreateClient() directly; for ILmpRunner users,
    /// the module must be created via a convention with CreateClient().
    /// </summary>
    private static IChatClient GetProposalClient(ILmpRunner runner)
    {
        if (runner is ConventionRunner convention)
            return convention.CreateClient();

        throw new InvalidOperationException(
            "MIPROv2 and GEPA optimizers require an IChatClient for proposal/reflection generation. " +
            "Use a module with a static CreateClient() convention, or choose 'bootstrap'/'random' optimizer.");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Usage: dotnet lmp auto-optimize --project <path> [options]

            Builds a project, discovers [AutoOptimize] modules and ILmpRunner, runs
            optimization, and writes Generated/{Module}.Optimized.g.cs with the winning state.

            Training data and budget are read from the [AutoOptimize] attribute by default.
            CLI flags override attribute values when specified.

            Typically invoked automatically by MSBuild via the LMP.Build package:
              dotnet build -p:LmpAutoOptimize=true

            Options:
              --project <path>     Required. Path to the .csproj file.
              --train <path>       Training JSONL file. Overrides [AutoOptimize(TrainSet)].
              --dev <path>         Dev/validation JSONL file. Overrides [AutoOptimize(DevSet)].
              --model <name>       LLM model/deployment override. Sets LMP_MODEL env var.
              --output <dir>       Output directory for .g.cs files. Default: Generated/.
              --optimizer <name>   Optimizer: "random" (default), "bootstrap", "mipro", or "gepa".
              --num-trials <int>   Number of trials/iterations. Default: 8.
              --max-demos <int>    Max demos per predictor. Default: 4.
              --budget <seconds>   Time budget in seconds. Overrides [AutoOptimize(BudgetSeconds)].
              --force              Re-optimize even if existing artifact is up-to-date.
              --help, -h           Show this help message.
            """);
    }
}
