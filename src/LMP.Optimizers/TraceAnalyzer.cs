using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace LMP.Optimizers;

/// <summary>
/// Result of a single optimization trial: the parameter configuration and its score.
/// </summary>
/// <param name="Config">The configuration that was evaluated (parameter name → category index).</param>
/// <param name="Score">The evaluation score (higher is better).</param>
/// <param name="Cost">Optional cost measurement for this trial. Present when MIPROv2 collects usage data.</param>
public sealed record TrialResult(Dictionary<string, int> Config, float Score, TrialCost? Cost = null);

/// <summary>
/// Posterior statistics for a single parameter value.
/// </summary>
/// <param name="Mean">Average score when this parameter value was used.</param>
/// <param name="StandardError">Standard error of the mean (σ / √n).</param>
/// <param name="Count">Number of trials where this parameter value appeared.</param>
public sealed record ParameterPosterior(double Mean, double StandardError, int Count);

/// <summary>
/// Empirical Bayesian analysis of optimization trial history.
/// Computes per-parameter-value posterior distributions, detects interactions
/// between parameters, and provides warm-start priors for transfer learning.
/// Zero external dependencies — uses frequentist statistics on observed trial data.
/// </summary>
public static class TraceAnalyzer
{
    /// <summary>
    /// Computes posterior distributions (mean ± standard error) for each parameter value
    /// across all trials. A high mean with low standard error indicates a confident winner.
    /// </summary>
    /// <param name="trials">Completed trial results.</param>
    /// <param name="cardinalities">Parameter name → number of distinct category values.</param>
    /// <returns>
    /// Nested dictionary: parameter name → { value index → posterior stats }.
    /// Missing entries indicate a value was never observed.
    /// </returns>
    public static Dictionary<string, Dictionary<int, ParameterPosterior>> ComputePosteriors(
        IReadOnlyList<TrialResult> trials,
        Dictionary<string, int> cardinalities)
    {
        ArgumentNullException.ThrowIfNull(trials);
        ArgumentNullException.ThrowIfNull(cardinalities);

        var result = new Dictionary<string, Dictionary<int, ParameterPosterior>>();

        foreach (var (paramName, cardinality) in cardinalities)
        {
            var posteriors = new Dictionary<int, ParameterPosterior>();

            for (int valueIdx = 0; valueIdx < cardinality; valueIdx++)
            {
                var scores = trials
                    .Where(t => t.Config.GetValueOrDefault(paramName, -1) == valueIdx)
                    .Select(t => (double)t.Score)
                    .ToList();

                if (scores.Count == 0)
                    continue;

                var scoresSpan = CollectionsMarshal.AsSpan(scores);
                double mean = TensorPrimitives.Average<double>(scoresSpan);
                double stdDev = 0;
                if (scores.Count > 1)
                {
                    var deviations = new double[scores.Count];
                    for (int i = 0; i < scores.Count; i++)
                        deviations[i] = scoresSpan[i] - mean;
                    TensorPrimitives.Multiply<double>(deviations, deviations, deviations);
                    stdDev = Math.Sqrt(TensorPrimitives.Sum<double>(deviations) / (scores.Count - 1));
                }
                double stdError = stdDev / Math.Sqrt(scores.Count);

                posteriors[valueIdx] = new ParameterPosterior(mean, stdError, scores.Count);
            }

            result[paramName] = posteriors;
        }

        return result;
    }

    /// <summary>
    /// Detects interaction effects between parameter pairs using ANOVA-style residual analysis.
    /// A high interaction strength indicates the combined effect of two parameters differs
    /// significantly from the sum of their individual effects (synergy or conflict).
    /// </summary>
    /// <param name="trials">Completed trial results.</param>
    /// <returns>
    /// Dictionary mapping (param1, param2) → interaction strength.
    /// Higher values indicate stronger interactions. Zero means perfectly additive.
    /// </returns>
    public static Dictionary<(string, string), double> DetectInteractions(
        IReadOnlyList<TrialResult> trials)
    {
        ArgumentNullException.ThrowIfNull(trials);

        if (trials.Count < 3)
            return [];

        var interactions = new Dictionary<(string, string), double>();
        var paramNames = trials[0].Config.Keys.OrderBy(k => k).ToList();
        double globalMean = trials.Average(t => t.Score);

        // Precompute main effects per parameter value
        var mainEffects = new Dictionary<string, Dictionary<int, double>>();
        foreach (string param in paramNames)
        {
            var effects = new Dictionary<int, double>();
            foreach (var group in trials.GroupBy(t => t.Config.GetValueOrDefault(param, 0)))
            {
                effects[group.Key] = group.Average(t => t.Score) - globalMean;
            }
            mainEffects[param] = effects;
        }

        // For each pair, compute residual variance (interaction strength)
        for (int i = 0; i < paramNames.Count - 1; i++)
        {
            for (int j = i + 1; j < paramNames.Count; j++)
            {
                string p1 = paramNames[i], p2 = paramNames[j];
                double residualSum = 0;
                int count = 0;

                foreach (var trial in trials)
                {
                    int v1 = trial.Config.GetValueOrDefault(p1, 0);
                    int v2 = trial.Config.GetValueOrDefault(p2, 0);

                    double effect1 = mainEffects[p1].GetValueOrDefault(v1, 0);
                    double effect2 = mainEffects[p2].GetValueOrDefault(v2, 0);
                    double predicted = globalMean + effect1 + effect2;
                    double residual = trial.Score - predicted;
                    residualSum += residual * residual;
                    count++;
                }

                interactions[(p1, p2)] = count > 0 ? residualSum / count : 0;
            }
        }

        return interactions;
    }

    /// <summary>
    /// Warm-starts an <see cref="ISampler"/> by generating synthetic trials from posteriors.
    /// This transfers knowledge from a prior optimization run to a new sampler,
    /// enabling faster convergence on related tasks.
    /// </summary>
    /// <param name="sampler">The sampler to warm-start.</param>
    /// <param name="posteriors">Posteriors from a prior run (from <see cref="ComputePosteriors"/>).</param>
    /// <param name="numSyntheticTrials">Number of synthetic trials to generate per parameter value.</param>
    public static void WarmStart(
        ISampler sampler,
        Dictionary<string, Dictionary<int, ParameterPosterior>> posteriors,
        int numSyntheticTrials = 3)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(posteriors);
        ArgumentOutOfRangeException.ThrowIfLessThan(numSyntheticTrials, 1);

        // For each parameter value with posteriors, create synthetic trials
        // using the posterior mean as the score. This biases the sampler
        // toward configurations that worked well in the prior task.
        var paramNames = posteriors.Keys.OrderBy(k => k).ToList();

        foreach (string paramName in paramNames)
        {
            foreach (var (valueIdx, posterior) in posteriors[paramName])
            {
                for (int i = 0; i < numSyntheticTrials; i++)
                {
                    // Create a config with this parameter value and random others
                    var config = new Dictionary<string, int>();
                    foreach (string p in paramNames)
                    {
                        if (p == paramName)
                            config[p] = valueIdx;
                        else
                        {
                            // Use the best value from posteriors for other params
                            var otherPosteriors = posteriors[p];
                            config[p] = otherPosteriors.Count > 0
                                ? otherPosteriors.MaxBy(kv => kv.Value.Mean).Key
                                : 0;
                        }
                    }

                    sampler.Update(config, (float)posterior.Mean);
                }
            }
        }
    }
}
