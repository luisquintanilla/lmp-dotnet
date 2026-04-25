namespace LMP.Optimizers;

/// <summary>
/// Internal helper that enumerates <c>(Path, IPredictor)</c> pairs across
/// supported <see cref="IOptimizationTarget"/> composites. Paths match the
/// fully-qualified names stored in <see cref="TraceEntry.PredictorName"/>
/// by <see cref="LmpModule"/> (raw predictor name), <see cref="ChainTarget"/>
/// (<c>"child_{i}.{inner}"</c>) and <see cref="Pipeline{TIn,TOut}"/>
/// (delegates to its internal chain via identical prefix), so a path yielded
/// here can be used to both filter traces and build a
/// <see cref="ParameterAssignment"/> routed by the fractal
/// <c>WithParameters</c> seam.
/// </summary>
/// <remarks>
/// Kept internal: public predictor-enumeration on <see cref="IOptimizationTarget"/>
/// is a design decision deferred until a broader audit. Optimizers that need
/// per-predictor access today use this walker.
/// </remarks>
internal static class PredictorWalker
{
    /// <summary>
    /// Yields <c>(path, predictor)</c> pairs for each leaf <see cref="IPredictor"/>
    /// reachable from <paramref name="target"/>. Returns an empty sequence when
    /// the target kind is not a supported composite (safe default; callers must
    /// treat empty as "no predictors to enumerate").
    /// </summary>
    internal static IEnumerable<(string Path, IPredictor Predictor)> Enumerate(IOptimizationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target is LmpModule module)
        {
            foreach (var (name, predictor) in module.GetPredictors())
                yield return (name, predictor);
            yield break;
        }

        if (target is ChainTarget chain)
        {
            for (int i = 0; i < chain.Targets.Count; i++)
            {
                foreach (var (path, predictor) in Enumerate(chain.Targets[i]))
                    yield return ($"child_{i}.{path}", predictor);
            }
            yield break;
        }

        // Pipeline<TIn, TOut> exposes its stages via IEnumerable<IOptimizationTarget>
        // and applies the same "child_{i}." prefix as ChainTarget through its internal chain.
        if (target is IEnumerable<IOptimizationTarget> stages
            && target.GetType() is { IsGenericType: true } t
            && t.GetGenericTypeDefinition() == typeof(Pipeline<,>))
        {
            int i = 0;
            foreach (var stage in stages)
            {
                foreach (var (path, predictor) in Enumerate(stage))
                    yield return ($"child_{i}.{path}", predictor);
                i++;
            }
            yield break;
        }

        // Unknown composite kinds: yield nothing. GEPA treats this as a no-op iteration.
    }
}
