using System.ComponentModel;

namespace LMP.Samples.Rag;

/// <summary>
/// Input record for the RAG pipeline: the user's question.
/// </summary>
/// <param name="Question">The question to answer.</param>
public record QuestionInput(
    [property: Description("The question to answer")]
    string Question);

/// <summary>
/// Output of the RAG predictor — an answer grounded in retrieved context.
/// The source generator reads field descriptions to build the LM prompt.
/// </summary>
[LmpSignature("Answer a question using provided context")]
public partial record AnswerOutput
{
    /// <summary>The answer to the question.</summary>
    [Description("The answer to the question based on the provided context")]
    public required string Answer { get; init; }

    /// <summary>Confidence score between 0.0 and 1.0.</summary>
    [Description("Confidence score between 0.0 and 1.0")]
    public required float Confidence { get; init; }
}
