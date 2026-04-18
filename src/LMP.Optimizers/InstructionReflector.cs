using System.Text;
using Microsoft.Extensions.AI;

namespace LMP.Optimizers;

/// <summary>
/// Shared helper for LLM-driven instruction improvement.
/// Used by <see cref="GEPA"/> and <see cref="SIMBA"/> to propose better predictor
/// instructions from failure traces.
/// </summary>
internal static class InstructionReflector
{
    /// <summary>
    /// Asks the reflection LLM to analyze failures for a specific predictor
    /// and propose an improved instruction. Uses predictor-specific trace I/O
    /// rather than the full module output to avoid cross-task confusion.
    /// Returns an empty string if no diagnosable failures are provided or if the
    /// LLM call fails.
    /// </summary>
    /// <param name="reflectionClient">LLM client for reflection.</param>
    /// <param name="predictorName">Name of the predictor to improve.</param>
    /// <param name="currentInstruction">The predictor's current instruction.</param>
    /// <param name="failedTraces">Failure trace data from the mini-batch.</param>
    /// <param name="externalObservations">
    /// Optional global critique entries from <see cref="ReflectionLog"/>
    /// (e.g., produced by <c>EvaluationCritique</c>). These are prepended as additional
    /// context but don't replace failure-trace analysis.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task<string> ReflectAsync(
        IChatClient reflectionClient,
        string predictorName,
        string currentInstruction,
        IEnumerable<(Example Example, object Output, float Score, Trace Trace)> failedTraces,
        CancellationToken cancellationToken,
        IReadOnlyList<ReflectionEntry>? externalObservations = null)
    {
        var diagnosable = failedTraces
            .Where(r => r.Trace.Entries.Any(e => e.PredictorName == predictorName))
            .Take(5)
            .ToList();

        if (diagnosable.Count == 0)
            return "";

        var prompt = new StringBuilder();
        prompt.AppendLine($"You are improving the '{predictorName}' predictor in a multi-predictor LM pipeline.");
        prompt.AppendLine($"Current instruction: \"{currentInstruction}\"");
        prompt.AppendLine();

        // Include global critique context (from EvaluationCritique or similar)
        var relevantObservations = externalObservations?
            .Where(e => e.Scope == ReflectionScope.Global ||
                        (e.Scope == ReflectionScope.Predictor && e.PredictorName == predictorName))
            .Take(3)
            .ToList();

        if (relevantObservations is { Count: > 0 })
        {
            prompt.AppendLine("External evaluation observations:");
            foreach (var obs in relevantObservations)
                prompt.AppendLine($"  - {obs.Text}");
            prompt.AppendLine();
        }

        prompt.AppendLine($"This predictor has ONE specific job: classify the '{predictorName}' of the input.");
        prompt.AppendLine("Other sub-tasks are handled by separate predictors — do NOT include them in this instruction.");
        prompt.AppendLine();
        prompt.AppendLine("Examples where this predictor contributed to errors:");
        prompt.AppendLine();

        int shown = 0;
        foreach (var (example, _, score, trace) in diagnosable)
        {
            shown++;
            var entries = trace.Entries.Where(e => e.PredictorName == predictorName).ToList();
            if (entries.Count == 0) continue;

            prompt.AppendLine($"--- Example {shown} (combined module score: {score:F2}) ---");
            foreach (var entry in entries)
            {
                prompt.AppendLine($"  Input:    {entry.Input}");
                prompt.AppendLine($"  Produced: {entry.Output}");
            }
            prompt.AppendLine($"  Full expected: {example.GetLabel()}");
            prompt.AppendLine();
        }

        prompt.AppendLine($"Write an improved instruction for the '{predictorName}' predictor.");
        prompt.AppendLine();
        prompt.AppendLine("CRITICAL RULES — violation breaks the pipeline:");
        prompt.AppendLine("  1. Output ONLY the instruction text — no explanation, no preamble");
        prompt.AppendLine("  2. Do NOT describe output format, JSON, or field names");
        prompt.AppendLine("  3. Do NOT instruct the predictor to output more than one field");
        prompt.AppendLine($"  4. Focus exclusively on '{predictorName}' — ignore other sub-tasks");

        var response = await reflectionClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System,
                $"You are an expert prompt engineer. You are improving a SINGLE predictor called '{predictorName}' " +
                "in a multi-task pipeline. The predictor's output schema is fixed and enforced automatically — " +
                "never include output format instructions. Write a focused, concise instruction that helps the " +
                $"predictor classify '{predictorName}' more accurately. Output ONLY the instruction text."),
            new ChatMessage(ChatRole.User, prompt.ToString())
        ],
        cancellationToken: cancellationToken);

        return response.Text?.Trim() ?? "";
    }

    /// <summary>
    /// Runs a module on a mini-batch sequentially, capturing per-example traces for
    /// failure diagnosis. Sets a fresh <see cref="Trace"/> before each example to
    /// ensure trace isolation. When <paramref name="trajectoryMetric"/> is provided,
    /// examples are scored using <see cref="ITrajectoryMetric.ScoreAsync"/> instead of
    /// <paramref name="metric"/>; traces are still captured for reflection.
    /// </summary>
    internal static async Task<List<(Example Example, object Output, float Score, Trace Trace)>> RunWithTracesAsync(
        LmpModule module,
        IEnumerable<Example> batch,
        Func<Example, object, float> metric,
        CancellationToken ct,
        ITrajectoryMetric? trajectoryMetric = null)
    {
        var results = new List<(Example, object, float, Trace)>();

        foreach (var example in batch)
        {
            var trace = new Trace();
            module.Trace = trace;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var output = await module.ForwardAsync(example.WithInputs(), ct).ConfigureAwait(false);
                    float score;
                    if (trajectoryMetric != null)
                    {
                        var traj = Trajectory.FromTrace(trace, example);
                        score = await trajectoryMetric.ScoreAsync(traj, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        score = metric(example, output);
                    }
                    results.Add((example, output, score, trace));
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch when (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << (attempt + 1)), ct).ConfigureAwait(false);
                }
                catch
                {
                    results.Add((example, "error", 0f, trace));
                    break;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Samples a random mini-batch from the training set.
    /// </summary>
    internal static List<Example> SampleMiniBatch(IReadOnlyList<Example> trainSet, Random rng, int size)
    {
        int actualSize = Math.Min(size, trainSet.Count);
        return Enumerable.Range(0, trainSet.Count)
            .OrderBy(_ => rng.Next())
            .Take(actualSize)
            .Select(i => trainSet[i])
            .ToList();
    }
}
