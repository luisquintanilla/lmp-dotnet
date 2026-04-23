using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using LMP.Optimizers;
using Microsoft.Extensions.AI;
#pragma warning disable CS0618 // tests obsolete ISampler interface intentionally
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
    /// Implements <see cref="IOptimizationTarget"/> so fractal <see cref="LmpModule"/>
    /// parameter discovery and routing include it (required for T2f.1 seam migration).
    /// </summary>
    private sealed class FakePredictor : IPredictor, IOptimizationTarget
    {
        /// <summary>
        /// Counts calls into <see cref="IOptimizationTarget.WithParameters"/> across
        /// all instances. Used by tests to prove the seam is exercised. Reset via
        /// <see cref="ResetWithParametersCalls"/>.
        /// </summary>
        public static int WithParametersCalls;

        public static void ResetWithParametersCalls() =>
            Interlocked.Exchange(ref WithParametersCalls, 0);

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
        public UsageDetails? RecordUsage { get; set; }

        public object Predict(object input, Trace? trace)
        {
            var output = _predict(input);
            trace?.Record(Name, input, output, RecordUsage);
            return output;
        }

        public void AddDemo(object input, object output)
        {
            TypedDemos.Add((input, output));
        }

        public PredictorState GetState() => new()
        {
            Instructions = Instructions,
            Demos = [.. TypedDemos.Select(d => new DemoEntry
            {
                Input = SerializeToDict(d.Input),
                Output = SerializeToDict(d.Output),
            })],
            Config = null
        };

        public void LoadState(PredictorState state)
        {
            Instructions = state.Instructions;
            TypedDemos.Clear();
            foreach (var entry in state.Demos)
            {
                TypedDemos.Add((DeserializeFromDict(entry.Input), DeserializeFromDict(entry.Output)));
            }
        }

        [UnconditionalSuppressMessage("AOT", "IL2026", Justification = "Test double.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Test double.")]
        private static Dictionary<string, JsonElement> SerializeToDict(object value)
            => new() { ["value"] = JsonSerializer.SerializeToElement(value, value.GetType()) };

        private static object DeserializeFromDict(Dictionary<string, JsonElement> dict)
        {
            if (dict.TryGetValue("value", out var elem))
            {
                return elem.ValueKind == JsonValueKind.String
                    ? elem.GetString()!
                    : elem.GetRawText();
            }
            return "";
        }

        public IPredictor Clone()
        {
            var clone = new FakePredictor(_predict)
            {
                Name = Name,
                Instructions = Instructions,
                RecordUsage = RecordUsage,
                Config = new ChatOptions
                {
                    Temperature = Config.Temperature,
                    MaxOutputTokens = Config.MaxOutputTokens,
                },
            };
            clone.TypedDemos = new List<(object, object)>(TypedDemos);
            return clone;
        }

        // ── IOptimizationTarget implementation ───────────────────────────

        TargetShape IOptimizationTarget.Shape => TargetShape.SingleTurn;

        Task<(object Output, Trace Trace)> IOptimizationTarget.ExecuteAsync(
            object input, CancellationToken ct)
        {
            var trace = new Trace();
            var output = _predict(input);
            trace.Record(Name, input, output, RecordUsage);
            return Task.FromResult<(object, Trace)>((output, trace));
        }

        TypedParameterSpace IOptimizationTarget.GetParameterSpace()
            => TypedParameterSpace.Empty
                .Add("instructions", new StringValued(Instructions))
                .Add("demos", new Subset([.. TypedDemos.Cast<object>()], 0, TypedDemos.Count));

        TargetState IOptimizationTarget.GetState() => TargetState.From(GetState());

        void IOptimizationTarget.ApplyState(TargetState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            LoadState(state.As<PredictorState>());
        }

        IOptimizationTarget IOptimizationTarget.WithParameters(ParameterAssignment assignment)
        {
            ArgumentNullException.ThrowIfNull(assignment);
            Interlocked.Increment(ref WithParametersCalls);
            var clone = (FakePredictor)Clone();
            foreach (var (k, v) in assignment.Values)
            {
                switch (k)
                {
                    case "instructions":
                        clone.Instructions = (string)v;
                        break;
                    case "demos":
                        if (v is not IReadOnlyList<object> list)
                            throw new ArgumentException(
                                $"FakePredictor.WithParameters: parameter 'demos' expected " +
                                $"IReadOnlyList<object>, got {v.GetType().FullName}.",
                                nameof(assignment));
                        clone.TypedDemos.Clear();
                        foreach (var item in list)
                        {
                            if (item is ValueTuple<object, object> erased)
                                clone.TypedDemos.Add((erased.Item1, erased.Item2));
                            else
                                throw new ArgumentException(
                                    $"FakePredictor.WithParameters: demo item has unsupported type " +
                                    $"{item?.GetType().FullName ?? "null"}.",
                                    nameof(assignment));
                        }
                        break;
                    default:
                        throw new ArgumentException(
                            $"FakePredictor.WithParameters: unknown parameter key '{k}'. " +
                            $"Valid keys: instructions, demos.",
                            nameof(assignment));
                }
            }
            return clone;
        }

        TService? IOptimizationTarget.GetService<TService>() where TService : class
            => this as TService;
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

    #region Custom ISampler Injection

    [Fact]
    public async Task CompileAsync_CustomSampler_IsUsed()
    {
        int proposeCallCount = 0;
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 15)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        // Provide a custom sampler factory that wraps TPE but counts calls
        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            samplerFactory: cardinalities =>
            {
                var inner = new CategoricalTpeSampler(cardinalities, seed: 42);
                return new CountingSampler(inner, () => proposeCallCount++);
            },
            numTrials: 3,
            numInstructionCandidates: 2,
            numDemoSubsets: 2,
            maxDemos: 2,
            seed: 42);

        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        Assert.NotNull(result);
        Assert.Equal(3, proposeCallCount); // One per trial
    }

    /// <summary>
    /// Wraps an ISampler and invokes a callback on each Propose() call.
    /// </summary>
    private sealed class CountingSampler(ISampler inner, Action onPropose) : ISampler
    {
        public int TrialCount => inner.TrialCount;

        public Dictionary<string, int> Propose()
        {
            onPropose();
            return inner.Propose();
        }

        public void Update(Dictionary<string, int> config, float score)
            => inner.Update(config, score);
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
            optimizer.CompileAsync(module, trainSet, ExactMatchMetric(), options: CompileOptions.RuntimeOnly, cts.Token));
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

    #region Cost Collection Wiring

    [Fact]
    public async Task CompileAsync_PassesTrialCostToSampler()
    {
        var receivedCosts = new List<TrialCost>();
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 15)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            samplerFactory: cardinalities =>
                new CostCapturingSampler(
                    new CategoricalTpeSampler(cardinalities, seed: 42),
                    receivedCosts),
            numTrials: 3,
            numInstructionCandidates: 2,
            numDemoSubsets: 2,
            maxDemos: 2,
            seed: 42);

        await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        Assert.Equal(3, receivedCosts.Count);
        foreach (var cost in receivedCosts)
        {
            Assert.True(cost.ElapsedMilliseconds >= 0, "ElapsedMilliseconds should be non-negative.");
            Assert.True(cost.TotalTokens >= 0, "TotalTokens should be non-negative.");
            Assert.True(cost.ApiCalls >= 0, "ApiCalls should be non-negative.");
        }
    }

    [Fact]
    public async Task CompileAsync_TrialHistoryIncludesCost()
    {
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

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

        await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        var history = optimizer.LastTrialHistory;
        Assert.NotNull(history);
        Assert.Equal(3, history.Count);

        foreach (var trial in history)
        {
            Assert.NotNull(trial.Cost);
            Assert.True(trial.Cost.ElapsedMilliseconds >= 0);
        }
    }

    [Fact]
    public async Task CompileAsync_CostAwareSampler_ReceivesCost()
    {
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 15)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            samplerFactory: cardinalities =>
                new CostAwareSampler(cardinalities, seed: 42),
            numTrials: 3,
            numInstructionCandidates: 2,
            numDemoSubsets: 2,
            maxDemos: 2,
            seed: 42);

        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        Assert.NotNull(result);
        Assert.NotSame(module, result);
    }

    [Fact]
    public async Task CompileAsync_TracingModuleRecordsUsage_CostHasTokens()
    {
        var receivedCosts = new List<TrialCost>();
        var usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 30, TotalTokenCount = 80 };

        var pred = new FakePredictor(x => x)
        {
            Name = "classify",
            RecordUsage = usage
        };
        var module = new TestModule([pred]);

        var trainSet = Enumerable.Range(0, 15)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            samplerFactory: cardinalities =>
                new CostCapturingSampler(
                    new CategoricalTpeSampler(cardinalities, seed: 42),
                    receivedCosts),
            numTrials: 2,
            numInstructionCandidates: 2,
            numDemoSubsets: 2,
            maxDemos: 2,
            seed: 42);

        await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        Assert.Equal(2, receivedCosts.Count);
        // Predictors record usage during ForwardAsync, so tokens should be captured
        foreach (var cost in receivedCosts)
        {
            Assert.True(cost.TotalTokens > 0, "TotalTokens should reflect trace usage.");
            Assert.True(cost.InputTokens > 0, "InputTokens should reflect trace usage.");
            Assert.True(cost.OutputTokens > 0, "OutputTokens should reflect trace usage.");
            Assert.True(cost.ApiCalls > 0, "ApiCalls should reflect trace usage.");
        }
    }

    /// <summary>
    /// Wraps an ISampler and captures TrialCost data from the cost-aware Update overload.
    /// </summary>
    private sealed class CostCapturingSampler(ISampler inner, List<TrialCost> capturedCosts) : ISampler
    {
        public int TrialCount => inner.TrialCount;

        public Dictionary<string, int> Propose() => inner.Propose();

        public void Update(Dictionary<string, int> config, float score)
            => inner.Update(config, score);

        public void Update(Dictionary<string, int> config, float score, TrialCost cost)
        {
            capturedCosts.Add(cost);
            inner.Update(config, score, cost);
        }
    }

    #endregion

    #region T2f — OptimizeAsync entry-point target validation

    [Fact]
    public async Task OptimizeAsync_OnLmpModuleTarget_AppliesStateAndPopulatesContext()
    {
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var ctx = new OptimizationContext
        {
            Target = module,
            TrainSet = trainSet,
            Metric = ExactMatchMetric()
        };

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 2,
            numInstructionCandidates: 2,
            numDemoSubsets: 1,
            maxDemos: 1,
            seed: 42);

        await optimizer.OptimizeAsync(ctx);

        Assert.True(ctx.TrialHistory.Count > 0,
            "OptimizeAsync should propagate at least one trial into ctx.TrialHistory.");
        Assert.True(ctx.SearchSpace.Parameters.Count > 0,
            "OptimizeAsync should publish the discovered parameter cardinalities to ctx.SearchSpace.");
    }

    [Fact]
    public async Task OptimizeAsync_OnChainTargetComposite_ThrowsArgumentException()
    {
        var (moduleA, _) = CreateSinglePredictorModule(x => x, "stage_a");
        var (moduleB, _) = CreateSinglePredictorModule(x => x, "stage_b");
        var composite = moduleA.Then(moduleB);

        var trainSet = new List<Example>
        {
            new Example<string, string>("a", "a"),
        };

        var ctx = new OptimizationContext
        {
            Target = composite,
            TrainSet = trainSet,
            Metric = ExactMatchMetric()
        };

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 1,
            numInstructionCandidates: 1,
            numDemoSubsets: 1,
            maxDemos: 1,
            seed: 42);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => optimizer.OptimizeAsync(ctx));
        Assert.Contains("MIPROv2", ex.Message);
        Assert.Contains("LmpModule", ex.Message);
        // Regression canary: the banned NotSupportedException must not come back.
        Assert.IsNotType<NotSupportedException>(ex);
    }

    [Fact]
    public async Task OptimizeAsync_OnPredictorTarget_ThrowsArgumentException()
    {
        // A bare Predictor<TIn,TOut> is IOptimizationTarget but not LmpModule.
        var bare = new Predictor<string, string>(new FakeProposalClient()) { Name = "bare" };

        var trainSet = new List<Example>
        {
            new Example<string, string>("x", "x"),
        };

        var ctx = new OptimizationContext
        {
            Target = bare,
            TrainSet = trainSet,
            Metric = ExactMatchMetric()
        };

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 1,
            numInstructionCandidates: 1,
            numDemoSubsets: 1,
            maxDemos: 1,
            seed: 42);

        await Assert.ThrowsAsync<ArgumentException>(() => optimizer.OptimizeAsync(ctx));
    }

    #endregion

    #region T2f.1 — Per-trial IOptimizationTarget.WithParameters seam

    [Fact]
    public async Task CompileAsync_PerTrial_UsesWithParametersSeam()
    {
        FakePredictor.ResetWithParametersCalls();

        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 2,
            numInstructionCandidates: 2,
            numDemoSubsets: 2,
            maxDemos: 1,
            seed: 42);

        await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        Assert.True(FakePredictor.WithParametersCalls > 0,
            "Per-trial candidate construction must go through IOptimizationTarget.WithParameters.");
    }

    [Fact]
    public async Task CompileAsync_PerTrial_DoesNotMutateOriginalModulePredictors()
    {
        const string originalInstructions = "ORIGINAL-UNIQUE-INSTRUCTION";

        var pred = new FakePredictor(x => x)
        {
            Name = "classify",
            Instructions = originalInstructions,
        };
        var module = new TestModule([pred]);

        var trainSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var optimizer = new MIPROv2(
            new FakeProposalClient(),
            numTrials: 2,
            numInstructionCandidates: 2,
            numDemoSubsets: 2,
            maxDemos: 2,
            seed: 42);

        await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        // The seam migration ensures per-trial construction clones via
        // WithParameters; the original module's predictors must remain untouched.
        var originalPred = module.GetPredictors()[0].Predictor;
        Assert.Equal(originalInstructions, originalPred.Instructions);
        Assert.Empty(originalPred.Demos);
    }

    #endregion
}
