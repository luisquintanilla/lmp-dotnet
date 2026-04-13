using Microsoft.Extensions.AI;

namespace LMP.Samples.MathReasoning;

/// <summary>
/// A chain-of-thought math reasoning module.
/// Uses ChainOfThought to make the LM reason step by step before answering.
/// Source generator emits GetPredictors() and CloneCore().
/// </summary>
public partial class MathReasoningModule : LmpModule<MathInput, MathAnswer>
{
    private readonly ChainOfThought<MathInput, MathAnswer> _solve;

    /// <summary>Creates a new math reasoning module.</summary>
    /// <param name="client">The chat client for LM calls.</param>
    public MathReasoningModule(IChatClient client)
    {
        _solve = new ChainOfThought<MathInput, MathAnswer>(client) { Name = "solve" };
    }

    /// <inheritdoc />
    public override async Task<MathAnswer> ForwardAsync(
        MathInput input,
        CancellationToken cancellationToken = default)
    {
        var answer = await _solve.PredictAsync(
            input,
            trace: Trace,
            validate: result =>
                LmpAssert.That(result,
                    r => !string.IsNullOrWhiteSpace(r.Answer),
                    "Answer must not be empty"),
            maxRetries: 2,
            cancellationToken: cancellationToken);

        return answer;
    }
}
