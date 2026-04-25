namespace LMP.Optimizers;

/// <summary>
/// Converts numeric (<see cref="Continuous"/>, <see cref="Integer"/>) and
/// <see cref="Categorical"/> parameters into a categorical index space compatible
/// with <see cref="CategoricalTpeSampler"/>, and provides decode/encode round-trips.
/// </summary>
internal sealed class ContinuousDiscretizer
{
    private abstract record GridEntry(string Name, int Cardinality);

    private sealed record ContGrid(string Name, double[] Grid)
        : GridEntry(Name, Grid.Length);

    private sealed record IntGrid(string Name, int[] Grid)
        : GridEntry(Name, Grid.Length);

    private sealed record CatGrid(string Name, int Count)
        : GridEntry(Name, Count);

    private readonly Dictionary<string, GridEntry> _entries;

    /// <summary>
    /// Maps parameter name → number of discrete steps for use as
    /// <see cref="CategoricalTpeSampler"/> cardinalities.
    /// </summary>
    public Dictionary<string, int> Cardinalities { get; }

    private ContinuousDiscretizer(Dictionary<string, GridEntry> entries)
    {
        _entries = entries;
        Cardinalities = entries.ToDictionary(kv => kv.Key, kv => kv.Value.Cardinality);
    }

    /// <summary>
    /// Creates a <see cref="ContinuousDiscretizer"/> from a <see cref="TypedParameterSpace"/>.
    /// Only <see cref="Continuous"/>, <see cref="Integer"/>, and <see cref="Categorical"/>
    /// parameters are processed; <see cref="StringValued"/>, <see cref="Subset"/>, and
    /// <see cref="Composite"/> are silently skipped.
    /// </summary>
    /// <param name="space">The parameter space to discretize.</param>
    /// <param name="continuousSteps">
    /// Number of grid points for <see cref="Continuous"/> parameters.
    /// Must be ≥ 2.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="continuousSteps"/> is less than 2.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a <see cref="Scale.Log"/>-scaled <see cref="Continuous"/> parameter
    /// has <c>Min</c> ≤ 0 or <c>Max</c> ≤ 0.
    /// </exception>
    public static ContinuousDiscretizer From(TypedParameterSpace space, int continuousSteps = 8)
    {
        ArgumentNullException.ThrowIfNull(space);
        if (continuousSteps < 2)
            throw new ArgumentOutOfRangeException(
                nameof(continuousSteps), continuousSteps,
                "continuousSteps must be >= 2.");

        var entries = new Dictionary<string, GridEntry>();

        foreach (var (name, kind) in space.Parameters)
        {
            switch (kind)
            {
                case Continuous cont:
                    entries[name] = new ContGrid(name, BuildContinuousGrid(cont, continuousSteps));
                    break;

                case Integer integer:
                    entries[name] = new IntGrid(name, BuildIntegerGrid(integer));
                    break;

                case Categorical cat:
                    entries[name] = new CatGrid(name, cat.Count);
                    break;

                // StringValued, Subset, Composite — skip
            }
        }

        return new ContinuousDiscretizer(entries);
    }

    /// <summary>
    /// Decodes a categorical-index configuration to actual typed values.
    /// <list type="bullet">
    /// <item><see cref="Continuous"/> parameters → <c>double</c> grid value.</item>
    /// <item><see cref="Integer"/> parameters → <c>int</c> grid value.</item>
    /// <item><see cref="Categorical"/> parameters → <c>int</c> index.</item>
    /// </list>
    /// </summary>
    /// <param name="catConfig">
    /// Categorical-index configuration as returned by <see cref="CategoricalTpeSampler.Propose"/>.
    /// </param>
    public ParameterAssignment Decode(Dictionary<string, int> catConfig)
    {
        ArgumentNullException.ThrowIfNull(catConfig);
        var result = ParameterAssignment.Empty;

        foreach (var (name, idx) in catConfig)
        {
            if (!_entries.TryGetValue(name, out var entry))
                continue;

            result = entry switch
            {
                ContGrid cg => result.With(name, (object)cg.Grid[idx]),
                IntGrid ig => result.With(name, (object)ig.Grid[idx]),
                CatGrid => result.With(name, (object)idx),
                _ => result
            };
        }

        return result;
    }

    /// <summary>
    /// Encodes an actual-value assignment back to the nearest categorical indices.
    /// Used to update the sampler after evaluating a decoded assignment.
    /// </summary>
    /// <param name="assignment">The assignment to encode.</param>
    public Dictionary<string, int> Encode(ParameterAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var result = new Dictionary<string, int>();

        foreach (var (name, entry) in _entries)
        {
            switch (entry)
            {
                case ContGrid cg when assignment.TryGet<double>(name, out var dval):
                    result[name] = NearestIndex(cg.Grid, dval);
                    break;

                case IntGrid ig when assignment.TryGet<int>(name, out var ival):
                    result[name] = NearestIntIndex(ig.Grid, ival);
                    break;

                case CatGrid when assignment.TryGet<int>(name, out var catIdx):
                    result[name] = catIdx;
                    break;
            }
        }

        return result;
    }

    // ── Grid builders ────────────────────────────────────────────────────

    private static double[] BuildContinuousGrid(Continuous cont, int steps)
    {
        if (cont.Scale == Scale.Log)
        {
            if (cont.Min <= 0 || cont.Max <= 0)
                throw new ArgumentException(
                    $"Log-scale Continuous parameter requires Min > 0 and Max > 0, " +
                    $"but got Min={cont.Min}, Max={cont.Max}.");

            double logMin = Math.Log(cont.Min);
            double logMax = Math.Log(cont.Max);
            var grid = new double[steps];
            for (int i = 0; i < steps; i++)
            {
                double t = (double)i / (steps - 1);
                grid[i] = Math.Exp(logMin + (logMax - logMin) * t);
            }
            return grid;
        }
        else
        {
            var grid = new double[steps];
            for (int i = 0; i < steps; i++)
            {
                double t = (double)i / (steps - 1);
                grid[i] = cont.Min + (cont.Max - cont.Min) * t;
            }
            return grid;
        }
    }

    private static int[] BuildIntegerGrid(Integer integer)
    {
        int totalRange = integer.Max - integer.Min + 1;
        int numSteps = Math.Min(totalRange, 20);

        var candidates = new int[numSteps];
        for (int i = 0; i < numSteps; i++)
        {
            double t = numSteps == 1 ? 0.0 : (double)i / (numSteps - 1);
            candidates[i] = (int)Math.Round(integer.Min + (integer.Max - integer.Min) * t);
        }

        return candidates.Distinct().ToArray();
    }

    // ── Nearest-index helpers ────────────────────────────────────────────

    private static int NearestIndex(double[] grid, double value)
    {
        int best = 0;
        double bestDist = Math.Abs(grid[0] - value);
        for (int i = 1; i < grid.Length; i++)
        {
            double d = Math.Abs(grid[i] - value);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    private static int NearestIntIndex(int[] grid, int value)
    {
        int best = 0;
        int bestDist = Math.Abs(grid[0] - value);
        for (int i = 1; i < grid.Length; i++)
        {
            int d = Math.Abs(grid[i] - value);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }
}
