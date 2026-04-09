using Microsoft.Extensions.AI;

namespace LMP.Samples.TicketTriage;

/// <summary>
/// A two-step LM program: classify a support ticket, then draft a reply.
/// Demonstrates predictor composition, assertions, and tracing.
/// </summary>
public partial class SupportTriageModule : LmpModule
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
    public override async Task<object> ForwardAsync(
        object input,
        CancellationToken cancellationToken = default)
    {
        var ticketInput = (TicketInput)input;

        // Step 1: Classify the ticket with assertion on urgency range
        var classification = await _classify.PredictAsync(
            ticketInput,
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
