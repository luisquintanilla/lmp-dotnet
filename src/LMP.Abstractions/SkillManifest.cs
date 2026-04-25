namespace LMP;

/// <summary>
/// Describes a named skill capability on an <see cref="LmpModule"/> subclass.
/// Produced by <see cref="LmpModule.GetSkills()"/> (source-gen override) and consumed
/// by optimizers such as <c>ContextualBandit</c> for skill routing and selection.
/// </summary>
/// <param name="Name">
/// Stable skill identifier. Matches the method name or the <c>[Skill(Name = ...)]</c> override.
/// </param>
/// <param name="Description">
/// Human-readable description of the skill's capability.
/// Null when no description is available; GEPA can evolve this value.
/// </param>
/// <param name="Tags">
/// Optional tags for grouping related skills and expressing compatibility constraints.
/// An empty list means no tags are defined.
/// </param>
public sealed record SkillManifest(
    string Name,
    string? Description = null,
    IReadOnlyList<string>? Tags = null)
{
    /// <summary>
    /// A <see cref="SkillManifest"/> with no tags. Convenience alias for
    /// <c>new SkillManifest(name)</c>.
    /// </summary>
    public static SkillManifest For(string name, string? description = null)
        => new(name, description);
}
