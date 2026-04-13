using System.ComponentModel;

namespace LMP.Samples.AdvancedRag;

// ── Pipeline Input/Output ───────────────────────────────────

/// <summary>
/// Input to the Advanced RAG pipeline — a question to answer.
/// </summary>
/// <param name="Question">The question to answer using retrieval-augmented generation.</param>
public record QuestionInput(
    [property: Description("The question to answer")]
    string Question);

/// <summary>
/// Final output — a grounded answer with citations from retrieved context.
/// </summary>
[LmpSignature("Answer the question using ONLY information from the provided context. Include citations.")]
public partial record GroundedAnswer
{
    /// <summary>The answer grounded in retrieved context.</summary>
    [Description("The answer to the question, grounded in the provided context")]
    public required string Answer { get; init; }

    /// <summary>Verbatim quotes from context that support the answer.</summary>
    [Description("Array of verbatim quotes from the context that support the answer")]
    public required string[] Citations { get; init; }
}

// ── Query Expansion ─────────────────────────────────────────

/// <summary>
/// Input for query expansion — the original question.
/// </summary>
/// <param name="Question">The original question to expand into multiple search queries.</param>
public record ExpandInput(
    [property: Description("The original question to expand")]
    string Question);

/// <summary>
/// Output of query expansion — multiple search query variants.
/// </summary>
[LmpSignature("Generate 3 diverse search query variants for the given question to improve recall")]
public partial record ExpandOutput
{
    /// <summary>First search query variant.</summary>
    [Description("First search query variant — rephrase the question")]
    public required string Query1 { get; init; }

    /// <summary>Second search query variant.</summary>
    [Description("Second search query variant — focus on key entities")]
    public required string Query2 { get; init; }

    /// <summary>Third search query variant.</summary>
    [Description("Third search query variant — use synonyms or related terms")]
    public required string Query3 { get; init; }
}

// ── Reranking ───────────────────────────────────────────────

/// <summary>
/// Input for relevance scoring — a question + single passage.
/// </summary>
/// <param name="Question">The question being answered.</param>
/// <param name="Passage">A retrieved passage to score for relevance.</param>
public record RerankInput(
    [property: Description("The question being answered")]
    string Question,
    [property: Description("A retrieved passage to evaluate for relevance")]
    string Passage);

/// <summary>
/// Output of relevance scoring — a score and brief justification.
/// </summary>
[LmpSignature("Rate how relevant this passage is for answering the question")]
public partial record RerankOutput
{
    /// <summary>Relevance score from 0 (irrelevant) to 10 (perfectly relevant).</summary>
    [Description("Relevance score from 0 (irrelevant) to 10 (perfectly relevant)")]
    public required int RelevanceScore { get; init; }
}

// ── CRAG Validation ─────────────────────────────────────────

/// <summary>
/// Input for CRAG validation — question + top passages.
/// </summary>
/// <param name="Question">The question being answered.</param>
/// <param name="Context">The top retrieved passages concatenated.</param>
public record CragInput(
    [property: Description("The question being answered")]
    string Question,
    [property: Description("The top retrieved passages as context")]
    string Context);

/// <summary>
/// Output of CRAG validation — confidence assessment.
/// </summary>
[LmpSignature("Assess whether the retrieved context is sufficient to answer the question confidently")]
public partial record CragOutput
{
    /// <summary>Confidence level in the retrieved context.</summary>
    [Description("Confidence: 'correct' (context is sufficient), 'ambiguous' (partially relevant), or 'incorrect' (context doesn't help)")]
    public required string Confidence { get; init; }

    /// <summary>If ambiguous, a refined follow-up question to search for more information.</summary>
    [Description("If confidence is 'ambiguous', provide a refined follow-up question to search for missing information. Otherwise empty.")]
    public required string FollowUpQuestion { get; init; }
}

// ── Answer Generation ───────────────────────────────────────

/// <summary>
/// Input for the answer generation step — question + validated context.
/// </summary>
/// <param name="Question">The original question.</param>
/// <param name="Context">Validated and reranked context passages.</param>
public record AnswerInput(
    [property: Description("The original question to answer")]
    string Question,
    [property: Description("Validated context passages to ground the answer in")]
    string Context);
