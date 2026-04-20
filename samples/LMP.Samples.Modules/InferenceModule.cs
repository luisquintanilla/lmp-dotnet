using Microsoft.Extensions.AI;

namespace LMP.Samples.Modules;

/// <summary>
/// Demonstrates three inference-time strategies on the same task:
/// a single prediction, best-of-N parallel sampling, and iterative refinement.
/// </summary>
public partial class InferenceModule : LmpModule<TicketInput, DraftReply>
{
    // Pipeline 2 (PromptBuilder) fires for Predictor<,> fields.
    // Pipeline 3 (GetPredictors) fires for all three fields (walks inheritance).
    private readonly Predictor<TicketInput, DraftReply> _baseline;
    private readonly BestOfN<TicketInput, DraftReply> _bestOfN;
    private readonly Refine<TicketInput, DraftReply> _refine;

    public PredictionStrategy Strategy { get; set; } = PredictionStrategy.Baseline;

    public InferenceModule(IChatClient client)
    {
        _baseline = new Predictor<TicketInput, DraftReply>(client) { Name = "baseline" };
        _bestOfN  = new BestOfN<TicketInput, DraftReply>(client, n: 3, reward: KeywordReward) { Name = "best_of_n" };
        _refine   = new Refine<TicketInput, DraftReply>(client, maxIterations: 2) { Name = "refine" };
    }

    public override Task<DraftReply> ForwardAsync(
        TicketInput input,
        CancellationToken cancellationToken = default)
        => Strategy switch
        {
            PredictionStrategy.BestOfN => _bestOfN.PredictAsync(input, Trace, cancellationToken: cancellationToken),
            PredictionStrategy.Refine  => _refine.PredictAsync(input, Trace, cancellationToken: cancellationToken),
            _                          => _baseline.PredictAsync(input, Trace, cancellationToken: cancellationToken)
        };

    // Reward: score candidates by polite opening (fast heuristic for demo).
    private static float KeywordReward(TicketInput _, DraftReply reply) =>
        reply.ReplyText.StartsWith("Thank you", StringComparison.OrdinalIgnoreCase) ? 1f :
        reply.ReplyText.StartsWith("We apologize", StringComparison.OrdinalIgnoreCase) ? 0.8f :
        reply.ReplyText.Length > 50 ? 0.4f : 0f;
}

public enum PredictionStrategy { Baseline, BestOfN, Refine }
