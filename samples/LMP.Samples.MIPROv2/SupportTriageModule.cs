using Microsoft.Extensions.AI;

namespace LMP.Samples.MIPROv2;

/// <summary>
/// A two-step LM program: classify a support ticket, then draft a reply.
/// Demonstrates predictor composition, assertions, and tracing.
/// </summary>
public partial class SupportTriageModule : LmpModule<TicketInput, DraftReply>
{
    private readonly Predictor<TicketInput, ClassifyTicket> _classify;
    private readonly Predictor<ClassifyTicket, DraftReply> _draft;

    /// <summary>
    /// Creates a new support triage module bound to the given chat client.
    /// </summary>
    /// <param name="client">The chat client for LM calls.</param>
    public SupportTriageModule(IChatClient client)
    {
        _classify = new Predictor<TicketInput, ClassifyTicket>(client) { Name = "classify" };
        _draft = new Predictor<ClassifyTicket, DraftReply>(client) { Name = "draft" };
    }

    /// <inheritdoc />
    public override async Task<DraftReply> ForwardAsync(
        TicketInput input,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Classify the ticket with assertion on urgency range
        var classification = await _classify.PredictAsync(
            input,
            trace: Trace,
            validate: result =>
                LmpAssert.That(result, c => c.Urgency >= 1 && c.Urgency <= 5,
                    "Urgency must be between 1 and 5"),
            cancellationToken: cancellationToken);

        // Step 2: Draft a reply based on classification
        var reply = await _draft.PredictAsync(
            classification,
            trace: Trace,
            cancellationToken: cancellationToken);

        return reply;
    }
}
