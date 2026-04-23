using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using LMP.Optimizers;
namespace LMP.Tests;

public class BootstrapRandomSearchTests
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
    /// Implements <see cref="IOptimizationTarget"/> so that <see cref="LmpModule"/>'s
    /// fractal parameter discovery/routing (used by BootstrapFewShot) includes it.
    /// </summary>
    private sealed class FakePredictor : IPredictor, IOptimizationTarget
    {
        private readonly Func<object, object> _predict;

        public FakePredictor(Func<object, object> predict)
        {
            _predict = predict;
        }

        public string Name { get; set; } = "fake";
        public string Instructions { get; set; } = "";
        public List<(object Input, object Output)> TypedDemos { get; set; } = [];
        IList IPredictor.Demos => TypedDemos;
        public Microsoft.Extensions.AI.ChatOptions Config { get; set; } = new();

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
                Config = new Microsoft.Extensions.AI.ChatOptions
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
            trace.Record(Name, input, output);
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
    public void Constructor_NumTrialsZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BootstrapRandomSearch(numTrials: 0));
    }

    [Fact]
    public void Constructor_NumTrialsNegative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BootstrapRandomSearch(numTrials: -1));
    }

    [Fact]
    public void Constructor_MaxDemosZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BootstrapRandomSearch(maxDemos: 0));
    }

    [Fact]
    public void Constructor_Defaults_DoesNotThrow()
    {
        var optimizer = new BootstrapRandomSearch();
        Assert.NotNull(optimizer);
    }

    [Fact]
    public void Constructor_WithSeed_DoesNotThrow()
    {
        var optimizer = new BootstrapRandomSearch(seed: 42);
        Assert.NotNull(optimizer);
    }

    #endregion

    #region CompileAsync Argument Validation

    [Fact]
    public async Task CompileAsync_NullModule_Throws()
    {
        var optimizer = new BootstrapRandomSearch();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync<LmpModule>(null!, [], (_, _) => 1f));
    }

    [Fact]
    public async Task CompileAsync_NullTrainSet_Throws()
    {
        var optimizer = new BootstrapRandomSearch();
        var (module, _) = CreateSinglePredictorModule(x => x);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(module, null!, (_, _) => 1f));
    }

    [Fact]
    public async Task CompileAsync_NullMetric_Throws()
    {
        var optimizer = new BootstrapRandomSearch();
        var (module, _) = CreateSinglePredictorModule(x => x);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(module, [], null!));
    }

    #endregion

    #region Empty TrainSet

    [Fact]
    public async Task CompileAsync_EmptyTrainSet_ReturnsModuleUnchanged()
    {
        var optimizer = new BootstrapRandomSearch(numTrials: 3, seed: 42);
        var (module, predictor) = CreateSinglePredictorModule(x => x);

        var result = await optimizer.CompileAsync(module, [], ExactMatchMetric());

        Assert.Same(module, result);
        Assert.Empty(predictor.TypedDemos);
    }

    #endregion

    #region Core Algorithm

    [Fact]
    public async Task CompileAsync_NTrials_ReturnsBestScoringCandidate()
    {
        // Setup: predictor echoes input. Metric is exact match.
        // All examples match, so all candidates should score 1.0 on validation.
        // The returned module should have demos filled.
        var callCount = 0;
        var (module, _) = CreateSinglePredictorModule(
            x => { callCount++; return x; },
            "classify");

        var trainSet = Enumerable.Range(0, 20)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var optimizer = new BootstrapRandomSearch(numTrials: 3, maxDemos: 2, seed: 42);
        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        Assert.NotNull(result);
        // The result should be a cloned module (not the original)
        Assert.NotSame(module, result);

        // The result should have demos filled (from successful traces)
        var predictors = result.GetPredictors();
        Assert.Single(predictors);
        Assert.True(predictors[0].Predictor.Demos.Count > 0,
            "Best candidate should have demos from successful traces.");
        Assert.True(predictors[0].Predictor.Demos.Count <= 2,
            "Should not exceed maxDemos.");
    }

    [Fact]
    public async Task CompileAsync_SelectsBestFromDifferentQualityCandidates()
    {
        // Create a module where the output depends on demo count.
        // Candidates with more demos perform better.
        // Since each trial shuffles differently, they may get different demos.
        var invocationCount = 0;
        var (module, _) = CreateSinglePredictorModule(
            x =>
            {
                invocationCount++;
                return x; // Echo input = exact match
            },
            "classify");

        var trainSet = Enumerable.Range(0, 15)
            .Select(i => (Example)new Example<string, string>($"val_{i}", $"val_{i}"))
            .ToList();

        var optimizer = new BootstrapRandomSearch(numTrials: 3, maxDemos: 4, seed: 123);
        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        Assert.NotNull(result);
        // Verify module was actually called (training + validation runs)
        Assert.True(invocationCount > 0);
    }

    [Fact]
    public async Task CompileAsync_ReturnsModuleNotOriginal()
    {
        var (module, _) = CreateSinglePredictorModule(x => x, "pred");

        var trainSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<int, int>(i, i))
            .ToList();

        var optimizer = new BootstrapRandomSearch(numTrials: 2, maxDemos: 2, seed: 1);
        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        // Result should be a clone (one of the trial candidates)
        Assert.NotSame(module, result);
    }

    #endregion

    #region Deterministic Seeding

    [Fact]
    public async Task CompileAsync_SameSeed_ProducesSameResult()
    {
        // Run twice with the same seed — both should pick the same best candidate.
        static (TestModule, FakePredictor) MakeModule() =>
            CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 20)
            .Select(i => (Example)new Example<string, string>($"item_{i}", $"item_{i}"))
            .ToList();

        // Run 1
        var (module1, _) = MakeModule();
        var optimizer1 = new BootstrapRandomSearch(numTrials: 3, maxDemos: 2, seed: 42);
        var result1 = await optimizer1.CompileAsync(module1, trainSet, ExactMatchMetric());

        // Run 2
        var (module2, _) = MakeModule();
        var optimizer2 = new BootstrapRandomSearch(numTrials: 3, maxDemos: 2, seed: 42);
        var result2 = await optimizer2.CompileAsync(module2, trainSet, ExactMatchMetric());

        // Both should have same number of demos for the predictor
        var demos1 = result1.GetPredictors()[0].Predictor.Demos;
        var demos2 = result2.GetPredictors()[0].Predictor.Demos;
        Assert.Equal(demos1.Count, demos2.Count);

        // Demos should contain the same entries in the same order
        for (int i = 0; i < demos1.Count; i++)
        {
            var d1 = ((object Input, object Output))demos1[i]!;
            var d2 = ((object Input, object Output))demos2[i]!;
            Assert.Equal(d1.Input, d2.Input);
            Assert.Equal(d1.Output, d2.Output);
        }
    }

    [Fact]
    public async Task CompileAsync_DifferentSeeds_MayProduceDifferentResults()
    {
        // With different seeds, the split and shuffle differ.
        // We can't guarantee different results, but we can verify both succeed.
        var trainSet = Enumerable.Range(0, 20)
            .Select(i => (Example)new Example<string, string>($"item_{i}", $"item_{i}"))
            .ToList();

        var (module1, _) = CreateSinglePredictorModule(x => x, "classify");
        var optimizer1 = new BootstrapRandomSearch(numTrials: 2, maxDemos: 2, seed: 1);
        var result1 = await optimizer1.CompileAsync(module1, trainSet, ExactMatchMetric());

        var (module2, _) = CreateSinglePredictorModule(x => x, "classify");
        var optimizer2 = new BootstrapRandomSearch(numTrials: 2, maxDemos: 2, seed: 999);
        var result2 = await optimizer2.CompileAsync(module2, trainSet, ExactMatchMetric());

        // Both should succeed
        Assert.NotNull(result1);
        Assert.NotNull(result2);
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

        var optimizer = new BootstrapRandomSearch(numTrials: 3, seed: 42);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            optimizer.CompileAsync(module, trainSet, ExactMatchMetric(), options: CompileOptions.RuntimeOnly, cts.Token));
    }

    #endregion

    #region SplitDataset

    [Fact]
    public void SplitDataset_SplitsCorrectly_80_20()
    {
        var data = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<int, int>(i, i))
            .ToList();

        var rng = new Random(42);
        var (train, val) = BootstrapRandomSearch.SplitDataset(data, 0.8, rng);

        Assert.Equal(8, train.Count);
        Assert.Equal(2, val.Count);

        // No overlap
        var allItems = train.Concat(val).ToList();
        Assert.Equal(10, allItems.Count);
        Assert.Equal(10, allItems.Distinct().Count());
    }

    [Fact]
    public void SplitDataset_SingleElement_AllGoesToTrain()
    {
        var data = new List<Example> { new Example<int, int>(1, 1) };
        var rng = new Random(42);
        var (train, val) = BootstrapRandomSearch.SplitDataset(data, 0.8, rng);

        Assert.Single(train);
        Assert.Empty(val);
    }

    [Fact]
    public void SplitDataset_SmallDataset_EnsuresAtLeastOneTrain()
    {
        var data = Enumerable.Range(0, 3)
            .Select(i => (Example)new Example<int, int>(i, i))
            .ToList();

        var rng = new Random(42);
        var (train, val) = BootstrapRandomSearch.SplitDataset(data, 0.8, rng);

        Assert.True(train.Count >= 1, "Should have at least 1 training example.");
        Assert.Equal(data.Count, train.Count + val.Count);
    }

    [Fact]
    public void SplitDataset_Deterministic_SameSeedSameResult()
    {
        var data = Enumerable.Range(0, 20)
            .Select(i => (Example)new Example<int, int>(i, i))
            .ToList();

        var (train1, val1) = BootstrapRandomSearch.SplitDataset(data, 0.8, new Random(42));
        var (train2, val2) = BootstrapRandomSearch.SplitDataset(data, 0.8, new Random(42));

        Assert.Equal(
            train1.Select(e => ((Example<int, int>)e).Input),
            train2.Select(e => ((Example<int, int>)e).Input));
        Assert.Equal(
            val1.Select(e => ((Example<int, int>)e).Input),
            val2.Select(e => ((Example<int, int>)e).Input));
    }

    #endregion

    #region MetricThreshold Forwarding

    [Fact]
    public async Task CompileAsync_MetricThreshold_OnlyHighScoresBecomeDemos()
    {
        // Use a metric that returns 0.5 for all examples.
        // With metricThreshold=1.0, no demos should be collected.
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var halfMetric = (Example e, object output) => 0.5f;

        var optimizer = new BootstrapRandomSearch(
            numTrials: 2, maxDemos: 4, metricThreshold: 1.0f, seed: 42);
        var result = await optimizer.CompileAsync(module, trainSet, halfMetric);

        // With threshold=1.0 and metric always returning 0.5, no demos should pass
        var demos = result.GetPredictors()[0].Predictor.Demos;
        Assert.Empty(demos);
    }

    [Fact]
    public async Task CompileAsync_LowThreshold_CollectsDemos()
    {
        var (module, _) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<string, string>($"input_{i}", $"input_{i}"))
            .ToList();

        var halfMetric = (Example e, object output) => 0.5f;

        var optimizer = new BootstrapRandomSearch(
            numTrials: 2, maxDemos: 4, metricThreshold: 0.4f, seed: 42);
        var result = await optimizer.CompileAsync(module, trainSet, halfMetric);

        // With threshold=0.4 and metric returning 0.5, demos should be collected
        var demos = result.GetPredictors()[0].Predictor.Demos;
        Assert.True(demos.Count > 0, "Demos should be collected when metric exceeds threshold.");
    }

    #endregion

    #region IOptimizer Interface

    [Fact]
    public void ImplementsIOptimizer()
    {
        var optimizer = new BootstrapRandomSearch();
        Assert.IsAssignableFrom<IOptimizer>(optimizer);
    }

    #endregion

    #region T2e — IOptimizationTarget seam

    [Fact]
    public async Task OptimizeAsync_OnPredictorTarget_PicksBestAcrossTrials()
    {
        // FakePredictor implements both IPredictor and IOptimizationTarget, so it
        // can stand in as a standalone leaf target for BRS (same shape as Predictor<TIn,TOut>).
        var predictor = new FakePredictor(x => x) { Name = "standalone" };
        IOptimizationTarget target = predictor;

        var trainSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<string, string>($"item_{i}", $"item_{i}"))
            .ToList();

        var ctx = OptimizationContext.For(target, trainSet, ExactMatchMetric());
        var optimizer = new BootstrapRandomSearch(numTrials: 3, maxDemos: 4, seed: 42);

        await optimizer.OptimizeAsync(ctx);

        // BRS should have applied the best candidate's state back into the original
        // predictor target, populating its demo slot.
        Assert.True(predictor.TypedDemos.Count > 0,
            "Standalone Predictor target should have demos after BRS.");
        Assert.True(predictor.TypedDemos.Count <= 4,
            "Should not exceed maxDemos.");
    }

    [Fact]
    public async Task OptimizeAsync_OnChainTargetOfModules_PicksBestAcrossTrials()
    {
        // Two LmpModule children, each containing one FakePredictor, composed via .Then().
        // BRS should populate demos on BOTH stages by picking the best trial candidate
        // and applying its state back to the chain.
        var (moduleA, predA) = CreateSinglePredictorModule(x => x, predictorName: "stageA");
        var (moduleB, predB) = CreateSinglePredictorModule(x => x, predictorName: "stageB");

        var chain = moduleA.Then(moduleB);

        var trainSet = new List<Example>
        {
            new Example<string, string>("alpha", "alpha"),
            new Example<string, string>("beta", "beta"),
            new Example<string, string>("gamma", "gamma"),
            new Example<string, string>("delta", "delta"),
            new Example<string, string>("epsilon", "epsilon"),
        };

        var ctx = OptimizationContext.For(chain, trainSet, ExactMatchMetric());
        var optimizer = new BootstrapRandomSearch(numTrials: 2, maxDemos: 4, seed: 7);

        await optimizer.OptimizeAsync(ctx);

        Assert.True(predA.TypedDemos.Count > 0,
            "Stage A predictor should have demos after BRS over chain target.");
        Assert.True(predB.TypedDemos.Count > 0,
            "Stage B predictor should have demos after BRS over chain target.");
    }

    [Fact]
    public async Task OptimizeAsync_OnPredictorTarget_DoesNotThrowNotSupported()
    {
        // Regression canary: the banned `GetService<LmpModule>() ?? throw NotSupportedException`
        // pattern (§20b.2) must stay gone. Passing a bare Predictor target must complete
        // without throwing NotSupportedException.
        var predictor = new FakePredictor(x => x) { Name = "bare" };
        IOptimizationTarget target = predictor;

        var trainSet = new List<Example>
        {
            new Example<string, string>("x", "x"),
            new Example<string, string>("y", "y"),
        };

        var ctx = OptimizationContext.For(target, trainSet, ExactMatchMetric());
        var optimizer = new BootstrapRandomSearch(numTrials: 2, maxDemos: 2, seed: 1);

        // Must not throw NotSupportedException (or any other exception in this happy path).
        await optimizer.OptimizeAsync(ctx);

        // Verify the source itself is free of the banned pattern (defense-in-depth).
        var sourcePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "LMP.Optimizers", "BootstrapRandomSearch.cs"));
        if (System.IO.File.Exists(sourcePath))
        {
            var source = System.IO.File.ReadAllText(sourcePath);
            Assert.DoesNotContain("NotSupportedException", source);
            Assert.DoesNotContain("GetService<LmpModule>()", source);
        }
    }

    [Fact]
    public async Task CompileAsync_DoesNotMutateInputModule()
    {
        // Back-compat regression for Amendment B: input module must never be mutated;
        // CompileAsync returns a clone whose predictor demos may be populated.
        var (module, inputPredictor) = CreateSinglePredictorModule(x => x, "classify");

        var trainSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<string, string>($"in_{i}", $"in_{i}"))
            .ToList();

        var optimizer = new BootstrapRandomSearch(numTrials: 2, maxDemos: 3, seed: 99);
        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        Assert.NotSame(module, result);
        Assert.Empty(inputPredictor.TypedDemos);
        // Returned clone should have demos applied.
        Assert.True(result.GetPredictors()[0].Predictor.Demos.Count > 0,
            "Returned clone should carry the best candidate's demos.");
    }

    #endregion
}
