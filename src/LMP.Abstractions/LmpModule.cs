using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LMP;

/// <summary>
/// Base class for composable LM programs. Subclass this and override
/// <see cref="ForwardAsync"/> to define multi-step LM logic.
/// </summary>
/// <remarks>
/// <see cref="LmpModule"/> implements <see cref="IOptimizationTarget"/> directly:
/// any subclass can be passed straight to an optimizer pipeline without an adapter.
/// </remarks>
public abstract class LmpModule : IOptimizationTarget
{
    /// <summary>
    /// The chat client used by <see cref="PredictAttribute"/>-decorated partial methods.
    /// Set this property in your constructor before calling any <c>[Predict]</c> methods.
    /// The source generator creates backing <see cref="Predictor{TInput, TOutput}"/> fields
    /// that are lazily initialized from this client.
    /// </summary>
    protected IChatClient? Client { get; set; }

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
    /// Returns all <see cref="SkillManifest"/> entries declared on this module via
    /// <see cref="SkillAttribute"/>-annotated methods.
    /// The source generator (Pipeline 8) emits this method for zero-reflection skill discovery.
    /// </summary>
    /// <returns>A list of skill manifests in declaration order.</returns>
    public virtual IReadOnlyList<SkillManifest> GetSkills()
        => [];

    /// <summary>
    /// Returns all <see cref="Microsoft.Extensions.AI.AIFunction"/> instances registered on this module
    /// via <see cref="ToolAttribute"/>-annotated methods.
    /// The source generator (Pipeline 7) emits this method for zero-reflection tool discovery.
    /// </summary>
    /// <returns>A read-only list of AI functions in declaration order.</returns>
    public virtual IReadOnlyList<AIFunction> GetTools()
        => [];

    /// <summary>
    /// Creates a deep copy of this module with independent predictor state.
    /// The returned module shares the same <c>IChatClient</c> bindings but has
    /// separate <c>Demos</c> and <c>Instructions</c> on every predictor.
    /// </summary>
    /// <typeparam name="TModule">The concrete module type.</typeparam>
    /// <returns>A deep-cloned module with independent learnable parameters.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when the source generator has not emitted a <c>CloneCore()</c> override
    /// for this module type. Ensure the module class is <c>partial</c>.
    /// </exception>
    public TModule Clone<TModule>() where TModule : LmpModule
    {
        var clone = CloneCore();
        clone.Trace = null;
        return (TModule)clone;
    }

    /// <summary>
    /// Creates a deep copy of this module. The concrete type is preserved at runtime.
    /// Used by optimizer implementations in <c>OptimizeAsync</c> when the concrete
    /// module type is not known at compile time.
    /// </summary>
    /// <returns>A deep-cloned module with independent learnable parameters.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when the source generator has not emitted a <c>CloneCore()</c> override.
    /// </exception>
    public LmpModule Clone()
    {
        var clone = CloneCore();
        clone.Trace = null;
        return clone;
    }

    /// <summary>
    /// Creates a deep copy of this module. Override this in source-generated code
    /// to clone all predictor fields with independent state.
    /// </summary>
    protected virtual LmpModule CloneCore()
        => throw new NotSupportedException(
            $"CloneCore() requires a source-generated override. " +
            $"Ensure '{GetType().Name}' is declared as a partial class.");

    /// <summary>
    /// Returns the current state of all predictors as a typed <see cref="ModuleState"/>.
    /// Use the <see cref="IOptimizationTarget.GetState"/> overload (returning
    /// <see cref="TargetState"/>) when you need to round-trip state through the
    /// optimization pipeline.
    /// </summary>
    public ModuleState GetModuleState()
        => new()
        {
            Version = "1.0",
            Module = GetType().Name,
            Predictors = GetPredictors().ToDictionary(
                p => p.Name,
                p => p.Predictor.GetState())
        };

    /// <summary>
    /// Applies a previously captured <see cref="ModuleState"/> to this module.
    /// Predictors not found in <paramref name="state"/> are left unchanged.
    /// </summary>
    public void ApplyState(ModuleState state)
    {
        foreach (var (name, predictor) in GetPredictors())
        {
            if (state.Predictors.TryGetValue(name, out var predictorState))
            {
                predictor.LoadState(predictorState);
            }
        }
    }

    /// <summary>
    /// Serializes all learnable parameters (demos, instructions, config) to a JSON file.
    /// Uses atomic write (temp file → rename) for safety.
    /// </summary>
    public virtual async Task SaveStateAsync(string path, CancellationToken cancellationToken = default)
    {
        var state = GetModuleState();

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            state,
            ModuleStateSerializerContext.Default.ModuleState);

        // Atomic write: temp file → rename.
        string tempPath = path + ".tmp";
        await File.WriteAllBytesAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Loads learnable parameters from a previously saved JSON file.
    /// Predictors not found in the file are left unchanged.
    /// Unknown JSON properties are silently ignored for forward compatibility.
    /// </summary>
    public virtual async Task ApplyStateAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);

        var state = JsonSerializer.Deserialize(
            bytes,
            ModuleStateSerializerContext.Default.ModuleState)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize module state from '{path}'.");

        ApplyState(state);
    }

    // ── IOptimizationTarget implementation ──────────────────────────────

    /// <inheritdoc />
    public TargetShape Shape => TargetShape.SingleTurn;

    /// <inheritdoc />
    /// <remarks>
    /// Sets <see cref="Trace"/> for the duration of the call so that <c>[Predict]</c>-decorated
    /// methods record into a fresh trace, then resets it in a finally block.
    /// </remarks>
    public async Task<(object Output, Trace Trace)> ExecuteAsync(
        object input,
        CancellationToken ct = default)
    {
        var trace = new Trace();
        Trace = trace;
        try
        {
            var output = await ForwardAsync(input, ct).ConfigureAwait(false);
            return (output, trace);
        }
        finally
        {
            Trace = null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// T1 returns <see cref="TypedParameterSpace.Empty"/>. The fractal predictor
    /// parameter aggregation lands in T2.
    /// </remarks>
    // T2: fractal predictor params land here.
    public TypedParameterSpace GetParameterSpace() => TypedParameterSpace.Empty;

    /// <inheritdoc />
    public TargetState GetState() => TargetState.From(GetModuleState());

    /// <inheritdoc />
    public void ApplyState(TargetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ApplyState(state.As<ModuleState>());
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Thrown when <paramref name="assignment"/> is non-empty. T2 introduces the
    /// fractal predictor parameter routing that makes per-parameter assignment work.
    /// </exception>
    public IOptimizationTarget WithParameters(ParameterAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (!assignment.IsEmpty)
            throw new NotSupportedException(
                "LmpModule.WithParameters: TypedParameterSpace assignment is implemented in T2 (fractal predictor params).");
        return Clone();
    }

    /// <inheritdoc />
    public T? GetService<T>() where T : class => this as T;
}
