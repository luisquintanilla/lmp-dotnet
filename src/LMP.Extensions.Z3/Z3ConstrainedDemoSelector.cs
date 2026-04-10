using LMP.Optimizers;
using Microsoft.Z3;

namespace LMP.Extensions.Z3;

/// <summary>
/// Constraint-based demo selection using the Z3 SMT solver.
/// Bootstraps a demo pool, then uses Z3 to select an optimal subset
/// satisfying coverage, cardinality, and quality constraints.
/// </summary>
public sealed class Z3ConstrainedDemoSelector : IOptimizer
{
    private readonly Func<object, string> _categoryExtractor;
    private readonly Func<object, int>? _tokenCounter;
    private readonly int _maxDemos;
    private readonly float _metricThreshold;
    private readonly int? _seed;

    /// <summary>
    /// Creates a new Z3-based constrained demo selector.
    /// </summary>
    /// <param name="categoryExtractor">
    /// Extracts a category label from a demo input. Used to enforce coverage constraints
    /// (at least one demo per category).
    /// </param>
    /// <param name="tokenCounter">
    /// Optional function that estimates token count for a demo input.
    /// When provided, the solver minimizes total token usage.
    /// </param>
    /// <param name="maxDemos">Maximum demos per predictor. Default is 4.</param>
    /// <param name="metricThreshold">Minimum score for a trace to be a demo candidate. Default is 0.5.</param>
    /// <param name="seed">Optional random seed for bootstrap phase.</param>
    public Z3ConstrainedDemoSelector(
        Func<object, string> categoryExtractor,
        Func<object, int>? tokenCounter = null,
        int maxDemos = 4,
        float metricThreshold = 0.5f,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(categoryExtractor);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDemos, 1);

        _categoryExtractor = categoryExtractor;
        _tokenCounter = tokenCounter;
        _maxDemos = maxDemos;
        _metricThreshold = metricThreshold;
        _seed = seed;
    }

    /// <inheritdoc />
    public async Task<TModule> CompileAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CompileOptions? options = null,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(trainSet);
        ArgumentNullException.ThrowIfNull(metric);

        if (trainSet.Count == 0)
            return module;

        // Phase 1: Bootstrap demo pool (same as BootstrapFewShot)
        var demoPool = await BootstrapDemoPool(module, trainSet, metric, cancellationToken);

        // Phase 2: For each predictor, solve the Z3 constraint model
        foreach (var (name, predictor) in module.GetPredictors())
        {
            if (!demoPool.TryGetValue(name, out var pool) || pool.Count == 0)
                continue;

            var selected = SolveConstrainedSelection(pool);

            predictor.Demos.Clear();
            foreach (var (input, output, _) in selected)
            {
                predictor.AddDemo(input, output);
            }
        }

        // Auto-emit .g.cs artifact
        string? outputDir = options?.OutputDir;
        if (outputDir is not null)
        {
            var evalResult = await Evaluator.EvaluateAsync(
                module, trainSet, metric, cancellationToken: cancellationToken);
            await CSharpArtifactWriter.WriteAsync(
                module, outputDir, evalResult.AverageScore, nameof(Z3ConstrainedDemoSelector),
                options?.TrainDataPath, cancellationToken);
        }

        return module;
    }

    /// <summary>
    /// Bootstraps a demo pool by running the module on training data and collecting
    /// successful traces with their scores.
    /// </summary>
    private async Task<Dictionary<string, List<ScoredDemo>>> BootstrapDemoPool<TModule>(
        TModule module,
        IReadOnlyList<Example> trainSet,
        Func<Example, object, float> metric,
        CancellationToken cancellationToken)
        where TModule : LmpModule
    {
        var pool = new Dictionary<string, List<ScoredDemo>>();
        foreach (var (name, _) in module.GetPredictors())
            pool[name] = [];

        var teacher = module.Clone<TModule>();

        foreach (var example in trainSet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            teacher.Trace = new Trace();

            try
            {
                var output = await teacher.ForwardAsync(example.WithInputs(), cancellationToken);
                var score = metric(example, output);

                if (score >= _metricThreshold)
                {
                    foreach (var entry in teacher.Trace.Entries)
                    {
                        if (pool.TryGetValue(entry.PredictorName, out var list))
                        {
                            list.Add(new ScoredDemo(entry.Input, entry.Output, score));
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Skip failed examples
            }
        }

        return pool;
    }

    /// <summary>
    /// Uses Z3 to select an optimal subset of demos from the pool.
    /// Constraints: exactly <see cref="_maxDemos"/> selected, category coverage.
    /// Objective: maximize total quality score (or minimize tokens if counter provided).
    /// </summary>
    internal List<ScoredDemo> SolveConstrainedSelection(List<ScoredDemo> pool)
    {
        if (pool.Count <= _maxDemos)
            return pool;

        using var ctx = new Context(new Dictionary<string, string>
        {
            ["model"] = "true"
        });
        var opt = ctx.MkOptimize();

        // Boolean variable per demo: selected or not
        var vars = new BoolExpr[pool.Count];
        for (int i = 0; i < pool.Count; i++)
            vars[i] = ctx.MkBoolConst($"d{i}");

        // Constraint: exactly maxDemos selected
        var counts = new ArithExpr[pool.Count];
        for (int i = 0; i < pool.Count; i++)
            counts[i] = (ArithExpr)ctx.MkITE(vars[i], ctx.MkInt(1), ctx.MkInt(0));
        opt.Add(ctx.MkEq(ctx.MkAdd(counts), ctx.MkInt(_maxDemos)));

        // Category coverage: at least one demo per observed category
        var categoryGroups = new Dictionary<string, List<int>>();
        for (int i = 0; i < pool.Count; i++)
        {
            var category = _categoryExtractor(pool[i].Input);
            if (!categoryGroups.TryGetValue(category, out var group))
            {
                group = [];
                categoryGroups[category] = group;
            }
            group.Add(i);
        }

        foreach (var (_, indices) in categoryGroups)
        {
            // At least one demo from this category must be selected
            var categoryVars = indices.Select(i => vars[i]).ToArray();
            opt.Add(ctx.MkOr(categoryVars));
        }

        // Objective: maximize total quality score (scaled to int for Z3)
        var qualityTerms = new ArithExpr[pool.Count];
        for (int i = 0; i < pool.Count; i++)
        {
            int scaledScore = (int)(pool[i].Score * 1000);
            qualityTerms[i] = (ArithExpr)ctx.MkITE(vars[i], ctx.MkInt(scaledScore), ctx.MkInt(0));
        }
        opt.MkMaximize(ctx.MkAdd(qualityTerms));

        // If token counter provided, add secondary objective: minimize total tokens
        if (_tokenCounter is not null)
        {
            var tokenTerms = new ArithExpr[pool.Count];
            for (int i = 0; i < pool.Count; i++)
            {
                int tokens = _tokenCounter(pool[i].Input);
                tokenTerms[i] = (ArithExpr)ctx.MkITE(vars[i], ctx.MkInt(tokens), ctx.MkInt(0));
            }
            opt.MkMinimize(ctx.MkAdd(tokenTerms));
        }

        // Solve
        var status = opt.Check();
        if (status != Status.SATISFIABLE)
        {
            // Fallback: take top-scoring demos without constraints
            return pool.OrderByDescending(d => d.Score).Take(_maxDemos).ToList();
        }

        var model = opt.Model;
        var selected = new List<ScoredDemo>();
        for (int i = 0; i < pool.Count; i++)
        {
            var val = model.Evaluate(vars[i], true);
            if (val.IsTrue)
                selected.Add(pool[i]);
        }

        return selected;
    }

    /// <summary>A demo candidate with its quality score.</summary>
    internal sealed record ScoredDemo(object Input, object Output, float Score);
}
