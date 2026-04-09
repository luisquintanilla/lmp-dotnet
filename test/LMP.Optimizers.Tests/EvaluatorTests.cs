using LMP.Optimizers;

namespace LMP.Tests;

public class EvaluatorTests
{
    #region Test Infrastructure

    private sealed class EchoModule : LmpModule
    {
        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(input);
        }
    }

    private sealed class ConstantModule : LmpModule
    {
        private readonly object _output;

        public ConstantModule(object output) => _output = output;

        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_output);
        }
    }

    private sealed class TrackingModule : LmpModule
    {
        private int _callCount;
        public int CallCount => _callCount;

        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(input);
        }
    }

    private sealed class DelayModule : LmpModule
    {
        private readonly TimeSpan _delay;

        public DelayModule(TimeSpan delay) => _delay = delay;

        public override async Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return input;
        }
    }

    private sealed class FailingModule : LmpModule
    {
        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Module failed");
        }
    }

    #endregion

    #region Argument Validation

    [Fact]
    public async Task EvaluateAsync_NullModule_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Evaluator.EvaluateAsync<LmpModule>(
                null!, [], (_, _) => 1f));
    }

    [Fact]
    public async Task EvaluateAsync_NullDevSet_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Evaluator.EvaluateAsync(
                new EchoModule(), null!, (_, _) => 1f));
    }

    [Fact]
    public async Task EvaluateAsync_NullMetric_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Evaluator.EvaluateAsync(
                new EchoModule(), [], null!));
    }

    [Fact]
    public async Task EvaluateAsync_ZeroConcurrency_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Evaluator.EvaluateAsync(
                new EchoModule(), [], (_, _) => 1f, maxConcurrency: 0));
    }

    [Fact]
    public async Task EvaluateAsync_NegativeConcurrency_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Evaluator.EvaluateAsync(
                new EchoModule(), [], (_, _) => 1f, maxConcurrency: -1));
    }

    #endregion

    #region Empty DevSet

    [Fact]
    public async Task EvaluateAsync_EmptyDevSet_ReturnsZeroes()
    {
        var result = await Evaluator.EvaluateAsync(
            new EchoModule(), [], (_, _) => 1f);

        Assert.Empty(result.PerExample);
        Assert.Equal(0f, result.AverageScore);
        Assert.Equal(0f, result.MinScore);
        Assert.Equal(0f, result.MaxScore);
        Assert.Equal(0, result.Count);
    }

    #endregion

    #region Basic Evaluation

    [Fact]
    public async Task EvaluateAsync_SingleExample_ReturnsCorrectResult()
    {
        var module = new EchoModule();
        var devSet = new List<Example>
        {
            new Example<string, string>("input1", "expected1")
        };

        var result = await Evaluator.EvaluateAsync(
            module, devSet, (_, _) => 0.8f);

        Assert.Equal(1, result.Count);
        Assert.Equal(0.8f, result.AverageScore);
        Assert.Equal(0.8f, result.MinScore);
        Assert.Equal(0.8f, result.MaxScore);
        Assert.Single(result.PerExample);
        Assert.Equal(0.8f, result.PerExample[0].Score);
    }

    [Fact]
    public async Task EvaluateAsync_MultipleExamples_CorrectAggregates()
    {
        var module = new EchoModule();
        var devSet = new List<Example>
        {
            new Example<string, string>("a", "la"),
            new Example<string, string>("b", "lb"),
            new Example<string, string>("c", "lc"),
            new Example<string, string>("d", "ld"),
            new Example<string, string>("e", "le"),
        };

        // Metric returns different scores based on input
        var result = await Evaluator.EvaluateAsync(
            module, devSet, (example, output) =>
            {
                return (string)output! switch
                {
                    "a" => 0.2f,
                    "b" => 0.4f,
                    "c" => 0.6f,
                    "d" => 0.8f,
                    "e" => 1.0f,
                    _ => 0f,
                };
            });

        Assert.Equal(5, result.Count);
        Assert.Equal(0.6f, result.AverageScore, precision: 4);
        Assert.Equal(0.2f, result.MinScore);
        Assert.Equal(1.0f, result.MaxScore);
        Assert.Equal(5, result.PerExample.Count);
    }

    [Fact]
    public async Task EvaluateAsync_AllPerfectScores_AverageIsOne()
    {
        var module = new EchoModule();
        var devSet = new List<Example>
        {
            new Example<string, string>("a", "a"),
            new Example<string, string>("b", "b"),
            new Example<string, string>("c", "c"),
        };

        var result = await Evaluator.EvaluateAsync(
            module, devSet, (_, _) => 1.0f);

        Assert.Equal(1.0f, result.AverageScore);
        Assert.Equal(1.0f, result.MinScore);
        Assert.Equal(1.0f, result.MaxScore);
    }

    [Fact]
    public async Task EvaluateAsync_AllZeroScores_AverageIsZero()
    {
        var module = new EchoModule();
        var devSet = new List<Example>
        {
            new Example<string, string>("a", "a"),
            new Example<string, string>("b", "b"),
        };

        var result = await Evaluator.EvaluateAsync(
            module, devSet, (_, _) => 0f);

        Assert.Equal(0f, result.AverageScore);
        Assert.Equal(0f, result.MinScore);
        Assert.Equal(0f, result.MaxScore);
    }

    #endregion

    #region Metric Receives Correct Data

    [Fact]
    public async Task EvaluateAsync_MetricReceivesExampleAndOutput()
    {
        var module = new ConstantModule("module_output");
        var example = new Example<string, string>("input1", "label1");
        var devSet = new List<Example> { example };

        Example? capturedExample = null;
        object? capturedOutput = null;

        await Evaluator.EvaluateAsync(
            module, devSet, (ex, output) =>
            {
                capturedExample = ex;
                capturedOutput = output;
                return 1.0f;
            });

        Assert.Same(example, capturedExample);
        Assert.Equal("module_output", capturedOutput);
    }

    [Fact]
    public async Task EvaluateAsync_MetricCanAccessExampleLabel()
    {
        var module = new ConstantModule("billing");
        var devSet = new List<Example>
        {
            new Example<string, string>("ticket text", "billing"),
            new Example<string, string>("other text", "support"),
        };

        var result = await Evaluator.EvaluateAsync(
            module, devSet, (example, output) =>
            {
                var label = (string)example.GetLabel();
                var actual = (string)output;
                return label == actual ? 1.0f : 0f;
            });

        // Module always returns "billing", so only the first example matches
        Assert.Equal(2, result.Count);
        var scores = result.PerExample.Select(r => r.Score).OrderBy(s => s).ToArray();
        Assert.Equal(0f, scores[0]);
        Assert.Equal(1.0f, scores[1]);
        Assert.Equal(0.5f, result.AverageScore);
    }

    #endregion

    #region Module ForwardAsync Integration

    [Fact]
    public async Task EvaluateAsync_CallsForwardAsyncForEachExample()
    {
        var module = new TrackingModule();
        var devSet = new List<Example>
        {
            new Example<string, string>("a", "la"),
            new Example<string, string>("b", "lb"),
            new Example<string, string>("c", "lc"),
        };

        await Evaluator.EvaluateAsync(
            module, devSet, (_, _) => 1f);

        Assert.Equal(3, module.CallCount);
    }

    [Fact]
    public async Task EvaluateAsync_PassesWithInputsToForwardAsync()
    {
        var receivedInputs = new System.Collections.Concurrent.ConcurrentBag<object>();
        var module = new InputCapturingModule(receivedInputs);
        var devSet = new List<Example>
        {
            new Example<string, string>("input_a", "la"),
            new Example<string, string>("input_b", "lb"),
        };

        await Evaluator.EvaluateAsync(
            module, devSet, (_, _) => 1f);

        var inputs = receivedInputs.OrderBy(x => x.ToString()).ToArray();
        Assert.Equal(2, inputs.Length);
        Assert.Equal("input_a", inputs[0]);
        Assert.Equal("input_b", inputs[1]);
    }

    [Fact]
    public async Task EvaluateAsync_OutputStoredInResult()
    {
        var module = new ConstantModule("fixed_output");
        var devSet = new List<Example>
        {
            new Example<string, string>("a", "la"),
        };

        var result = await Evaluator.EvaluateAsync(
            module, devSet, (_, _) => 1f);

        Assert.Equal("fixed_output", result.PerExample[0].Output);
    }

    #endregion

    #region Concurrency

    [Fact]
    public async Task EvaluateAsync_RespectsConcurrencyLimit()
    {
        int peakConcurrency = 0;
        int currentConcurrency = 0;
        var module = new ConcurrencyTrackingModule(
            () => Interlocked.Increment(ref currentConcurrency),
            () =>
            {
                int val = currentConcurrency;
                int prev;
                do
                {
                    prev = peakConcurrency;
                    if (val <= prev) break;
                }
                while (Interlocked.CompareExchange(ref peakConcurrency, val, prev) != prev);
            },
            () => Interlocked.Decrement(ref currentConcurrency));

        var devSet = Enumerable.Range(0, 20)
            .Select(i => (Example)new Example<int, int>(i, i))
            .ToList();

        await Evaluator.EvaluateAsync(
            module, devSet, (_, _) => 1f, maxConcurrency: 2);

        Assert.True(peakConcurrency <= 2,
            $"Peak concurrency was {peakConcurrency}, expected <= 2");
    }

    [Fact]
    public async Task EvaluateAsync_DefaultConcurrency_ProcessesAll()
    {
        var module = new TrackingModule();
        var devSet = Enumerable.Range(0, 10)
            .Select(i => (Example)new Example<int, int>(i, i))
            .ToList();

        var result = await Evaluator.EvaluateAsync(
            module, devSet, (_, _) => 1f);

        Assert.Equal(10, result.Count);
        Assert.Equal(10, module.CallCount);
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task EvaluateAsync_CancellationToken_HonorsCancellation()
    {
        var cts = new CancellationTokenSource();
        var module = new DelayModule(TimeSpan.FromSeconds(10));
        var devSet = Enumerable.Range(0, 100)
            .Select(i => (Example)new Example<int, int>(i, i))
            .ToList();

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Evaluator.EvaluateAsync(
                module, devSet, (_, _) => 1f,
                cancellationToken: cts.Token));
    }

    #endregion

    #region Error Propagation

    [Fact]
    public async Task EvaluateAsync_ModuleThrows_PropagatesException()
    {
        var module = new FailingModule();
        var devSet = new List<Example>
        {
            new Example<string, string>("a", "la")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Evaluator.EvaluateAsync(module, devSet, (_, _) => 1f));
    }

    #endregion

    #region Result Record Types

    [Fact]
    public void EvaluationResult_RecordEquality()
    {
        var ex = new Example<string, string>("a", "la");
        var perExample = new List<ExampleResult>
        {
            new(ex, "output", 0.5f)
        };

        var r1 = new EvaluationResult(perExample, 0.5f, 0.5f, 0.5f, 1);
        var r2 = new EvaluationResult(perExample, 0.5f, 0.5f, 0.5f, 1);

        Assert.Equal(r1, r2);
    }

    [Fact]
    public void ExampleResult_RecordEquality()
    {
        var ex = new Example<string, string>("a", "la");
        var r1 = new ExampleResult(ex, "output", 0.9f);
        var r2 = new ExampleResult(ex, "output", 0.9f);

        Assert.Equal(r1, r2);
    }

    [Fact]
    public void ExampleResult_Properties()
    {
        var ex = new Example<string, string>("input", "label");
        var result = new ExampleResult(ex, "output", 0.75f);

        Assert.Same(ex, result.Example);
        Assert.Equal("output", result.Output);
        Assert.Equal(0.75f, result.Score);
    }

    #endregion

    #region Test Helpers

    private sealed class InputCapturingModule : LmpModule
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<object> _captured;

        public InputCapturingModule(System.Collections.Concurrent.ConcurrentBag<object> captured)
            => _captured = captured;

        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            _captured.Add(input);
            return Task.FromResult(input);
        }
    }

    private sealed class ConcurrencyTrackingModule : LmpModule
    {
        private readonly Action _onEnter;
        private readonly Action _onTrack;
        private readonly Action _onExit;

        public ConcurrencyTrackingModule(Action onEnter, Action onTrack, Action onExit)
        {
            _onEnter = onEnter;
            _onTrack = onTrack;
            _onExit = onExit;
        }

        public override async Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
        {
            _onEnter();
            _onTrack();
            await Task.Delay(50, cancellationToken);
            _onExit();
            return input;
        }
    }

    #endregion
}
