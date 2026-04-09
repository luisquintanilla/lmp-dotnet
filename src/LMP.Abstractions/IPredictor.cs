using System.Collections;
using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// Non-generic predictor interface for optimizer enumeration.
/// Exposes learnable state (instructions, demos, config) without
/// requiring knowledge of <c>TInput</c>/<c>TOutput</c> at compile time.
/// </summary>
public interface IPredictor
{
    /// <summary>
    /// The predictor's name, used in traces and artifact serialization.
    /// Set by the source generator to the field name in the containing module.
    /// </summary>
    string Name { get; set; }

    /// <summary>
    /// Task instructions. Defaults to the <see cref="LmpSignatureAttribute"/> instructions.
    /// Can be overridden by optimizers.
    /// </summary>
    string Instructions { get; set; }

    /// <summary>
    /// Few-shot demonstration examples as a non-generic list.
    /// The concrete type is <c>List&lt;(TInput, TOutput)&gt;</c>.
    /// </summary>
    IList Demos { get; }

    /// <summary>
    /// Predictor-level configuration overrides (temperature, max tokens, etc.).
    /// </summary>
    ChatOptions Config { get; set; }

    /// <summary>
    /// Captures the predictor's current learnable state for serialization.
    /// </summary>
    PredictorState GetState();

    /// <summary>
    /// Restores the predictor's learnable state from a previously saved state.
    /// </summary>
    void LoadState(PredictorState state);

    /// <summary>
    /// Creates an independent copy of this predictor with the same client binding
    /// but separate learnable state (Demos, Instructions, Config).
    /// Used by optimizers that need teacher/student separation.
    /// </summary>
    IPredictor Clone();
}
