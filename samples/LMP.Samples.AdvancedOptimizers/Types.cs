using System.ComponentModel;

namespace LMP.Samples.AdvancedOptimizers;

/// <summary>Input record for ticket classification.</summary>
public record TicketInput(
    [property: Description("The raw support ticket text")]
    string TicketText);

/// <summary>Ticket category enum — enforces valid categories via JSON Schema.</summary>
public enum TicketCategory { Billing, Technical, Account, General }

/// <summary>Classification output — category and urgency.</summary>
[LmpSignature("Classify a support ticket by category and urgency")]
public partial record ClassifyTicket
{
    [Description("The ticket category")]
    public required TicketCategory Category { get; init; }

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
