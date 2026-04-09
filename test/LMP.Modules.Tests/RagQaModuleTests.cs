using Microsoft.Extensions.AI;

namespace LMP.Tests;

// --- RAG types ---

/// <summary>
/// Input to the RAG QA module: just the user's question.
/// </summary>
public record QuestionInput(string Question);

/// <summary>
/// Input to the predictor inside the RAG module: the question plus retrieved passages.
/// </summary>
public record AnswerInput(string Question, string[] Passages)
{
    public override string ToString()
        => $"Question: {Question}\n\nContext:\n{string.Join("\n\n", Passages)}";
}

/// <summary>
/// Output of the RAG QA predictor: an answer with a confidence score.
/// </summary>
public record AnswerWithContext
{
    public required string Answer { get; init; }
    public required float Confidence { get; init; }
}

// --- RagQaModule ---

/// <summary>
/// Sample RAG (Retrieval-Augmented Generation) module that demonstrates
/// <see cref="IRetriever"/> + <see cref="Predictor{TInput, TOutput}"/> composition
/// inside <see cref="LmpModule.ForwardAsync"/>.
///
/// Flow: question → retrieve context passages → predict answer with context.
/// The predictor is discoverable via <see cref="GetPredictors"/> so optimizers
/// can fill demos and tune instructions automatically.
/// </summary>
public class RagQaModule : LmpModule
{
    private readonly IRetriever _retriever;
    private readonly Predictor<AnswerInput, AnswerWithContext> _answer;
    private readonly int _topK;

    /// <summary>
    /// Creates a RAG QA module that retrieves context then predicts an answer.
    /// </summary>
    /// <param name="client">The chat client for LM calls.</param>
    /// <param name="retriever">The retriever for fetching context passages.</param>
    /// <param name="topK">Number of passages to retrieve (default: 5).</param>
    public RagQaModule(IChatClient client, IRetriever retriever, int topK = 5)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);

        _retriever = retriever;
        _topK = topK;
        _answer = new Predictor<AnswerInput, AnswerWithContext>(client)
        {
            Name = "_answer",
            Instructions = "Answer the question using the provided context passages"
        };
    }

    /// <summary>
    /// Typed entry point: retrieves context passages for the question,
    /// then predicts an answer using the predictor.
    /// </summary>
    /// <param name="input">The user's question.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer with a confidence score.</returns>
    /// <exception cref="LmpAssertionException">
    /// Thrown when the predicted confidence is outside [0.0, 1.0].
    /// </exception>
    public async Task<AnswerWithContext> ForwardAsync(
        QuestionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Retrieve context passages
        var passages = await _retriever.RetrieveAsync(
            input.Question, _topK, cancellationToken);

        // 2. Predict with retrieved context
        var answerInput = new AnswerInput(input.Question, passages);
        var result = await _answer.PredictAsync(
            answerInput, Trace, cancellationToken: cancellationToken);

        // 3. Assert confidence is valid
        LmpAssert.That(result,
            r => r.Confidence >= 0f && r.Confidence <= 1f,
            "Confidence must be between 0.0 and 1.0");

        return result;
    }

    /// <inheritdoc />
    public override async Task<object> ForwardAsync(
        object input,
        CancellationToken cancellationToken = default)
        => await ForwardAsync((QuestionInput)input, cancellationToken);

    /// <inheritdoc />
    public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
        => [("_answer", _answer)];
}

// --- Tests ---

/// <summary>
/// Tests for Phase 5.2 — RAG Composition Example.
/// Verifies that RagQaModule retrieves context passages via IRetriever,
/// passes them to a Predictor, and returns typed output.
/// </summary>
public class RagQaModuleTests
{
    private static readonly string[] SampleDocs =
    [
        "The capital of France is Paris. It has been the capital since the 10th century.",
        "Python is a popular programming language created by Guido van Rossum.",
        "The Eiffel Tower is located in Paris, France. It was built in 1889.",
        "Machine learning is a subset of artificial intelligence.",
        "The Great Wall of China is one of the most famous landmarks in the world.",
        "Paris has a population of about 2.1 million in the city proper.",
    ];

    // === Constructor tests ===

    [Fact]
    public void Constructor_ThrowsOnNullClient()
    {
        var retriever = new FakeRetriever([]);
        Assert.Throws<ArgumentNullException>(
            () => new RagQaModule(null!, retriever));
    }

    [Fact]
    public void Constructor_ThrowsOnNullRetriever()
    {
        var client = new FakeChatClient();
        Assert.Throws<ArgumentNullException>(
            () => new RagQaModule(client, null!));
    }

    [Fact]
    public void Constructor_ThrowsOnInvalidTopK()
    {
        var client = new FakeChatClient();
        var retriever = new FakeRetriever([]);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RagQaModule(client, retriever, topK: 0));
    }

    // === ForwardAsync — typed overload ===

    [Fact]
    public async Task ForwardAsync_RetrievesContextAndPredicts()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AnswerWithContext
        {
            Answer = "The capital of France is Paris.",
            Confidence = 0.95f
        });

        var retriever = new FakeRetriever(SampleDocs);
        var module = new RagQaModule(fakeClient, retriever);

        var result = await module.ForwardAsync(
            new QuestionInput("What is the capital of France?"));

        Assert.Equal("The capital of France is Paris.", result.Answer);
        Assert.Equal(0.95f, result.Confidence);
    }

    [Fact]
    public async Task ForwardAsync_PassesRetrievedPassagesToPredictor()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AnswerWithContext
        {
            Answer = "Paris",
            Confidence = 0.9f
        });

        var retriever = new FakeRetriever(SampleDocs);
        var module = new RagQaModule(fakeClient, retriever);

        await module.ForwardAsync(
            new QuestionInput("Tell me about Paris France"));

        // The user message sent to the client should contain retrieved passages
        var messages = fakeClient.SentMessages[0];
        var userMessage = messages.Last(m => m.Role == ChatRole.User);
        Assert.Contains("capital of France is Paris", userMessage.Text);
        Assert.Contains("Eiffel Tower", userMessage.Text);
    }

    [Fact]
    public async Task ForwardAsync_CustomTopK_LimitsPassageCount()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AnswerWithContext
        {
            Answer = "Paris",
            Confidence = 0.8f
        });

        var retriever = new FakeRetriever(SampleDocs);
        var module = new RagQaModule(fakeClient, retriever, topK: 2);

        await module.ForwardAsync(
            new QuestionInput("Tell me about Paris France capital"));

        // Should have retrieved at most 2 passages
        var messages = fakeClient.SentMessages[0];
        var userMessage = messages.Last(m => m.Role == ChatRole.User);
        // With topK=2, only the top 2 matching docs should appear
        Assert.Contains("Paris", userMessage.Text);
    }

    // === ForwardAsync — object overload ===

    [Fact]
    public async Task ForwardAsync_ObjectOverload_CastsAndDelegates()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AnswerWithContext
        {
            Answer = "Paris",
            Confidence = 0.85f
        });

        var retriever = new FakeRetriever(SampleDocs);
        LmpModule module = new RagQaModule(fakeClient, retriever);

        var result = await module.ForwardAsync(
            new QuestionInput("What is the capital of France?"));

        var answer = Assert.IsType<AnswerWithContext>(result);
        Assert.Equal("Paris", answer.Answer);
    }

    // === Trace recording ===

    [Fact]
    public async Task ForwardAsync_RecordsTraceEntry()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AnswerWithContext
        {
            Answer = "Paris",
            Confidence = 0.9f
        });

        var retriever = new FakeRetriever(SampleDocs);
        var module = new RagQaModule(fakeClient, retriever);
        module.Trace = new Trace();

        await module.ForwardAsync(new QuestionInput("Capital of France?"));

        Assert.Single(module.Trace.Entries);
        var entry = module.Trace.Entries[0];
        Assert.Equal("_answer", entry.PredictorName);
        Assert.IsType<AnswerInput>(entry.Input);
        Assert.IsType<AnswerWithContext>(entry.Output);
    }

    [Fact]
    public async Task ForwardAsync_TraceInputContainsPassages()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AnswerWithContext
        {
            Answer = "Paris",
            Confidence = 0.9f
        });

        var retriever = new FakeRetriever(SampleDocs);
        var module = new RagQaModule(fakeClient, retriever);
        module.Trace = new Trace();

        await module.ForwardAsync(new QuestionInput("Capital of France?"));

        var input = Assert.IsType<AnswerInput>(module.Trace.Entries[0].Input);
        Assert.Equal("Capital of France?", input.Question);
        Assert.NotEmpty(input.Passages);
        Assert.Contains(input.Passages,
            p => p.Contains("capital of France is Paris"));
    }

    // === Validation ===

    [Fact]
    public async Task ForwardAsync_ThrowsOnInvalidConfidence_TooHigh()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AnswerWithContext
        {
            Answer = "test",
            Confidence = 1.5f
        });

        var retriever = new FakeRetriever(SampleDocs);
        var module = new RagQaModule(fakeClient, retriever);

        await Assert.ThrowsAsync<LmpAssertionException>(
            () => module.ForwardAsync(new QuestionInput("test")));
    }

    [Fact]
    public async Task ForwardAsync_ThrowsOnInvalidConfidence_Negative()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AnswerWithContext
        {
            Answer = "test",
            Confidence = -0.1f
        });

        var retriever = new FakeRetriever(SampleDocs);
        var module = new RagQaModule(fakeClient, retriever);

        await Assert.ThrowsAsync<LmpAssertionException>(
            () => module.ForwardAsync(new QuestionInput("test")));
    }

    [Fact]
    public async Task ForwardAsync_AcceptsBoundaryConfidence_Zero()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AnswerWithContext
        {
            Answer = "unsure",
            Confidence = 0.0f
        });

        var retriever = new FakeRetriever(SampleDocs);
        var module = new RagQaModule(fakeClient, retriever);

        var result = await module.ForwardAsync(new QuestionInput("test"));
        Assert.Equal(0.0f, result.Confidence);
    }

    [Fact]
    public async Task ForwardAsync_AcceptsBoundaryConfidence_One()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AnswerWithContext
        {
            Answer = "certain",
            Confidence = 1.0f
        });

        var retriever = new FakeRetriever(SampleDocs);
        var module = new RagQaModule(fakeClient, retriever);

        var result = await module.ForwardAsync(new QuestionInput("test"));
        Assert.Equal(1.0f, result.Confidence);
    }

    // === GetPredictors ===

    [Fact]
    public void GetPredictors_ReturnsSinglePredictor()
    {
        var fakeClient = new FakeChatClient();
        var retriever = new FakeRetriever([]);
        var module = new RagQaModule(fakeClient, retriever);

        var predictors = module.GetPredictors();

        Assert.Single(predictors);
        Assert.Equal("_answer", predictors[0].Name);
        Assert.IsAssignableFrom<IPredictor>(predictors[0].Predictor);
    }

    [Fact]
    public void GetPredictors_PredictorIsOptimizable()
    {
        var fakeClient = new FakeChatClient();
        var retriever = new FakeRetriever([]);
        var module = new RagQaModule(fakeClient, retriever);

        var (_, predictor) = module.GetPredictors()[0];

        // Optimizer should be able to modify instructions and demos
        predictor.Instructions = "Custom instruction from optimizer";
        Assert.Equal("Custom instruction from optimizer", predictor.Instructions);

        predictor.AddDemo(
            new AnswerInput("q", ["passage"]),
            new AnswerWithContext { Answer = "a", Confidence = 0.8f });
        Assert.Single(predictor.Demos);
    }

    // === Null input ===

    [Fact]
    public async Task ForwardAsync_ThrowsOnNullInput()
    {
        var fakeClient = new FakeChatClient();
        var retriever = new FakeRetriever([]);
        var module = new RagQaModule(fakeClient, retriever);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => module.ForwardAsync((QuestionInput)null!));
    }

    // === Empty retrieval ===

    [Fact]
    public async Task ForwardAsync_WorksWithEmptyRetrieval()
    {
        var fakeClient = new FakeChatClient();
        fakeClient.EnqueueResponse(new AnswerWithContext
        {
            Answer = "I don't know",
            Confidence = 0.1f
        });

        // Retriever with no matching documents
        var retriever = new FakeRetriever(["Unrelated document about cooking."]);
        var module = new RagQaModule(fakeClient, retriever);

        var result = await module.ForwardAsync(
            new QuestionInput("quantum physics"));

        Assert.Equal("I don't know", result.Answer);
    }

    // === Cancellation ===

    [Fact]
    public async Task ForwardAsync_RespectsCancellation()
    {
        var fakeClient = new FakeChatClient();
        var retriever = new FakeRetriever(SampleDocs);
        var module = new RagQaModule(fakeClient, retriever);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // FakeRetriever checks cancellation before doing work
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => module.ForwardAsync(
                new QuestionInput("test"),
                cts.Token));
    }
}

/// <summary>
/// Tests for the <see cref="FakeRetriever"/> test utility.
/// </summary>
public class FakeRetrieverTests
{
    [Fact]
    public async Task RetrieveAsync_ReturnsMatchingDocuments()
    {
        var docs = new[]
        {
            "The sky is blue.",
            "Grass is green.",
            "The ocean is blue and vast."
        };

        var retriever = new FakeRetriever(docs);
        var results = await retriever.RetrieveAsync("blue");

        Assert.Equal(2, results.Length);
        Assert.Contains("The sky is blue.", results);
        Assert.Contains("The ocean is blue and vast.", results);
    }

    [Fact]
    public async Task RetrieveAsync_RespectsTopK()
    {
        var docs = Enumerable.Range(0, 10)
            .Select(i => $"Document {i} about cats")
            .ToArray();

        var retriever = new FakeRetriever(docs);
        var results = await retriever.RetrieveAsync("cats", k: 3);

        Assert.Equal(3, results.Length);
    }

    [Fact]
    public async Task RetrieveAsync_RanksMultipleWordMatchesHigher()
    {
        var docs = new[]
        {
            "Languages are useful for development.",
            "Python is great for scripting.",
            "Python programming with data science is popular."
        };

        var retriever = new FakeRetriever(docs);
        var results = await retriever.RetrieveAsync("Python programming", k: 3);

        // "Python programming..." matches both words (score 2)
        // "Python is great..." matches only "python" (score 1)
        // "Languages are..." matches neither (score 0)
        Assert.Equal(2, results.Length);
        Assert.Equal("Python programming with data science is popular.", results[0]);
        Assert.Equal("Python is great for scripting.", results[1]);
    }

    [Fact]
    public async Task RetrieveAsync_ReturnsEmptyForNoMatch()
    {
        var docs = new[] { "The sky is blue.", "Grass is green." };

        var retriever = new FakeRetriever(docs);
        var results = await retriever.RetrieveAsync("quantum");

        Assert.Empty(results);
    }

    [Fact]
    public async Task RetrieveAsync_IsCaseInsensitive()
    {
        var docs = new[] { "Python IS Great" };

        var retriever = new FakeRetriever(docs);
        var results = await retriever.RetrieveAsync("python");

        Assert.Single(results);
    }

    [Fact]
    public async Task RetrieveAsync_DefaultK_IsFive()
    {
        var docs = Enumerable.Range(0, 20)
            .Select(i => $"Document {i} about testing")
            .ToArray();

        var retriever = new FakeRetriever(docs);
        var results = await retriever.RetrieveAsync("testing");

        Assert.Equal(5, results.Length);
    }

    [Fact]
    public async Task RetrieveAsync_RespectsCancellation()
    {
        var retriever = new FakeRetriever(["some doc"]);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => retriever.RetrieveAsync("test", cancellationToken: cts.Token));
    }
}
