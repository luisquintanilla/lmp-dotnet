using Microsoft.Extensions.AI;

namespace LMP.Samples.AdvancedRag;

/// <summary>
/// Advanced RAG module with 4 LMP predictors orchestrating a multi-hop pipeline:
///
///   1. QueryExpander — generates diverse search query variants
///   2. Reranker — scores each passage for relevance (LLM-based cross-encoder)
///   3. CragValidator — assesses whether context is sufficient (Corrective RAG)
///   4. AnswerGenerator — ChainOfThought grounded answer with citations
///
/// Multi-hop: if CRAG confidence = "ambiguous", generates a follow-up question
/// and loops back to retrieval (up to maxHops iterations).
///
/// Architecture mirrors the validated LMP+MEDI integration spec. When MEDI
/// RetrievalPipeline becomes available, the expand/rerank/CRAG predictors
/// can be wrapped as MEDI processors (RetrievalQueryProcessor,
/// RetrievalResultProcessor) for production pipeline orchestration.
///
/// Source generator emits GetPredictors() and CloneCore().
/// </summary>
public partial class AdvancedRagModule : LmpModule<QuestionInput, GroundedAnswer>
{
    private readonly Predictor<ExpandInput, ExpandOutput> _expand;
    private readonly Predictor<RerankInput, RerankOutput> _rerank;
    private readonly Predictor<CragInput, CragOutput> _cragValidate;
    private readonly ChainOfThought<AnswerInput, GroundedAnswer> _answer;
    private readonly IRetriever _retriever;
    private readonly int _topK;
    private readonly int _maxHops;

    /// <summary>Creates a new advanced RAG module.</summary>
    /// <param name="client">The chat client for LM calls.</param>
    /// <param name="retriever">The retriever for fetching passages.</param>
    /// <param name="topK">Number of passages to retrieve per query (default: 5).</param>
    /// <param name="maxHops">Maximum multi-hop iterations (default: 3).</param>
    public AdvancedRagModule(IChatClient client, IRetriever retriever, int topK = 5, int maxHops = 3)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(retriever);

        _retriever = retriever;
        _topK = topK;
        _maxHops = maxHops;

        _expand = new Predictor<ExpandInput, ExpandOutput>(client) { Name = "expand" };
        _rerank = new Predictor<RerankInput, RerankOutput>(client) { Name = "rerank" };
        _cragValidate = new Predictor<CragInput, CragOutput>(client) { Name = "crag_validate" };
        _answer = new ChainOfThought<AnswerInput, GroundedAnswer>(client) { Name = "answer" };
    }

    /// <inheritdoc />
    public override async Task<GroundedAnswer> ForwardAsync(
        QuestionInput input,
        CancellationToken cancellationToken = default)
    {
        var allPassages = new List<string>();
        var currentQuestion = input.Question;

        for (int hop = 0; hop < _maxHops; hop++)
        {
            // ── Step 1: Query Expansion ─────────────────────────
            var expanded = await _expand.PredictAsync(
                new ExpandInput(currentQuestion),
                trace: Trace,
                cancellationToken: cancellationToken);

            var queries = new[] { currentQuestion, expanded.Query1, expanded.Query2, expanded.Query3 }
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Distinct()
                .ToArray();

            // ── Step 2: Multi-Query Retrieval ───────────────────
            var retrievalTasks = queries.Select(q =>
                _retriever.RetrieveAsync(q, _topK, cancellationToken));
            var results = await Task.WhenAll(retrievalTasks);

            // Deduplicate passages from multiple queries
            var passages = results
                .SelectMany(r => r)
                .Distinct()
                .ToArray();

            // ── Step 3: LLM Reranking ───────────────────────────
            var reranked = new List<(string Passage, int Score)>();
            foreach (var passage in passages)
            {
                var score = await _rerank.PredictAsync(
                    new RerankInput(input.Question, passage),
                    trace: Trace,
                    validate: r =>
                        LmpAssert.That(r, x => x.RelevanceScore >= 0 && x.RelevanceScore <= 10,
                            "Relevance score must be between 0 and 10"),
                    maxRetries: 1,
                    cancellationToken: cancellationToken);
                reranked.Add((passage, score.RelevanceScore));
            }

            var topPassages = reranked
                .OrderByDescending(r => r.Score)
                .Take(_topK)
                .Select(r => r.Passage)
                .ToArray();

            allPassages.AddRange(topPassages);

            // ── Step 4: CRAG Validation ─────────────────────────
            var context = string.Join("\n\n---\n\n", topPassages);
            var crag = await _cragValidate.PredictAsync(
                new CragInput(input.Question, context),
                trace: Trace,
                validate: r =>
                    LmpAssert.That(r,
                        x => x.Confidence is "correct" or "ambiguous" or "incorrect",
                        "Confidence must be 'correct', 'ambiguous', or 'incorrect'"),
                maxRetries: 1,
                cancellationToken: cancellationToken);

            if (crag.Confidence == "correct")
                break;

            if (crag.Confidence == "incorrect")
            {
                // No useful context found — return a graceful fallback
                return new GroundedAnswer
                {
                    Answer = "I could not find sufficiently relevant information to answer this question confidently.",
                    Citations = []
                };
            }

            // "ambiguous" — use the follow-up question for the next hop
            if (!string.IsNullOrWhiteSpace(crag.FollowUpQuestion))
                currentQuestion = crag.FollowUpQuestion;
            else
                break; // No follow-up available, proceed with what we have
        }

        // ── Step 5: Answer Generation ───────────────────────────
        var finalContext = string.Join("\n\n---\n\n",
            allPassages.Distinct().Take(_topK * 2));

        var answer = await _answer.PredictAsync(
            new AnswerInput(input.Question, finalContext),
            trace: Trace,
            validate: result =>
            {
                LmpAssert.That(result,
                    r => !string.IsNullOrWhiteSpace(r.Answer),
                    "Answer must not be empty");
                LmpAssert.That(result,
                    r => r.Citations is { Length: > 0 },
                    "Must include at least one citation from context");
            },
            maxRetries: 2,
            cancellationToken: cancellationToken);

        return answer;
    }
}
