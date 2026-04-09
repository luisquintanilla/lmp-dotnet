using System.Collections;
using System.Collections.Concurrent;
using LMP.Optimizers;
using Microsoft.Extensions.AI;
using Moq;

namespace LMP.Tests;

public class BootstrapFewShotTests
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
    /// Does NOT require an IChatClient — it simulates predictions directly.
    /// </summary>
    private sealed class FakePredictor : IPredictor
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

            // Default: run each predictor sequentially, return last output
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
    /// Creates a simple TestModule with a single predictor.
    /// </summary>
    private static (TestModule Module, FakePredictor Predictor) CreateSinglePredictorModule(
        Func<object, object> predict,
        string predictorName = "classify")
    {
        var predictor = new FakePredictor(predict) { Name = predictorName };
        var module = new TestModule([predictor]);
        return (module, predictor);
    }

    /// <summary>
    /// Creates a simple metric that returns 1.0 when output matches label.
    /// </summary>
    private static Func<Example, object, float> ExactMatchMetric()
        => (example, output) =>
        {
            var label = example.GetLabel();
            return Equals(label, output) ? 1.0f : 0f;
        };

    #endregion

    #region Argument Validation

    [Fact]
    public async Task CompileAsync_NullModule_Throws()
    {
        var optimizer = new BootstrapFewShot();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync<LmpModule>(null!, [], (_, _) => 1f));
    }

    [Fact]
    public async Task CompileAsync_NullTrainSet_Throws()
    {
        var optimizer = new BootstrapFewShot();
        var (module, _) = CreateSinglePredictorModule(x => x);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(module, null!, (_, _) => 1f));
    }

    [Fact]
    public async Task CompileAsync_NullMetric_Throws()
    {
        var optimizer = new BootstrapFewShot();
        var (module, _) = CreateSinglePredictorModule(x => x);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            optimizer.CompileAsync(module, [], null!));
    }

    [Fact]
    public void Constructor_MaxDemosZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BootstrapFewShot(maxDemos: 0));
    }

    [Fact]
    public void Constructor_MaxRoundsZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BootstrapFewShot(maxRounds: 0));
    }

    #endregion

    #region Empty TrainSet

    [Fact]
    public async Task CompileAsync_EmptyTrainSet_ReturnsModuleUnchanged()
    {
        var optimizer = new BootstrapFewShot();
        var (module, predictor) = CreateSinglePredictorModule(x => x);

        var result = await optimizer.CompileAsync(module, [], (_, _) => 1f);

        Assert.Same(module, result);
        Assert.Empty(predictor.TypedDemos);
    }

    #endregion

    #region Basic Bootstrapping

    [Fact]
    public async Task CompileAsync_AllSuccessful_FillsDemos()
    {
        var optimizer = new BootstrapFewShot(maxDemos: 4, metricThreshold: 1.0f);
        var (module, predictor) = CreateSinglePredictorModule(x => $"output_{x}");

        var trainSet = new List<Example>
        {
            new Example<string, string>("input1", "output_input1"),
            new Example<string, string>("input2", "output_input2"),
            new Example<string, string>("input3", "output_input3"),
        };

        var result = await optimizer.CompileAsync(
            module, trainSet, ExactMatchMetric());

        Assert.Same(module, result);
        Assert.Equal(3, predictor.TypedDemos.Count);
    }

    [Fact]
    public async Task CompileAsync_DemosContainCorrectInputOutput()
    {
        var optimizer = new BootstrapFewShot(maxDemos: 4, metricThreshold: 1.0f);
        var (module, predictor) = CreateSinglePredictorModule(x => $"classified_{x}");

        var trainSet = new List<Example>
        {
            new Example<string, string>("ticket", "classified_ticket"),
        };

        await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        Assert.Single(predictor.TypedDemos);
        Assert.Equal("ticket", predictor.TypedDemos[0].Input);
        Assert.Equal("classified_ticket", predictor.TypedDemos[0].Output);
    }

    [Fact]
    public async Task CompileAsync_ReturnsSameModuleInstance()
    {
        var optimizer = new BootstrapFewShot();
        var (module, _) = CreateSinglePredictorModule(x => x);

        var result = await optimizer.CompileAsync(
            module, [], (_, _) => 1f);

        Assert.Same(module, result);
    }

    #endregion

    #region Metric Threshold Filtering

    [Fact]
    public async Task CompileAsync_BelowThreshold_NoDemosAdded()
    {
        var optimizer = new BootstrapFewShot(maxDemos: 4, metricThreshold: 0.8f);
        var (module, predictor) = CreateSinglePredictorModule(x => "wrong_answer");

        var trainSet = new List<Example>
        {
            new Example<string, string>("input1", "correct_answer"),
            new Example<string, string>("input2", "correct_answer"),
        };

        await optimizer.CompileAsync(
            module, trainSet, (example, output) =>
            {
                return Equals(example.GetLabel(), output) ? 1.0f : 0.3f;
            });

        Assert.Empty(predictor.TypedDemos);
    }

    [Fact]
    public async Task CompileAsync_MixedScores_OnlyHighScoringBecameDemos()
    {
        var optimizer = new BootstrapFewShot(maxDemos: 10, metricThreshold: 0.5f);
        // Predictor echoes input
        var (module, predictor) = CreateSinglePredictorModule(x => x);

        var trainSet = new List<Example>
        {
            new Example<string, string>("good1", "good1"),  // exact match -> 1.0
            new Example<string, string>("good2", "good2"),  // exact match -> 1.0
            new Example<string, string>("bad1", "other"),   // mismatch -> 0.0
        };

        await optimizer.CompileAsync(
            module, trainSet, ExactMatchMetric());

        // Only the 2 matching examples should become demos
        Assert.Equal(2, predictor.TypedDemos.Count);
        var inputs = predictor.TypedDemos.Select(d => d.Input).OrderBy(x => x).ToList();
        Assert.Equal("good1", inputs[0]);
        Assert.Equal("good2", inputs[1]);
    }

    [Fact]
    public async Task CompileAsync_ExactThreshold_IncludesDemos()
    {
        var optimizer = new BootstrapFewShot(maxDemos: 4, metricThreshold: 0.5f);
        var (module, predictor) = CreateSinglePredictorModule(x => x);

        var trainSet = new List<Example>
        {
            new Example<string, string>("a", "a"),
        };

        // Metric returns exactly the threshold
        await optimizer.CompileAsync(
            module, trainSet, (_, _) => 0.5f);

        Assert.Single(predictor.TypedDemos);
    }

    #endregion

    #region MaxDemos Limit

    [Fact]
    public async Task CompileAsync_ExceedsMaxDemos_TruncatesToLimit()
    {
        var optimizer = new BootstrapFewShot(maxDemos: 2, metricThreshold: 0.0f);
        var (module, predictor) = CreateSinglePredictorModule(x => x);

        var trainSet = Enumerable.Range(0, 5)
            .Select(i => (Example)new Example<string, string>($"input{i}", $"input{i}"))
            .ToList();

        await optimizer.CompileAsync(module, trainSet, (_, _) => 1.0f);

        Assert.Equal(2, predictor.TypedDemos.Count);
    }

    #endregion

    #region Failed Examples Are Swallowed

    [Fact]
    public async Task CompileAsync_FailedExamples_Swallowed()
    {
        int callCount = 0;
        var (module, predictor) = CreateSinglePredictorModule(x =>
        {
            callCount++;
            if ((string)x == "bad_input")
                throw new InvalidOperationException("LLM call failed");
            return x;
        });

        var optimizer = new BootstrapFewShot(maxDemos: 4, metricThreshold: 0.0f);
        var trainSet = new List<Example>
        {
            new Example<string, string>("good1", "good1"),
            new Example<string, string>("bad_input", "whatever"),
            new Example<string, string>("good2", "good2"),
        };

        // Should not throw
        await optimizer.CompileAsync(module, trainSet, (_, _) => 1.0f);

        // The successful examples should still produce demos
        Assert.Equal(2, predictor.TypedDemos.Count);
    }

    [Fact]
    public async Task CompileAsync_OperationCanceled_Propagates()
    {
        var (module, _) = CreateSinglePredictorModule(x =>
        {
            throw new OperationCanceledException();
        });

        var optimizer = new BootstrapFewShot();
        var trainSet = new List<Example>
        {
            new Example<string, string>("input", "label"),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            optimizer.CompileAsync(module, trainSet, (_, _) => 1.0f));
    }

    [Fact]
    public async Task CompileAsync_CancellationToken_Honored()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var (module, _) = CreateSinglePredictorModule(x => x);
        var optimizer = new BootstrapFewShot();
        var trainSet = new List<Example>
        {
            new Example<string, string>("input", "label"),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            optimizer.CompileAsync(module, trainSet, (_, _) => 1.0f, cts.Token));
    }

    #endregion

    #region Multi-Predictor Module

    [Fact]
    public async Task CompileAsync_MultiPredictorModule_FillsEachPredictor()
    {
        var pred1 = new FakePredictor(x => $"p1_{x}") { Name = "extract" };
        var pred2 = new FakePredictor(x => $"p2_{x}") { Name = "classify" };

        // ForwardAsync calls both predictors and returns second's output
        var module = new TestModule([pred1, pred2], async (input, mod, ct) =>
        {
            var out1 = pred1.Predict(input, mod.Trace);
            var out2 = pred2.Predict(out1, mod.Trace);
            return out2;
        });

        var trainSet = new List<Example>
        {
            new Example<string, string>("text1", "p2_p1_text1"),
            new Example<string, string>("text2", "p2_p1_text2"),
        };

        var optimizer = new BootstrapFewShot(maxDemos: 4, metricThreshold: 1.0f);
        await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        // Both predictors should have demos
        Assert.Equal(2, pred1.TypedDemos.Count);
        Assert.Equal(2, pred2.TypedDemos.Count);
    }

    [Fact]
    public async Task CompileAsync_MultiPredictorModule_IndependentDemoLists()
    {
        var pred1 = new FakePredictor(x => $"p1_{x}") { Name = "step1" };
        var pred2 = new FakePredictor(x => $"p2_{x}") { Name = "step2" };

        var module = new TestModule([pred1, pred2]);

        var trainSet = new List<Example>
        {
            new Example<string, string>("input", "p2_input"),
        };

        var optimizer = new BootstrapFewShot(maxDemos: 4, metricThreshold: 1.0f);
        await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        // pred1 gets (input, p1_input); pred2 gets (input, p2_input)
        Assert.Single(pred1.TypedDemos);
        Assert.Equal("input", pred1.TypedDemos[0].Input);
        Assert.Equal("p1_input", pred1.TypedDemos[0].Output);

        Assert.Single(pred2.TypedDemos);
        Assert.Equal("input", pred2.TypedDemos[0].Input);
        Assert.Equal("p2_input", pred2.TypedDemos[0].Output);
    }

    #endregion

    #region Teacher/Student Isolation

    [Fact]
    public async Task CompileAsync_TeacherIsClone_OriginalNotMutated()
    {
        var (module, predictor) = CreateSinglePredictorModule(x => x);
        predictor.Instructions = "Original instructions";

        var trainSet = new List<Example>
        {
            new Example<string, string>("input", "input"),
        };

        var optimizer = new BootstrapFewShot(metricThreshold: 1.0f);
        var result = await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        // The module is the student, instructions should be unchanged
        Assert.Equal("Original instructions", predictor.Instructions);
        Assert.Same(module, result);
    }

    #endregion

    #region IOptimizer Interface

    [Fact]
    public void BootstrapFewShot_ImplementsIOptimizer()
    {
        var optimizer = new BootstrapFewShot();
        Assert.IsAssignableFrom<IOptimizer>(optimizer);
    }

    #endregion

    #region Predictor.AddDemo Integration

    [Fact]
    public async Task CompileAsync_WithRealPredictor_AddsDemos()
    {
        var client = new Mock<IChatClient>().Object;
        var predictor = new Predictor<string, TestOutput>(client) { Name = "classify" };

        var module = new TestModuleWithRealPredictor(predictor);

        // ForwardAsync returns the input as string (doesn't call LLM)
        var trainSet = new List<Example>
        {
            new Example<string, string>("hello", "hello"),
        };

        var optimizer = new BootstrapFewShot(metricThreshold: 1.0f);
        await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        // The real predictor should have a demo added via AddDemo
        Assert.Single(predictor.Demos);
        Assert.Equal("hello", predictor.Demos[0].Input);
    }

    /// <summary>
    /// A test module wrapping a real Predictor that records traces and supports Clone.
    /// ForwardAsync does NOT actually call the LLM; it just records trace and returns input.
    /// </summary>
    private sealed class TestModuleWithRealPredictor : LmpModule
    {
        private Predictor<string, TestOutput> _predictor;

        public TestModuleWithRealPredictor(Predictor<string, TestOutput> predictor)
        {
            _predictor = predictor;
        }

        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            // Record trace as if the predictor ran
            var output = new TestOutput { Value = (string)input };
            Trace?.Record(_predictor.Name, input, output);
            return Task.FromResult<object>(input);
        }

        public override IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors()
            => [(_predictor.Name, _predictor)];

        protected override LmpModule CloneCore()
        {
            var clonedPredictor = (Predictor<string, TestOutput>)_predictor.Clone();
            return new TestModuleWithRealPredictor(clonedPredictor);
        }
    }

    #endregion

    #region Existing Demos Cleared

    [Fact]
    public async Task CompileAsync_ClearsExistingDemosBeforeFilling()
    {
        var (module, predictor) = CreateSinglePredictorModule(x => x);
        // Add pre-existing demos
        predictor.TypedDemos.Add(("old_input", "old_output"));
        predictor.TypedDemos.Add(("old_input2", "old_output2"));

        var trainSet = new List<Example>
        {
            new Example<string, string>("new_input", "new_input"),
        };

        var optimizer = new BootstrapFewShot(maxDemos: 4, metricThreshold: 1.0f);
        await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        // Existing demos should be replaced, not appended
        Assert.Single(predictor.TypedDemos);
        Assert.Equal("new_input", predictor.TypedDemos[0].Input);
    }

    #endregion

    #region No Matching Predictors

    [Fact]
    public async Task CompileAsync_NoSuccessfulTraces_DemosUnchanged()
    {
        var (module, predictor) = CreateSinglePredictorModule(x => "wrong");

        var trainSet = new List<Example>
        {
            new Example<string, string>("input", "expected"),
        };

        // Metric always returns 0 (below default threshold of 1.0)
        var optimizer = new BootstrapFewShot();
        await optimizer.CompileAsync(module, trainSet, (_, _) => 0f);

        Assert.Empty(predictor.TypedDemos);
    }

    #endregion

    #region Constructor Defaults

    [Fact]
    public async Task CompileAsync_DefaultParameters_Works()
    {
        // Default: maxDemos=4, maxRounds=1, metricThreshold=1.0
        var optimizer = new BootstrapFewShot();
        var (module, predictor) = CreateSinglePredictorModule(x => x);

        var trainSet = Enumerable.Range(0, 6)
            .Select(i => (Example)new Example<string, string>($"i{i}", $"i{i}"))
            .ToList();

        await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        // Default maxDemos=4, so even though 6 examples succeed, only 4 demos
        Assert.Equal(4, predictor.TypedDemos.Count);
    }

    #endregion

    #region Multi-Round Bootstrapping

    [Fact]
    public async Task CompileAsync_MultiRound_AccumulatesMoreTraces()
    {
        // With maxRounds=2, the teacher in round 2 has demos from round 1
        var optimizer = new BootstrapFewShot(maxDemos: 10, maxRounds: 2, metricThreshold: 0.0f);
        var (module, predictor) = CreateSinglePredictorModule(x => x);

        var trainSet = new List<Example>
        {
            new Example<string, string>("a", "a"),
            new Example<string, string>("b", "b"),
        };

        await optimizer.CompileAsync(module, trainSet, (_, _) => 1.0f);

        // 2 rounds × 2 examples = up to 4 trace entries (capped by maxDemos=10)
        Assert.True(predictor.TypedDemos.Count >= 2,
            $"Expected at least 2 demos, got {predictor.TypedDemos.Count}");
    }

    #endregion

    #region Trace Isolation

    [Fact]
    public async Task CompileAsync_TracesFromDifferentExamples_Isolated()
    {
        // Each example should get its own fresh trace
        var traceEntryCounts = new List<int>();

        var pred = new FakePredictor(x => x) { Name = "pred" };
        var module = new TestModule([pred], async (input, mod, ct) =>
        {
            // Record how many entries in the current trace before we add
            int before = mod.Trace?.Entries.Count ?? 0;
            var output = pred.Predict(input, mod.Trace);
            traceEntryCounts.Add(before);
            return output;
        });

        var trainSet = new List<Example>
        {
            new Example<string, string>("a", "a"),
            new Example<string, string>("b", "b"),
            new Example<string, string>("c", "c"),
        };

        var optimizer = new BootstrapFewShot(maxDemos: 10, metricThreshold: 1.0f);
        await optimizer.CompileAsync(module, trainSet, ExactMatchMetric());

        // Each example should start with 0 entries (fresh trace)
        Assert.All(traceEntryCounts, count => Assert.Equal(0, count));
    }

    #endregion

    #region Module Without Predictors

    [Fact]
    public async Task CompileAsync_ModuleWithNoPredictors_ReturnsUnchanged()
    {
        var module = new EmptyModule();
        var optimizer = new BootstrapFewShot();

        var trainSet = new List<Example>
        {
            new Example<string, string>("input", "input"),
        };

        var result = await optimizer.CompileAsync(module, trainSet, (_, _) => 1.0f);

        Assert.Same(module, result);
    }

    private sealed class EmptyModule : LmpModule
    {
        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);

        protected override LmpModule CloneCore()
            => new EmptyModule();
    }

    #endregion

    #region Large Training Set

    [Fact]
    public async Task CompileAsync_LargeTrainSet_ProcessesAll()
    {
        var callCount = 0;
        var (module, predictor) = CreateSinglePredictorModule(x =>
        {
            Interlocked.Increment(ref callCount);
            return x;
        });

        var trainSet = Enumerable.Range(0, 50)
            .Select(i => (Example)new Example<int, int>(i, i))
            .ToList();

        var optimizer = new BootstrapFewShot(maxDemos: 10, metricThreshold: 0.0f);
        await optimizer.CompileAsync(module, trainSet, (_, _) => 1.0f);

        // All examples should have been processed
        Assert.True(callCount >= 50,
            $"Expected at least 50 calls, got {callCount}");
        Assert.Equal(10, predictor.TypedDemos.Count);
    }

    #endregion
}
