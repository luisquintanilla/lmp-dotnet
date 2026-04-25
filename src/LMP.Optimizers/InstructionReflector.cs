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
    /// <param name="predictorPath">
    /// Fully-qualified path of the predictor to improve, matching the
    /// <see cref="TraceEntry.PredictorName"/> written by the target. For bare
    /// <see cref="LmpModule"/>s this is the raw predictor name; for composites
    /// (e.g., <see cref="ChainTarget"/>) it is the composite-prefixed path
    /// (e.g., <c>"child_0.classify"</c>).
    /// </param>
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
        string predictorPath,
        string currentInstruction,
        IEnumerable<(Example Example, object Output, float Score, Trace Trace)> failedTraces,
        CancellationToken cancellationToken,
        IReadOnlyList<ReflectionEntry>? externalObservations = null)
    {
        var diagnosable = failedTraces
            .Where(r => r.Trace.Entries.Any(e => e.PredictorName == predictorPath))
            .Take(5)
            .ToList();

        if (diagnosable.Count == 0)
            return "";

        var prompt = new StringBuilder();
        prompt.AppendLine($"You are improving the '{predictorPath}' predictor in a multi-predictor LM pipeline.");
        prompt.AppendLine($"Current instruction: \"{currentInstruction}\"");
        prompt.AppendLine();

        // Include global critique context (from EvaluationCritique or similar)
        var relevantObservations = externalObservations?
            .Where(e => e.Scope == ReflectionScope.Global ||
                        (e.Scope == ReflectionScope.Predictor && e.PredictorName == predictorPath))
            .Take(3)
            .ToList();

        if (relevantObservations is { Count: > 0 })
        {
            prompt.AppendLine("External evaluation observations:");
            foreach (var obs in relevantObservations)
                prompt.AppendLine($"  - {obs.Text}");
            prompt.AppendLine();
        }

        prompt.AppendLine($"This predictor has ONE specific job: classify the '{predictorPath}' of the input.");
        prompt.AppendLine("Other sub-tasks are handled by separate predictors — do NOT include them in this instruction.");
        prompt.AppendLine();
        prompt.AppendLine("Examples where this predictor contributed to errors:");
        prompt.AppendLine();

        int shown = 0;
        foreach (var (example, _, score, trace) in diagnosable)
        {
            shown++;
            var entries = trace.Entries.Where(e => e.PredictorName == predictorPath).ToList();
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

        prompt.AppendLine($"Write an improved instruction for the '{predictorPath}' predictor.");
        prompt.AppendLine();
        prompt.AppendLine("CRITICAL RULES — violation breaks the pipeline:");
        prompt.AppendLine("  1. Output ONLY the instruction text — no explanation, no preamble");
        prompt.AppendLine("  2. Do NOT describe output format, JSON, or field names");
        prompt.AppendLine("  3. Do NOT instruct the predictor to output more than one field");
        prompt.AppendLine($"  4. Focus exclusively on '{predictorPath}' — ignore other sub-tasks");

        var response = await reflectionClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System,
                $"You are an expert prompt engineer. You are improving a SINGLE predictor called '{predictorPath}' " +
                "in a multi-task pipeline. The predictor's output schema is fixed and enforced automatically — " +
                "never include output format instructions. Write a focused, concise instruction that helps the " +
                $"predictor classify '{predictorPath}' more accurately. Output ONLY the instruction text."),
            new ChatMessage(ChatRole.User, prompt.ToString())
        ],
        cancellationToken: cancellationToken);

        return response.Text?.Trim() ?? "";
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
