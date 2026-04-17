using System.ComponentModel;

namespace LMP.Samples.TicketTriage;

/// <summary>
/// Ticket category enum. Using a C# enum enforces valid categories at the schema
/// level — the LLM is constrained to output exactly one of these values.
/// This is the C# equivalent of DSPy's <c>typing.Literal</c>.
/// </summary>
public enum TicketCategory { Billing, Technical, Account, General }

/// <summary>
/// Input record for ticket classification. The raw support ticket text.
/// </summary>
/// <param name="TicketText">The raw support ticket text to classify.</param>
public record TicketInput(
    [property: Description("The raw support ticket text")]
    string TicketText);

/// <summary>
/// Output of ticket classification — category and urgency.
/// The source generator reads field descriptions to build the LM prompt.
/// </summary>
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    /// <summary>Category of the ticket.</summary>
    [Description("The ticket category")]
    public required TicketCategory Category { get; init; }

    /// <summary>Urgency level of the ticket.</summary>
    [Description("Urgency from 1 (low) to 5 (critical)")]
    public required int Urgency { get; init; }
}

/// <summary>
/// Output of the reply drafting step — a customer-facing response.
/// </summary>
[LmpSignature("Draft a helpful reply to the customer based on the ticket classification")]
public partial record DraftReply
{
    /// <summary>The reply text to send to the customer.</summary>
    [Description("The reply text to send to the customer")]
    public required string ReplyText { get; init; }
}
