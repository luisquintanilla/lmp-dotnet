using System.ComponentModel;

namespace LMP.Samples.AdvancedOptimizers;

/// <summary>Input record for ticket classification.</summary>
public record TicketInput(
    [property: Description("The raw support ticket text")]
    string TicketText);

/// <summary>Classification output — category and urgency.</summary>
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Description("Category: billing, technical, account, general")]
    public required string Category { get; init; }

    [Description("Urgency from 1 (low) to 5 (critical)")]
    public required int Urgency { get; init; }
}

/// <summary>Draft reply output — a customer-facing response.</summary>
[LmpSignature("Draft a helpful reply to the customer based on the ticket classification")]
public partial record DraftReply
{
    [Description("The reply text to send to the customer")]
    public required string ReplyText { get; init; }
}
