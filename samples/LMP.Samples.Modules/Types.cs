using System.ComponentModel;

namespace LMP.Samples.Modules;

// ── Support domain ────────────────────────────────────────────────────────────

public record TicketInput(
    [property: Description("The raw customer support ticket text")]
    string TicketText);

[LmpSignature("Write a helpful, professional reply to this customer support ticket")]
public partial record DraftReply(
    [property: Description(
        "The reply text to send to the customer. " +
        "Start with 'Thank you for reaching out.' and address the concern directly.")]
    string ReplyText);

// ── Math domain (for ProgramOfThought) ───────────────────────────────────────

public record MathProblem(
    [property: Description("The math expression to evaluate")]
    string Expression);
