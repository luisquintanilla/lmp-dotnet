namespace LMP;

/// <summary>
/// The five optimization axes — dimensions along which LM programs can be improved.
/// </summary>
public enum OptimizationAxis
{
    /// <summary>Prompt text and few-shot demonstration selection.</summary>
    Instructions,

    /// <summary>Tool pool selection and AIFunction description evolution.</summary>
    Tools,

    /// <summary>Skill routing and skill manifest optimization.</summary>
    Skills,

    /// <summary>Model selection, temperature, and other hyperparameters.</summary>
    Model,

    /// <summary>Multi-turn / agent trajectory quality.</summary>
    MultiTurn
}
