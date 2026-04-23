using Microsoft.Extensions.AI;
using Microsoft.Z3;

namespace LMP.Extensions.Z3;

/// <summary>
/// Optimizer step that enforces structural constraints over an <see cref="AITool"/> pool
/// registered in <see cref="OptimizationContext.SearchSpace"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Z3FeasibilityOptimizer"/> is a <em>search space pruner</em>: it does not run
/// the target or consume training examples. Instead, it:
/// <list type="number">
/// <item><description>Reads a <see cref="Subset"/> parameter from <see cref="OptimizationContext.SearchSpace"/>.</description></item>
/// <item><description>Uses the Z3 SMT solver to determine which tools can appear in at least one valid subset.</description></item>
/// <item><description>Prunes the pool to only individually-feasible tools and tightens the min/max bounds.</description></item>
/// <item><description>Stores the feasible tool names in <see cref="OptimizationContext.Diagnostics"/>.<see cref="OptimizationDiagnostics.Snapshots"/> for downstream introspection.</description></item>
/// </list>
/// </para>
/// <para>
/// When placed early in an <see cref="OptimizationPipeline"/>, downstream optimizers such as
/// <c>MIPROv2</c> or <c>SIMBA</c> will only explore feasible regions of the tool space.
/// </para>
/// <para>Usage:
/// <code>
/// var pipeline = OptimizationPipeline.For(target)
///     .Use(new Z3FeasibilityOptimizer("tools")
///         .RequireAtLeastOne("search", "lookup")
///         .ExcludeCombination("search", "web_search"))
///     .Use(new MIPROv2(client));
/// </code>
/// </para>
/// </remarks>
public sealed class Z3FeasibilityOptimizer : IOptimizer
{
    /// <summary>
    /// Diagnostics snapshot key prefix where feasible tool name sets are stored.
    /// Full key: <c>"lmp.z3:feasible:{paramName}"</c>.
    /// The value is <see cref="IReadOnlySet{T}"/> of <see cref="string"/> (tool names).
    /// </summary>
    public const string BagKeyPrefix = "lmp.z3:feasible:";

    private readonly string _paramName;
    private readonly List<ToolConstraint> _constraints = [];

    /// <summary>
    /// Creates a <see cref="Z3FeasibilityOptimizer"/> for the named tool pool parameter.
    /// </summary>
    /// <param name="paramName">
    /// Name of the <see cref="Subset"/> parameter in <see cref="OptimizationContext.SearchSpace"/>.
    /// Defaults to <c>"tools"</c>.
    /// </param>
    public Z3FeasibilityOptimizer(string paramName = "tools")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paramName);
        _paramName = paramName;
    }

    // ── Fluent constraint builders ────────────────────────────────────────

    /// <summary>
    /// Adds a constraint requiring that at least one tool from <paramref name="toolNames"/>
    /// must be present in every valid selection.
    /// </summary>
    /// <param name="toolNames">One or more tool names. At least one must be selected.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="toolNames"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="toolNames"/> is empty.</exception>
    public Z3FeasibilityOptimizer RequireAtLeastOne(params string[] toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        if (toolNames.Length == 0)
            throw new ArgumentException("At least one tool name is required.", nameof(toolNames));
        _constraints.Add(new RequireAtLeastOneConstraint([.. toolNames]));
        return this;
    }

    /// <summary>
    /// Adds a mutual-exclusion constraint: <paramref name="toolA"/> and <paramref name="toolB"/>
    /// cannot both appear in the same selection.
    /// </summary>
    public Z3FeasibilityOptimizer ExcludeCombination(string toolA, string toolB)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolA);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolB);
        _constraints.Add(new ExcludeCombinationConstraint(toolA, toolB));
        return this;
    }

    // ── IOptimizer ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task OptimizeAsync(OptimizationContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!ctx.SearchSpace.Parameters.TryGetValue(_paramName, out var kind)
            || kind is not Subset subset)
            return Task.CompletedTask;

        var pool = ExtractToolNames(subset.Pool);
        if (pool.Count == 0)
            return Task.CompletedTask;

        int minSize = subset.MinSize;
        int maxSize = subset.MaxSize == -1 ? pool.Count : subset.MaxSize;

        // Clamp maxSize to pool count
        maxSize = Math.Min(maxSize, pool.Count);

        if (minSize > maxSize)
        {
            // Infeasible bounds — clear the pool to signal no valid selection exists
            ctx.Diagnostics.Snapshots[BagKeyPrefix + _paramName] = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);
            ctx.SearchSpace = ctx.SearchSpace.Remove(_paramName);
            return Task.CompletedTask;
        }

        // For each tool, check if it can appear in at least one feasible subset
        var feasibleTools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in pool)
        {
            ct.ThrowIfCancellationRequested();
            if (CanAppearInFeasibleSubset(name, pool, minSize, maxSize))
                feasibleTools.Add(name);
        }

        // Store feasible tools in diagnostics for downstream introspection
        ctx.Diagnostics.Snapshots[BagKeyPrefix + _paramName] = (IReadOnlySet<string>)feasibleTools;

        // Prune pool to individually feasible tools only
        var prunedPool = subset.Pool
            .Where(item => IsFeasibleItem(item, feasibleTools))
            .ToList();

        if (prunedPool.Count == 0)
        {
            ctx.SearchSpace = ctx.SearchSpace.Remove(_paramName);
            return Task.CompletedTask;
        }

        // Tighten bounds to pruned pool size
        int newMax = subset.MaxSize == -1 ? -1 : Math.Min(subset.MaxSize, prunedPool.Count);
        int newMin = Math.Min(subset.MinSize, prunedPool.Count);

        ctx.SearchSpace = ctx.SearchSpace
            .Remove(_paramName)
            .Add(_paramName, new Subset(prunedPool, newMin, newMax));

        return Task.CompletedTask;
    }

    // ── Z3 feasibility check ──────────────────────────────────────────────

    /// <summary>
    /// Uses Z3 to check whether <paramref name="targetTool"/> can appear in at least one
    /// valid subset of <paramref name="pool"/> that satisfies all registered constraints
    /// and the given size bounds.
    /// </summary>
    internal bool CanAppearInFeasibleSubset(
        string targetTool,
        IReadOnlyList<string> pool,
        int minSize,
        int maxSize)
    {
        using var z3Ctx = new Context(new Dictionary<string, string> { ["model"] = "true" });
        var solver = z3Ctx.MkSolver();

        // Boolean variable per tool
        var vars = new Dictionary<string, BoolExpr>(StringComparer.Ordinal);
        foreach (var name in pool)
            vars[name] = z3Ctx.MkBoolConst(name);

        // Force target tool to be selected
        if (!vars.TryGetValue(targetTool, out var targetVar))
            return false;
        solver.Add(targetVar);

        // Size constraint: minSize ≤ |selection| ≤ maxSize
        var selectionCounts = vars.Values
            .Select(v => (ArithExpr)z3Ctx.MkITE(v, z3Ctx.MkInt(1), z3Ctx.MkInt(0)))
            .ToArray();
        var total = z3Ctx.MkAdd(selectionCounts);
        solver.Add(z3Ctx.MkGe(total, z3Ctx.MkInt(minSize)));
        solver.Add(z3Ctx.MkLe(total, z3Ctx.MkInt(maxSize)));

        // User constraints
        foreach (var constraint in _constraints)
        {
            switch (constraint)
            {
                case RequireAtLeastOneConstraint req:
                    var options = req.ToolNames
                        .Where(n => vars.ContainsKey(n))
                        .Select(n => (BoolExpr)vars[n])
                        .ToArray();
                    if (options.Length == 0)
                        return false; // required tools not in pool → always infeasible
                    solver.Add(z3Ctx.MkOr(options));
                    break;

                case ExcludeCombinationConstraint excl:
                    if (vars.TryGetValue(excl.ToolA, out var va) &&
                        vars.TryGetValue(excl.ToolB, out var vb))
                        solver.Add(z3Ctx.MkNot(z3Ctx.MkAnd(va, vb)));
                    break;
            }
        }

        return solver.Check() == Status.SATISFIABLE;
    }

    /// <summary>
    /// Checks whether a selection of tool names is feasible under all registered constraints.
    /// Does not use Z3 — performs simple predicate evaluation.
    /// </summary>
    /// <param name="selection">Tool names to validate.</param>
    /// <returns><c>true</c> if the selection satisfies all constraints.</returns>
    public bool IsFeasible(IEnumerable<string> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var set = new HashSet<string>(selection, StringComparer.Ordinal);

        foreach (var constraint in _constraints)
        {
            switch (constraint)
            {
                case RequireAtLeastOneConstraint req:
                    if (!req.ToolNames.Any(t => set.Contains(t)))
                        return false;
                    break;

                case ExcludeCombinationConstraint excl:
                    if (set.Contains(excl.ToolA) && set.Contains(excl.ToolB))
                        return false;
                    break;
            }
        }

        return true;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static IReadOnlyList<string> ExtractToolNames(IReadOnlyList<object> pool)
        => pool
            .Select(item => item switch
            {
                AITool t => t.Name,
                string s => s,
                _ => null
            })
            .Where(n => n is not null)
            .ToList()!;

    private static bool IsFeasibleItem(object item, HashSet<string> feasibleNames)
    {
        var name = item switch
        {
            AITool t => t.Name,
            string s => s,
            _ => null
        };
        return name is not null && feasibleNames.Contains(name);
    }

    // ── Constraint types ──────────────────────────────────────────────────

    private abstract record ToolConstraint;

    private sealed record RequireAtLeastOneConstraint(
        IReadOnlyList<string> ToolNames) : ToolConstraint;

    private sealed record ExcludeCombinationConstraint(
        string ToolA,
        string ToolB) : ToolConstraint;
}
