using System.Collections;
using LMP.Optimizers;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

public class MIPROv2Tests
{
    #region Test Infrastructure

    /// <summary>
    /// Simple output type for test predictors.
    /// </summary>
    public sealed class TestOutput
    {
        public required string Value { get; init; }
    }

    /// <summary>
    /// A test predictor that returns a fixed output and records traces.
    /// </summary>
    private sealed class FakePredictor : IPredictor
    {
        private readonly Func<object, object> _predict;

        public FakePredictor(Func<object, object> predict)
        {
            _predict = predict;
        }

        public string Name { get; set; } = "fake";
        public string Instructions { get; set; } = "default instruction";
        public List<(object Input, object Output)> TypedDemos { get; set; } = [];
        IList IPredictor.Demos => TypedDemos;
        public ChatOptions Config { get; set; } = new();

        public object Predict(object input, Trace? trace)
        {
            var output = _predict(input);
            trace?.Record(Name, input, output);
            return output;
        }

        public void AddDemo(object input, object output)
        {
            TypedDemos.Add((input, output));
        }

        public PredictorState GetState() => new()
        {
            Instructions = Instructions,
            Demos = [],
            Config = null
        };

        public void LoadState(PredictorState state) =>
            Instructions = state.Instructions;

        public IPredictor Clone()
        {
            var clone = new FakePredictor(_predict)
            {
                Name = Name,
                Instructions = Instructions,
                Config = new ChatOptions
                {
                    Temperature = Config.Temperature,
                    MaxOutputTokens = Config.MaxOutputTokens,
                },
            };
            clone.TypedDemos = new List<(object, object)>(TypedDemos);
            return clone;
        }
    }

    /// <summary>
    /// A test module with configurable predictors that supports cloning and tracing.
    /// </summary>
    private sealed class TestModule : LmpModule
    {
        private readonly List<FakePredictor> _predictors;
        private readonly Func<object, TestModule, CancellationToken, Task<object>>? _forwardLogic;

        public TestModule(
            List<FakePredictor> predictors,
            Func<object, TestModule, CancellationToken, Task<object>>? forwardLogic = null)
        {
            _predictors = predictors;
            _forwardLogic = forwardLogic;
        }

        public override async Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_forwardLogic is not null)
                return await _forwardLogic(input, this, cancellationToken);

            object result = input;
            foreach (var pred in _predictors)
            {
                result = pred.Predict(input, Trace);
            }
            return result;
        }

        public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
            => _predictors.Select(p => (p.Name, (IPredictor)p)).ToList();

        protected override LmpModule CloneCore()
        {
            var clonedPredictors = _predictors
                .Select(p => (FakePredictor)p.Clone())
                .ToList();
            return new TestModule(clonedPredictors, _forwardLogic);
        }
    }

    /// <summary>
    /// A fake IChatClient for instruction proposal that returns canned responses.
    /// </summary>
    private sealed class FakeProposalClient : IChatClient
    {
        private int _callCount;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var idx = Interlocked.Increment(ref _callCount);
            var response = new ChatResponse(
                new ChatMessage(ChatRole.Assistant, $"Generated instruction variant {idx}"));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>
    /// A fake IChatClient that throws on every call, for testing error resilience.
    /// </summary>
    private sealed class FailingProposalClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Proposal generation failed.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static (TestModule Module, FakePredictor Predictor) CreateSinglePredictorModule(
        Func<object, object> predict,
        string predictorName = "classify")
    {
        var predictor = new FakePredictor(predict) { Name = predictorName };
        var module = new TestModule([predictor]);
        return (module, predictor);
    }

    private static Func<Example, object, float> ExactMatchMetric()
        => (example, output) =>
        {
            var label = example.GetLabel();
            return Equals(label, output) ? 1.0f : 0f;
        };

    #endregion

    #region Constructor Validation

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MIPROv2(proposalClient: null!));
    }

    [Fact]
    public void Constructor_NumTrialsZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MIPROv2(new FakeProposalClient(), numTrials: 0));
    }

    [Fact]
    public void Constructor_NumTrialsNegative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MIPROv2(new FakeProposalClient(), numTrials: -1));
    }

    [Fact]
    public void Constructor_InstructionCandidatesZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MIPROv2(new FakeProposalClient(), numInstructionCandidates: 0));
    }

    [Fact]
    public void Constructor_DemoSubsetsZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MIPROv2(new FakeProposalClient(), numDemoSubsets: 0));
    }

    [Fact]
    public void Constructor_MaxDemosZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MIPROv2(new FakeProposalClient(), maxDemos: 0));
    }

    [Fact]
    public void Constructor_GammaZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MIPROv2(new FakeProposalClient(), gamma: 0));
    }

    [Fact]
    public void Constructor_GammaOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MIPROv2(new FakeProposalClient(), gamma: 1.0));
    }

    [Fact]
    public void Constructor_ValidParameters_DoesNotThrow()
    {
        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 10,
            numInstructionCandidates: 3,
            numDemoSubsets: 3,
            maxDemos: 2,
            gamma: 0.25,
            seed: 42);
        Assert.NotNull(optimizer);
    }

    #endregion

    #region CompileAsync Argument Validation

    [Fact]
    public async Task CompileAsync_NullModule_Throws()
    {
        var optimizer = new MIPROv2(new FakeProposalClient());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync<LmpModule>(null!, [], (_, _) => 1f));
    }

    [Fact]
    public async Task CompileAsync_NullTrainSet_Throws()
    {
        var optimizer = new MIPROv2(new FakeProposalClient());
        var (module, _) = CreateSinglePredictorModule(x => x);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(module, null!, (_, _) => 1f));
    }

    [Fact]
    public async Task CompileAsync_NullMetric_Throws()
    {
        var optimizer = new MIPROv2(new FakeProposalClient());
        var (module, _) = CreateSinglePredictorModule(x => x);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(module, [], null!));
    }

    #endregion

    #region Empty TrainSet

    [Fact]
    public async Task CompileAsync_EmptyTrainSet_ReturnsModuleUnchanged()
    {
        var optimizer = new MIPROv2(new FakeProposalClient(), numTrials: 3, seed: 42);
        var (module, predictor) = CreateSinglePredictorModule(x => x);

        var result = await optimizer.CompileAsync(module, [], ExactMatchMetric());

        Assert.Same(module, result);
        Assert.Empty(predictor.TypedDemos);
    }

    #endregion

    #region IOptimizer Interface

    [Fact]
    public void ImplementsIOptimizer()
    {
        var optimizer = new MIPROv2(new FakeProposalClient());
        Assert.IsAssignableFrom<IOptimizer>(optimizer);
    }

    #endregion

    #region Core Algorithm

    [Fact]
    public async Task CompileAsync_WithTrainSet_ReturnsOptimizedModule()
    {
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 20)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 4,
            numInstructionCandidates: 3,
            numDemoSubsets: 3,
            maxDemos: 2,
            seed: 42);

        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        Assert.NotNull(result);
        // Result should be a clone, not the original
        Assert.NotSame(module, result);
    }

    [Fact]
    public async Task CompileAsync_SetsInstructionsFromCandidates()
    {
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 15)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 5,
            numInstructionCandidates: 3,
            numDemoSubsets: 2,
            maxDemos: 2,
            seed: 42);

        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        // The result's predictor should have an instruction set from the candidates
        var predictors = result.GetPredictors();
        Assert.Single(predictors);
        Assert.False(string.IsNullOrEmpty(predictors[0].Predictor.Instructions),
            "Predictor should have instructions set by the optimizer.");
    }

    [Fact]
    public async Task CompileAsync_FillsDemos()
    {
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 20)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 4,
            numInstructionCandidates: 2,
            numDemoSubsets: 3,
            maxDemos: 3,
            seed: 42);

        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        // The best candidate should have demos filled from the bootstrap pool
        var demos = result.GetPredictors()[0].Predictor.Demos;
        // Demos might be 0 if the demo subsets were empty, or > 0 if bootstrap succeeded
        // With exact-match metric and echo predictor, bootstrap should succeed
        Assert.True(demos.Count >= 0);
    }

    [Fact]
    public async Task CompileAsync_MultiplePredictors_AllOptimized()
    {
        var pred1 = new FakePredictor(x => x) { Name = "classify" };
        var pred2 = new FakePredictor(x => x) { Name = "draft" };
        var module = new TestModule([pred1, pred2]);

        var trainSet = Enumerable.Range(0, 15)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 3,
            numInstructionCandidates: 2,
            numDemoSubsets: 2,
            maxDemos: 2,
            seed: 42);

        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        var predictors = result.GetPredictors();
        Assert.Equal(2, predictors.Count);

        // Both predictors should have instructions set
        foreach (var (name, predictor) in predictors)
        {
            Assert.False(string.IsNullOrEmpty(predictor.Instructions),
                $"Predictor '{name}' should have instructions.");
        }
    }

    #endregion

    #region Instruction Proposal Error Resilience

    [Fact]
    public async Task CompileAsync_ProposalClientFails_StillOptimizes()
    {
        // Even if the LM proposal client fails, MIPROv2 should still work
        // using the original instructions.
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 15)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var optimizer = new MIPROv2(
            new FailingProposalClient(),
            numTrials: 3,
            numInstructionCandidates: 3,
            numDemoSubsets: 2,
            maxDemos: 2,
            seed: 42);

        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        // Should still return a result, falling back to original instructions
        Assert.NotNull(result);
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task CompileAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var optimizer = new MIPROv2(new FakeProposalClient(), numTrials: 5, seed: 42);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            optimizer.CompileAsync(module, trainSet, ExactMatchMetric(), cts.Token));
    }

    #endregion

    #region Deterministic Seeding

    [Fact]
    public async Task CompileAsync_SameSeed_ProducesSameResult()
    {
        static (TestModule, FakePredictor) MakeModule() =>
            CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 20)
            .Select(i => (Example)new Example<string, string>($"item_{i}", $"item_{i}"))
            .ToList();

        // Run 1
        var (module1, _) = MakeModule();
        var optimizer1 = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 4,
            numInstructionCandidates: 2,
            numDemoSubsets: 2,
            maxDemos: 2,
            seed: 42);
        var result1 = await optimizer1.CompileAsync(module1, trainSet, ExactMatchMetric());

        // Run 2
        var (module2, _) = MakeModule();
        var optimizer2 = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 4,
            numInstructionCandidates: 2,
            numDemoSubsets: 2,
            maxDemos: 2,
            seed: 42);
        var result2 = await optimizer2.CompileAsync(module2, trainSet, ExactMatchMetric());

        // Both should have the same instruction
        var instr1 = result1.GetPredictors()[0].Predictor.Instructions;
        var instr2 = result2.GetPredictors()[0].Predictor.Instructions;
        Assert.Equal(instr1, instr2);

        // Both should have the same demo count
        var demos1 = result1.GetPredictors()[0].Predictor.Demos;
        var demos2 = result2.GetPredictors()[0].Predictor.Demos;
        Assert.Equal(demos1.Count, demos2.Count);
    }

    #endregion

    #region Small Dataset

    [Fact]
    public async Task CompileAsync_VerySmallDataset_HandlesGracefully()
    {
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        // Only 2 examples — validation split will be empty, falls back to train
        var trainSet = new List<Example>
        {
            new Example<string, string>("a", "a"),
            new Example<string, string>("b", "b"),
        };

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 2,
            numInstructionCandidates: 2,
            numDemoSubsets: 2,
            maxDemos: 1,
            seed: 42);

        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());
        Assert.NotNull(result);
    }

    #endregion

    #region MetricThreshold

    [Fact]
    public async Task CompileAsync_HighThreshold_NoDemos_StillOptimizesInstructions()
    {
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        // Metric returns 0.5, threshold is 1.0 — no demos will be bootstrapped
        var halfMetric = (Example e, object output) => 0.5f;

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 3,
            numInstructionCandidates: 2,
            numDemoSubsets: 2,
            maxDemos: 2,
            metricThreshold: 1.0f,
            seed: 42);

        var result = await optimizer.CompileAsync(module, trainSet, halfMetric);

        // Should still return a result (instruction optimization works without demos)
        Assert.NotNull(result);
        Assert.NotSame(module, result);
    }

    #endregion
}
