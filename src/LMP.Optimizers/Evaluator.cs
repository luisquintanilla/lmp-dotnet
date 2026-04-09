using System.Collections.Concurrent;

namespace LMP.Optimizers;

/// <summary>
/// Runs a module on a dataset and scores results. Every optimizer uses this internally.
/// </summary>
public static class Evaluator
{
    /// <summary>
    /// Evaluates a module against a development set using the provided metric function.
    /// Runs examples concurrently up to <paramref name="maxConcurrency"/>.
    /// </summary>
    /// <typeparam name="TModule">The module type.</typeparam>
    /// <param name="module">The module to evaluate.</param>
    /// <param name="devSet">The set of examples to evaluate against.</param>
    /// <param name="metric">Scoring function: (example, module output) → score in [0, 1].</param>
    /// <param name="maxConcurrency">Maximum number of concurrent evaluations. Default is 4.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aggregate evaluation results including per-example scores.</returns>
    /// <exception cref="ArgumentNullException">Thrown when module, devSet, or metric is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when maxConcurrency is less than 1.</exception>
    public static async Task<EvaluationResult> EvaluateAsync<TModule>(
        TModule module,
        IReadOnlyList<Example> devSet,
        Func<Example, object, float> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
        where TModule : LmpModule
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(devSet);
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        if (devSet.Count == 0)
        {
            return new EvaluationResult([], 0f, 0f, 0f, 0);
        }

        var results = new ConcurrentBag<ExampleResult>();

        await Parallel.ForEachAsync(
            devSet,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cancellationToken
            },
            async (example, ct) =>
            {
                var output = await module.ForwardAsync(example.WithInputs(), ct);
                var score = metric(example, output);
                results.Add(new ExampleResult(example, output, score));
            });

        var scores = results.Select(r => r.Score).ToArray();
        return new EvaluationResult(
            PerExample: [.. results],
            AverageScore: (float)scores.Average(),
            MinScore: scores.Min(),
            MaxScore: scores.Max(),
            Count: scores.Length);
    }

    // ── Typed overloads (module output and dataset label may differ) ──

    /// <summary>
    /// Evaluates a typed module against a development set using a typed float metric.
    /// The module's output type and dataset label type may differ.
    /// When they match, the compiler infers all three type parameters automatically.
    /// </summary>
    /// <typeparam name="TInput">The module's input type.</typeparam>
    /// <typeparam name="TPredicted">The module's output type.</typeparam>
    /// <typeparam name="TExpected">The dataset label (ground truth) type.</typeparam>
    /// <param name="module">The typed module to evaluate.</param>
    /// <param name="devSet">The set of typed examples.</param>
    /// <param name="metric">Typed scoring function: (predicted, expected) → score in [0, 1].</param>
    /// <param name="maxConcurrency">Maximum number of concurrent evaluations. Default is 4.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aggregate evaluation results including per-example scores.</returns>
    public static Task<EvaluationResult> EvaluateAsync<TInput, TPredicted, TExpected>(
        LmpModule<TInput, TPredicted> module,
        IReadOnlyList<Example<TInput, TExpected>> devSet,
        Func<TPredicted, TExpected, float> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
        => EvaluateAsync(module, (IReadOnlyList<Example>)devSet,
            Metric.Create(metric), maxConcurrency, cancellationToken);

    /// <summary>
    /// Evaluates a typed module against a development set using a typed bool metric.
    /// <c>true</c> maps to <c>1.0f</c>, <c>false</c> maps to <c>0.0f</c>.
    /// The module's output type and dataset label type may differ.
    /// </summary>
    /// <typeparam name="TInput">The module's input type.</typeparam>
    /// <typeparam name="TPredicted">The module's output type.</typeparam>
    /// <typeparam name="TExpected">The dataset label (ground truth) type.</typeparam>
    /// <param name="module">The typed module to evaluate.</param>
    /// <param name="devSet">The set of typed examples.</param>
    /// <param name="metric">Typed predicate: (predicted, expected) → correct?</param>
    /// <param name="maxConcurrency">Maximum number of concurrent evaluations. Default is 4.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Aggregate evaluation results including per-example scores.</returns>
    public static Task<EvaluationResult> EvaluateAsync<TInput, TPredicted, TExpected>(
        LmpModule<TInput, TPredicted> module,
        IReadOnlyList<Example<TInput, TExpected>> devSet,
        Func<TPredicted, TExpected, bool> metric,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default)
        => EvaluateAsync(module, (IReadOnlyList<Example>)devSet,
            Metric.Create(metric), maxConcurrency, cancellationToken);
}
