using System.ComponentModel;

namespace LMP;

/// <summary>
/// Input type for the refiner predictor in a <see cref="Refine{TInput, TOutput}"/> loop.
/// Carries the original input and the previous attempt so the LM can critique and improve.
/// </summary>
/// <typeparam name="TOutput">The output type being refined.</typeparam>
/// <param name="OriginalInput">The original input that started the refinement chain.</param>
/// <param name="PreviousOutput">The previous attempt to improve upon.</param>
public record RefineCritiqueInput<TOutput>(
    [property: Description("The original input")]
    object OriginalInput,
    [property: Description("The previous attempt to improve upon")]
    TOutput PreviousOutput);
