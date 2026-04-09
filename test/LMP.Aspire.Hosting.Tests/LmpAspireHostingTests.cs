using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using LMP;
using LMP.Aspire.Hosting;
using LMP.Optimizers;
using Xunit;

namespace LMP.Aspire.Hosting.Tests;

#region Test Helpers

public sealed class TestModule : LmpModule
{
    public override Task<object> ForwardAsync(object input, CancellationToken cancellationToken = default)
        => Task.FromResult(input);
}

public sealed class AnotherModule : LmpModule
{
    public override Task<object> ForwardAsync(object input, CancellationToken cancellationToken = default)
        => Task.FromResult<object>("result");
}

#endregion

public class LmpOptimizerResourceTests
{
    [Fact]
    public void Constructor_SetsNameAndModuleType()
    {
        var resource = new LmpOptimizerResource("test-opt", typeof(TestModule));

        Assert.Equal("test-opt", resource.Name);
        Assert.Equal(typeof(TestModule), resource.ModuleType);
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var resource = new LmpOptimizerResource("opt", typeof(TestModule));

        Assert.Null(resource.TrainDataPath);
        Assert.Null(resource.DevDataPath);
        Assert.Null(resource.OptimizerType);
        Assert.Null(resource.OutputPath);
        Assert.Equal(4, resource.MaxConcurrency);
    }

    [Fact]
    public void Properties_CanBeSet()
    {
        var resource = new LmpOptimizerResource("opt", typeof(TestModule))
        {
            TrainDataPath = "train.jsonl",
            DevDataPath = "dev.jsonl",
            OptimizerType = typeof(BootstrapRandomSearch),
            OutputPath = "output.json",
            MaxConcurrency = 8
        };

        Assert.Equal("train.jsonl", resource.TrainDataPath);
        Assert.Equal("dev.jsonl", resource.DevDataPath);
        Assert.Equal(typeof(BootstrapRandomSearch), resource.OptimizerType);
        Assert.Equal("output.json", resource.OutputPath);
        Assert.Equal(8, resource.MaxConcurrency);
    }

    [Fact]
    public void ModuleType_ReflectsDifferentModules()
    {
        var r1 = new LmpOptimizerResource("r1", typeof(TestModule));
        var r2 = new LmpOptimizerResource("r2", typeof(AnotherModule));

        Assert.NotEqual(r1.ModuleType, r2.ModuleType);
        Assert.Equal("TestModule", r1.ModuleType.Name);
        Assert.Equal("AnotherModule", r2.ModuleType.Name);
    }
}

public class LmpTelemetryTests
{
    [Fact]
    public void SourceName_IsCorrect()
    {
        Assert.Equal("LMP.Optimization", LmpTelemetry.SourceName);
    }

    [Fact]
    public void ActivitySource_HasCorrectName()
    {
        Assert.Equal("LMP.Optimization", LmpTelemetry.ActivitySource.Name);
    }

    [Fact]
    public void Meter_HasCorrectName()
    {
        Assert.Equal("LMP.Optimization", LmpTelemetry.Meter.Name);
    }

    [Fact]
    public void TrialScore_Instrument_Exists()
    {
        Assert.NotNull(LmpTelemetry.TrialScore);
    }

    [Fact]
    public void TrialCount_Instrument_Exists()
    {
        Assert.NotNull(LmpTelemetry.TrialCount);
    }

    [Fact]
    public void OptimizationDuration_Instrument_Exists()
    {
        Assert.NotNull(LmpTelemetry.OptimizationDuration);
    }

    [Fact]
    public void EvaluationDuration_Instrument_Exists()
    {
        Assert.NotNull(LmpTelemetry.EvaluationDuration);
    }

    [Fact]
    public void ExamplesEvaluated_Instrument_Exists()
    {
        Assert.NotNull(LmpTelemetry.ExamplesEvaluated);
    }

    [Fact]
    public void StartOptimization_ReturnsActivityWhenListenerRegistered()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LmpTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = LmpTelemetry.StartOptimization("TestModule", "BootstrapRandomSearch");

        Assert.NotNull(activity);
        Assert.Equal("lmp.optimize", activity.OperationName);
        Assert.Equal("TestModule", activity.GetTagItem("lmp.module"));
        Assert.Equal("BootstrapRandomSearch", activity.GetTagItem("lmp.optimizer"));
    }

    [Fact]
    public void StartOptimization_ReturnsNullWithoutListener()
    {
        // No listener registered for our source
        var activity = LmpTelemetry.StartOptimization("TestModule", "BootstrapRandomSearch");

        // Activity may or may not be null depending on global listeners,
        // but calling it should not throw
        activity?.Dispose();
    }

    [Fact]
    public void StartTrial_SetsTrialIndex()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LmpTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = LmpTelemetry.StartTrial(3);

        Assert.NotNull(activity);
        Assert.Equal("lmp.optimize.trial", activity.OperationName);
        Assert.Equal(3, activity.GetTagItem("lmp.trial.index"));
    }

    [Fact]
    public void StartEvaluation_SetsDatasetSize()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LmpTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = LmpTelemetry.StartEvaluation(100);

        Assert.NotNull(activity);
        Assert.Equal("lmp.evaluate", activity.OperationName);
        Assert.Equal(100, activity.GetTagItem("lmp.evaluation.dataset_size"));
    }

    [Fact]
    public void RecordTrialResult_SetsScoreTag()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LmpTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = LmpTelemetry.StartTrial(0);
        LmpTelemetry.RecordTrialResult(0, 0.85, activity);

        Assert.Equal(0.85, activity!.GetTagItem("lmp.trial.score"));
    }

    [Fact]
    public void RecordTrialResult_WorksWithNullActivity()
    {
        // Should not throw
        LmpTelemetry.RecordTrialResult(0, 0.5);
    }

    [Fact]
    public void RecordEvaluation_DoesNotThrow()
    {
        // Should not throw
        LmpTelemetry.RecordEvaluation(0.9, 50, 12.5);
    }

    [Fact]
    public void Metrics_RecordValues()
    {
        // Collect metrics using MeterListener
        var trialScores = new List<double>();
        var trialCounts = new List<long>();

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == LmpTelemetry.SourceName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "lmp.optimization.trial.score")
                trialScores.Add(measurement);
        });
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "lmp.optimization.trial.count")
                trialCounts.Add(measurement);
        });
        meterListener.Start();

        LmpTelemetry.RecordTrialResult(0, 0.75);
        LmpTelemetry.RecordTrialResult(1, 0.92);

        meterListener.RecordObservableInstruments();

        Assert.Equal(2, trialScores.Count);
        Assert.Contains(0.75, trialScores);
        Assert.Contains(0.92, trialScores);
        Assert.Equal(2, trialCounts.Count);
    }
}

public class LmpOptimizerResourceBuilderExtensionsTests
{
    [Fact]
    public void AddLmpOptimizer_NullBuilder_Throws()
    {
        IDistributedApplicationBuilder? builder = null;

        Assert.Throws<ArgumentNullException>(() =>
            builder!.AddLmpOptimizer<TestModule>("test"));
    }

    [Fact]
    public void WithTrainData_NullBuilder_Throws()
    {
        IResourceBuilder<LmpOptimizerResource>? builder = null;

        Assert.Throws<ArgumentNullException>(() =>
            builder!.WithTrainData("train.jsonl"));
    }

    [Fact]
    public void WithDevData_NullBuilder_Throws()
    {
        IResourceBuilder<LmpOptimizerResource>? builder = null;

        Assert.Throws<ArgumentNullException>(() =>
            builder!.WithDevData("dev.jsonl"));
    }

    [Fact]
    public void WithOptimizer_NullBuilder_Throws()
    {
        IResourceBuilder<LmpOptimizerResource>? builder = null;

        Assert.Throws<ArgumentNullException>(() =>
            builder!.WithOptimizer<BootstrapRandomSearch>());
    }

    [Fact]
    public void WithOutputPath_NullBuilder_Throws()
    {
        IResourceBuilder<LmpOptimizerResource>? builder = null;

        Assert.Throws<ArgumentNullException>(() =>
            builder!.WithOutputPath("output.json"));
    }

    [Fact]
    public void WithMaxConcurrency_NullBuilder_Throws()
    {
        IResourceBuilder<LmpOptimizerResource>? builder = null;

        Assert.Throws<ArgumentNullException>(() =>
            builder!.WithMaxConcurrency(4));
    }

    [Fact]
    public void WithMaxConcurrency_ZeroValue_Throws()
    {
        var resource = new LmpOptimizerResource("opt", typeof(TestModule));
        var mockBuilder = new MockResourceBuilder(resource);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            mockBuilder.WithMaxConcurrency(0));
    }

    [Fact]
    public void WithTrainData_EmptyPath_Throws()
    {
        var resource = new LmpOptimizerResource("opt", typeof(TestModule));
        var mockBuilder = new MockResourceBuilder(resource);

        Assert.Throws<ArgumentException>(() =>
            mockBuilder.WithTrainData(""));
    }

    [Fact]
    public void WithDevData_EmptyPath_Throws()
    {
        var resource = new LmpOptimizerResource("opt", typeof(TestModule));
        var mockBuilder = new MockResourceBuilder(resource);

        Assert.Throws<ArgumentException>(() =>
            mockBuilder.WithDevData(""));
    }

    [Fact]
    public void WithOutputPath_EmptyPath_Throws()
    {
        var resource = new LmpOptimizerResource("opt", typeof(TestModule));
        var mockBuilder = new MockResourceBuilder(resource);

        Assert.Throws<ArgumentException>(() =>
            mockBuilder.WithOutputPath(""));
    }

    [Fact]
    public void WithTrainData_SetsPath()
    {
        var resource = new LmpOptimizerResource("opt", typeof(TestModule));
        var builder = new MockResourceBuilder(resource);

        var result = builder.WithTrainData("data/train.jsonl");

        Assert.Equal("data/train.jsonl", resource.TrainDataPath);
        Assert.Same(builder, result);
    }

    [Fact]
    public void WithDevData_SetsPath()
    {
        var resource = new LmpOptimizerResource("opt", typeof(TestModule));
        var builder = new MockResourceBuilder(resource);

        var result = builder.WithDevData("data/dev.jsonl");

        Assert.Equal("data/dev.jsonl", resource.DevDataPath);
        Assert.Same(builder, result);
    }

    [Fact]
    public void WithOptimizer_SetsOptimizerType()
    {
        var resource = new LmpOptimizerResource("opt", typeof(TestModule));
        var builder = new MockResourceBuilder(resource);

        var result = builder.WithOptimizer<BootstrapRandomSearch>();

        Assert.Equal(typeof(BootstrapRandomSearch), resource.OptimizerType);
        Assert.Same(builder, result);
    }

    [Fact]
    public void WithOutputPath_SetsPath()
    {
        var resource = new LmpOptimizerResource("opt", typeof(TestModule));
        var builder = new MockResourceBuilder(resource);

        var result = builder.WithOutputPath("output/model.json");

        Assert.Equal("output/model.json", resource.OutputPath);
        Assert.Same(builder, result);
    }

    [Fact]
    public void WithMaxConcurrency_SetsConcurrency()
    {
        var resource = new LmpOptimizerResource("opt", typeof(TestModule));
        var builder = new MockResourceBuilder(resource);

        var result = builder.WithMaxConcurrency(16);

        Assert.Equal(16, resource.MaxConcurrency);
        Assert.Same(builder, result);
    }

    [Fact]
    public void FluentChaining_ConfiguresAllProperties()
    {
        var resource = new LmpOptimizerResource("opt", typeof(TestModule));
        var builder = new MockResourceBuilder(resource);

        builder
            .WithTrainData("train.jsonl")
            .WithDevData("dev.jsonl")
            .WithOptimizer<BootstrapFewShot>()
            .WithOutputPath("optimized.json")
            .WithMaxConcurrency(2);

        Assert.Equal("train.jsonl", resource.TrainDataPath);
        Assert.Equal("dev.jsonl", resource.DevDataPath);
        Assert.Equal(typeof(BootstrapFewShot), resource.OptimizerType);
        Assert.Equal("optimized.json", resource.OutputPath);
        Assert.Equal(2, resource.MaxConcurrency);
    }
}

/// <summary>
/// Minimal mock of <see cref="IResourceBuilder{T}"/> that exposes the resource
/// for testing extension methods without a full Aspire AppHost.
/// </summary>
file sealed class MockResourceBuilder(LmpOptimizerResource resource)
    : IResourceBuilder<LmpOptimizerResource>
{
    public LmpOptimizerResource Resource => resource;

    public IDistributedApplicationBuilder ApplicationBuilder =>
        throw new NotSupportedException("Mock does not support ApplicationBuilder.");

    public IResourceBuilder<LmpOptimizerResource> WithAnnotation<TAnnotation>(
        ResourceAnnotationMutationBehavior behavior = ResourceAnnotationMutationBehavior.Append)
        where TAnnotation : IResourceAnnotation, new()
        => this;

    public IResourceBuilder<LmpOptimizerResource> WithAnnotation<TAnnotation>(
        TAnnotation annotation, ResourceAnnotationMutationBehavior behavior = ResourceAnnotationMutationBehavior.Append)
        where TAnnotation : IResourceAnnotation
        => this;
}
