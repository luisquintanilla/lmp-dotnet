namespace LMP;

/// <summary>
/// Base class for composable LM programs. Subclass this and override
/// <see cref="ForwardAsync"/> to define multi-step LM logic.
/// </summary>
public abstract class LmpModule
{
    /// <summary>
    /// Active trace for recording predictor invocations during execution.
    /// Set by optimizers before running training examples.
    /// </summary>
    public Trace? Trace { get; set; }

    /// <summary>
    /// Defines the module's execution logic. Override this to compose
    /// predictors, assertions, and other modules.
    /// </summary>
    /// <param name="input">The input to the module.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The module's output.</returns>
    public abstract Task<object> ForwardAsync(
        object input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all <see cref="IPredictor"/> instances in this module.
    /// The source generator emits this method for zero-reflection predictor discovery.
    /// </summary>
    /// <returns>A list of (name, predictor) pairs.</returns>
    public virtual IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
        => [];

    /// <summary>
    /// Serializes all learnable parameters (demos, instructions, config) to a JSON file.
    /// </summary>
    /// <param name="path">The file path to write the artifact to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public virtual Task SaveAsync(string path, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("SaveAsync will be implemented in Phase 4.");

    /// <summary>
    /// Loads learnable parameters from a previously saved JSON file.
    /// </summary>
    /// <param name="path">The file path to read the artifact from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public virtual Task LoadAsync(string path, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("LoadAsync will be implemented in Phase 4.");
}
