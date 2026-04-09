using Microsoft.Extensions.AI;

namespace LMP.Samples.Rag;

/// <summary>
/// Input to the predictor inside the RAG module: the question plus retrieved passages.
/// </summary>
public record AugmentedInput(string Question, string[] Passages)
{
    public override string ToString()
        => $"Question: {Question}\n\nContext:\n{string.Join("\n\n", Passages)}";
}

/// <summary>
/// A RAG (Retrieval-Augmented Generation) module that retrieves relevant
/// passages and uses them as context for answering questions.
///
/// Flow: question → retrieve top-K passages → predict answer with context.
/// The source generator emits GetPredictors() and CloneCore().
/// </summary>
public partial class RagModule : LmpModule<QuestionInput, AnswerOutput>
{
    private readonly Predictor<AugmentedInput, AnswerOutput> _answer;
    private readonly IRetriever _retriever;
    private readonly int _topK;

    /// <summary>
    /// Creates a RAG module that retrieves context then predicts an answer.
    /// </summary>
    /// <param name="client">The chat client for LM calls.</param>
    /// <param name="retriever">The retriever for fetching context passages.</param>
    /// <param name="topK">Number of passages to retrieve (default: 3).</param>
    public RagModule(IChatClient client, IRetriever retriever, int topK = 3)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);

        _retriever = retriever;
        _topK = topK;
        _answer = new Predictor<AugmentedInput, AnswerOutput>(client)
        {
            Name = "answer"
        };
    }

    /// <inheritdoc />
    public override async Task<AnswerOutput> ForwardAsync(
        QuestionInput input,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Retrieve relevant passages
        var passages = await _retriever.RetrieveAsync(
            input.Question, _topK, cancellationToken);

        // Step 2: Build augmented input with retrieved context
        var augmented = new AugmentedInput(input.Question, passages);

        // Step 3: Predict answer using context
        var result = await _answer.PredictAsync(
            augmented,
            trace: Trace,
            validate: r =>
                LmpAssert.That(r, x => x.Confidence >= 0f && x.Confidence <= 1f,
                    "Confidence must be between 0.0 and 1.0"),
            cancellationToken: cancellationToken);

        return result;
    }
}
