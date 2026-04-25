#pragma warning disable CS0618 // implements obsolete ISampler intentionally for backward compat
namespace LMP.Optimizers;

/// <summary>
/// Cost-aware sampler for categorical search spaces based on FLAML's Flow2 algorithm
/// (AAAI 2021 CostFrugal). Uses randomized local search with adaptive step sizes
/// that account for both quality and cost of evaluations.
/// </summary>
/// <remarks>
/// <para>
/// The sampler maps categorical parameters to a continuous space, performs local
/// search with Gaussian perturbations on the unit sphere, and projects back to
/// categorical indices. A user-supplied cost projection function drives step-size
/// adaptation: expensive evaluations trigger more conservative steps, while cheap
/// improvements expand the search radius.
/// </para>
/// <para>
/// This design follows the <see cref="Metric"/> pattern: users provide a
/// <c>Func&lt;TrialCost, double&gt;</c> to project multi-dimensional cost
/// to a single scalar for the acquisition function.
/// </para>
/// </remarks>
public sealed class CostAwareSampler : ISampler, ISearchStrategy
{
    private readonly string[] _paramNames;
    private readonly Func<TrialCost, double> _costProjection;
    private readonly Flow2Categorical _flow;
    private readonly SearchThread _thread;

    /// <summary>
    /// Creates a new cost-aware sampler for a categorical search space.
    /// </summary>
    /// <param name="cardinalities">
    /// Maps parameter name → number of distinct category values.
    /// For example, <c>{ "classify_instruction" → 5, "classify_demos" → 4 }</c>.
    /// </param>
    /// <param name="costProjection">
    /// Projects multi-dimensional <see cref="TrialCost"/> to a scalar cost value.
    /// Default is <c>c =&gt; c.TotalTokens</c>. Examples:
    /// <list type="bullet">
    /// <item>Dollar pricing: <c>c =&gt; c.OutputTokens * 0.06/1000 + c.InputTokens * 0.01/1000</c></item>
    /// <item>Latency: <c>c =&gt; c.ElapsedMilliseconds</c></item>
    /// <item>Blended: <c>c =&gt; c.TotalTokens * 0.7 + c.ElapsedMilliseconds * 0.3</c></item>
    /// </list>
    /// </param>
    /// <param name="seed">Random seed for reproducibility. Default is 42.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="cardinalities"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="cardinalities"/> is empty.
    /// </exception>
    public CostAwareSampler(
        Dictionary<string, int> cardinalities,
        Func<TrialCost, double>? costProjection = null,
        int seed = 42)
    {
        ArgumentNullException.ThrowIfNull(cardinalities);
        if (cardinalities.Count == 0)
            throw new ArgumentException("At least one parameter is required.", nameof(cardinalities));

        _paramNames = [.. cardinalities.Keys];
        var cards = new int[_paramNames.Length];
        for (int i = 0; i < _paramNames.Length; i++)
            cards[i] = cardinalities[_paramNames[i]];

        _costProjection = costProjection ?? (static c => c.TotalTokens);

        var rng = new Random(seed);
        _flow = new Flow2Categorical(cards, rng);
        _thread = new SearchThread();
    }

    /// <inheritdoc />
    public int TrialCount => _thread.TrialCount;

    /// <summary>
    /// Whether the search has converged (no improvement for many consecutive trials).
    /// Once converged, proposals still return valid configurations but are unlikely
    /// to discover significantly better regions.
    /// </summary>
    public bool IsConverged => _thread.IsConverged;

    /// <inheritdoc />
    public Dictionary<string, int> Propose()
    {
        var indices = _flow.Propose(_thread.StepSize);

        var config = new Dictionary<string, int>(_paramNames.Length);
        for (int i = 0; i < _paramNames.Length; i++)
            config[_paramNames[i]] = indices[i];

        return config;
    }

    /// <summary>
    /// Reports the result of evaluating a proposed configuration without cost data.
    /// Step adaptation uses score only; no cost penalty is applied.
    /// </summary>
    /// <param name="config">The configuration that was evaluated.</param>
    /// <param name="score">The evaluation score (higher is better).</param>
    public void Update(Dictionary<string, int> config, float score)
    {
        ArgumentNullException.ThrowIfNull(config);
        bool improved = _thread.RecordTrial(score, cost: 0);
        _flow.CommitProposal(improved);
    }

    /// <summary>
    /// Reports the result of evaluating a proposed configuration with cost data.
    /// The cost projection function converts <paramref name="cost"/> to a scalar,
    /// which drives cost-aware step adaptation: expensive trials cause the step size
    /// to shrink more aggressively.
    /// </summary>
    /// <param name="config">The configuration that was evaluated.</param>
    /// <param name="score">The evaluation score (higher is better).</param>
    /// <param name="cost">The cost measurement for this trial.</param>
    public void Update(Dictionary<string, int> config, float score, TrialCost cost)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(cost);

        double projectedCost = _costProjection(cost);
        bool improved = _thread.RecordTrial(score, projectedCost);
        _flow.CommitProposal(improved);
    }

    // ── ISearchStrategy (explicit, thin bridge) ──────────────────────────

    /// <inheritdoc />
    ParameterAssignment ISearchStrategy.Propose(TypedParameterSpace space)
    {
        ArgumentNullException.ThrowIfNull(space);
        var config = Propose();
        return ParameterAssignment.FromCategorical(config);
    }

    /// <inheritdoc />
    void ISearchStrategy.Update(ParameterAssignment assignment, float score, TrialCost cost)
    {
        var config = assignment.ToCategoricalDictionary();
        Update(config, score, cost);  // delegates to cost-aware Update overload
    }
}

/// <summary>
/// Tracks cost, adapts step size, and detects convergence for a single search thread.
/// Implements the step-size schedule from FLAML's Flow2: grow on improvement, shrink
/// on stagnation, with an additional cost penalty for expensive evaluations.
/// </summary>
internal sealed class SearchThread
{
    private double _stepSize = InitialStepSize;
    private double _bestScore = double.NegativeInfinity;
    private double _totalCost;
    private int _trialCount;
    private int _stagnationCount;

    private const double InitialStepSize = 1.0;
    private const double StepGrowFactor = 2.0;
    private const double StepShrinkFactor = 0.5;
    private const double MinStepSize = 0.01;
    private const double MaxStepSize = 8.0;
    private const int ConvergencePatience = 10;
    private const double CostPenaltyThreshold = 1.5;

    /// <summary>Number of completed trials.</summary>
    public int TrialCount => _trialCount;

    /// <summary>Current adaptive step size for the search.</summary>
    public double StepSize => _stepSize;

    /// <summary>Best score observed so far.</summary>
    public double BestScore => _bestScore;

    /// <summary>
    /// Whether the search has stagnated for <see cref="ConvergencePatience"/> consecutive trials.
    /// </summary>
    public bool IsConverged => _stagnationCount >= ConvergencePatience;

    /// <summary>
    /// Records a trial result and adjusts step size.
    /// </summary>
    /// <param name="score">Evaluation score (higher is better).</param>
    /// <param name="cost">Projected scalar cost for this trial.</param>
    /// <returns><c>true</c> if <paramref name="score"/> is a new best (improvement).</returns>
    public bool RecordTrial(double score, double cost)
    {
        _trialCount++;
        _totalCost += cost;

        bool improved = score > _bestScore;

        if (improved)
        {
            _bestScore = score;
            _stepSize = Math.Min(_stepSize * StepGrowFactor, MaxStepSize);
            _stagnationCount = 0;
        }
        else
        {
            _stepSize = Math.Max(_stepSize * StepShrinkFactor, MinStepSize);
            _stagnationCount++;
        }

        // Cost-aware penalty: if trial was expensive relative to running average, shrink further
        double avgCost = _totalCost / _trialCount;
        if (avgCost > 0 && cost > avgCost * CostPenaltyThreshold)
        {
            _stepSize = Math.Max(_stepSize * StepShrinkFactor, MinStepSize);
        }

        return improved;
    }
}

/// <summary>
/// Discretized Flow2 local search on categorical space. Maps categorical parameters
/// to a continuous space [0, cardinality_i), samples Gaussian perturbations on the
/// unit sphere, and projects back to categorical indices via floor + clamp.
/// </summary>
internal sealed class Flow2Categorical
{
    private readonly int _dim;
    private readonly int[] _cardinalities;
    private readonly Random _rng;
    private readonly double[] _currentPosition;
    private double[]? _proposedPosition;
    private bool _hasProposed;

    public Flow2Categorical(int[] cardinalities, Random rng)
    {
        _dim = cardinalities.Length;
        _cardinalities = cardinalities;
        _rng = rng;

        // Initialize at a random position in continuous space
        _currentPosition = new double[_dim];
        for (int i = 0; i < _dim; i++)
            _currentPosition[i] = rng.NextDouble() * cardinalities[i];
    }

    /// <summary>
    /// Proposes a new configuration by sampling a perturbation from the current position.
    /// On the first call, returns the discretized initial position.
    /// </summary>
    /// <param name="stepSize">Adaptive step size from the <see cref="SearchThread"/>.</param>
    /// <returns>Categorical indices for each parameter dimension.</returns>
    public int[] Propose(double stepSize)
    {
        if (!_hasProposed)
        {
            _hasProposed = true;
            _proposedPosition = (double[])_currentPosition.Clone();
        }
        else
        {
            var direction = SampleUnitSphere();

            _proposedPosition = new double[_dim];
            for (int i = 0; i < _dim; i++)
            {
                // Scale perturbation by step size and sqrt(cardinality) so each
                // parameter's range is respected proportionally.
                double perturbation = stepSize * direction[i] * Math.Sqrt(_cardinalities[i]);
                _proposedPosition[i] = _currentPosition[i] + perturbation;

                // Clamp to valid continuous range [0, cardinality)
                _proposedPosition[i] = Math.Clamp(
                    _proposedPosition[i], 0, _cardinalities[i] - 1e-10);
            }
        }

        return Discretize(_proposedPosition);
    }

    /// <summary>
    /// Commits or rejects the last proposal. If the trial improved the score,
    /// the current position moves to the proposed position.
    /// </summary>
    /// <param name="accept">Whether the trial was an improvement.</param>
    public void CommitProposal(bool accept)
    {
        if (accept && _proposedPosition is not null)
        {
            Array.Copy(_proposedPosition, _currentPosition, _dim);
        }
    }

    /// <summary>
    /// Converts continuous positions to categorical indices via floor + clamp.
    /// </summary>
    private int[] Discretize(double[] position)
    {
        var indices = new int[_dim];
        for (int i = 0; i < _dim; i++)
            indices[i] = Math.Clamp((int)Math.Floor(position[i]), 0, _cardinalities[i] - 1);
        return indices;
    }

    /// <summary>
    /// Samples a random direction uniformly distributed on the unit sphere
    /// using the Gaussian method: sample i.i.d. standard normals, then normalize.
    /// </summary>
    private double[] SampleUnitSphere()
    {
        var direction = new double[_dim];
        double sumSq = 0;

        for (int i = 0; i < _dim; i++)
        {
            // Box-Muller transform for standard normal
            double u1 = 1.0 - _rng.NextDouble(); // avoid log(0)
            double u2 = _rng.NextDouble();
            direction[i] = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            sumSq += direction[i] * direction[i];
        }

        // Normalize to unit length
        double norm = Math.Sqrt(sumSq);
        if (norm > 1e-10)
        {
            for (int i = 0; i < _dim; i++)
                direction[i] /= norm;
        }

        return direction;
    }
}
