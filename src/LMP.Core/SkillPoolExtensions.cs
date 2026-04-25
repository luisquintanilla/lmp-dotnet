namespace LMP;

/// <summary>
/// Extension methods for registering <see cref="SkillManifest"/> pools in optimization contexts.
/// </summary>
/// <remarks>
/// <para>
/// A skill pool is an ordered list of <see cref="SkillManifest"/> candidates. It is registered
/// in <see cref="TypedParameterSpace"/> as a <see cref="Subset"/> parameter, allowing optimizers
/// such as <c>ContextualBandit</c> to learn which skill subsets perform best.
/// </para>
/// <para>
/// Skills are discoverable via <see cref="LmpModule.GetSkills()"/> when a module class is
/// annotated with <c>[Skill]</c> methods and compiled with the LMP source generator.
/// </para>
/// </remarks>
public static class SkillPoolExtensions
{
    /// <summary>
    /// Returns a new <see cref="TypedParameterSpace"/> with a skill pool registered as a
    /// <see cref="Subset"/> parameter under <paramref name="paramName"/>.
    /// </summary>
    /// <param name="space">The parameter space to extend.</param>
    /// <param name="skills">The skill pool. All items are initially in the pool.</param>
    /// <param name="paramName">The parameter name (default: <c>"skills"</c>).</param>
    /// <param name="minSize">Minimum number of skills a valid subset must contain (default: 1).</param>
    /// <param name="maxSize">
    /// Maximum number of skills in a valid subset.
    /// <c>-1</c> (default) means no upper bound — all pool skills may be selected.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="space"/> or <paramref name="skills"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="minSize"/> is less than 0 or <paramref name="maxSize"/> is less than -1.
    /// </exception>
    public static TypedParameterSpace AddSkillPool(
        this TypedParameterSpace space,
        IReadOnlyList<SkillManifest> skills,
        string paramName = "skills",
        int minSize = 1,
        int maxSize = -1)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentOutOfRangeException.ThrowIfLessThan(minSize, 0);
        if (maxSize != -1)
            ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, 0);

        return space.Add(paramName, new Subset<SkillManifest>(TypedPool: [.. skills], minSize, maxSize));
    }

    /// <summary>
    /// Registers a skill pool in <see cref="OptimizationContext.SearchSpace"/> and returns the context.
    /// </summary>
    /// <param name="ctx">The optimization context to update.</param>
    /// <param name="skills">The skill pool.</param>
    /// <param name="paramName">The parameter name (default: <c>"skills"</c>).</param>
    /// <param name="minSize">Minimum subset size (default: 1).</param>
    /// <param name="maxSize">Maximum subset size, or <c>-1</c> for unbounded (default).</param>
    /// <returns>The same <paramref name="ctx"/> for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="ctx"/> or <paramref name="skills"/> is null.
    /// </exception>
    public static OptimizationContext WithSkillPool(
        this OptimizationContext ctx,
        IReadOnlyList<SkillManifest> skills,
        string paramName = "skills",
        int minSize = 1,
        int maxSize = -1)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.SearchSpace = ctx.SearchSpace.AddSkillPool(skills, paramName, minSize, maxSize);
        return ctx;
    }
}
