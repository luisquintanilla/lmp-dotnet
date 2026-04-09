using System.ComponentModel;

namespace LMP.Samples.Middleware;

/// <summary>Input: raw support ticket text.</summary>
public record TicketInput(
    [property: Description("The raw support ticket text")]
    string TicketText);

/// <summary>Intermediate: classification result.</summary>
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Description("Category: billing, technical, account, general")]
    public required string Category { get; init; }

    [Description("Urgency from 1 (low) to 5 (critical)")]
    public required int Urgency { get; init; }
}

/// <summary>Output: customer-facing reply.</summary>
[LmpSignature("Draft a helpful reply to the customer based on the ticket classification")]
public partial record DraftReply
{
    [Description("The reply text to send to the customer")]
    public required string ReplyText { get; init; }
}
