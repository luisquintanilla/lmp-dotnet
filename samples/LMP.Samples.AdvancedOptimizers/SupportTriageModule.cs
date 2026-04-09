using Microsoft.Extensions.AI;

namespace LMP.Samples.AdvancedOptimizers;

/// <summary>Two-step LM program: classify a support ticket, then draft a reply.</summary>
public partial class SupportTriageModule : LmpModule<TicketInput, DraftReply>
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply> _draft;

    public SupportTriageModule(IChatClient client)
    {
        _classify = new Predictor<TicketInput, ClassifyTicket>(client) { Name = "classify" };
        _draft = new Predictor<ClassifyTicket, DraftReply>(client) { Name = "draft" };
    }

    public override async Task<DraftReply> ForwardAsync(
        TicketInput input,
        CancellationToken cancellationToken = default)
    {
        var classification = await _classify.PredictAsync(
            input,
            trace: Trace,
            validate: result =>
                LmpAssert.That(result, c => c.Urgency >= 1 && c.Urgency <= 5,
                    "Urgency must be between 1 and 5"),
            cancellationToken: cancellationToken);

        var reply = await _draft.PredictAsync(
            classification,
            trace: Trace,
            cancellationToken: cancellationToken);

        return reply;
    }
}
