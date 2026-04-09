using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace LMP.Tests;

/// <summary>
/// Tests for <see cref="Predictor{TInput, TOutput}.Clone"/> and
/// <see cref="LmpModule.Clone{TModule}"/>.
/// </summary>
public class CloneTests
{
    #region Predictor.Clone

    [Fact]
    public void PredictorClone_ReturnsNewInstance()
    {
        var client = new Mock<IChatClient>().Object;
        var original = new Predictor<string, TestOutput>(client);

        var clone = (Predictor<string, TestOutput>)original.Clone();

        Assert.NotSame(original, clone);
    }

    [Fact]
    public void PredictorClone_PreservesName()
    {
        var client = new Mock<IChatClient>().Object;
        var original = new Predictor<string, TestOutput>(client) { Name = "classify" };

        var clone = (Predictor<string, TestOutput>)original.Clone();

        Assert.Equal("classify", clone.Name);
    }

    [Fact]
    public void PredictorClone_PreservesInstructions()
    {
        var client = new Mock<IChatClient>().Object;
        var original = new Predictor<string, TestOutput>(client)
        {
            Instructions = "Classify the ticket."
        };

        var clone = (Predictor<string, TestOutput>)original.Clone();

        Assert.Equal("Classify the ticket.", clone.Instructions);
    }

    [Fact]
    public void PredictorClone_HasIndependentDemosList()
    {
        var client = new Mock<IChatClient>().Object;
        var original = new Predictor<string, TestOutput>(client);
        original.Demos.Add(("input1", new TestOutput { Value = "output1" }));

        var clone = (Predictor<string, TestOutput>)original.Clone();

        Assert.NotSame(original.Demos, clone.Demos);
        Assert.Single(clone.Demos);
        Assert.Equal("input1", clone.Demos[0].Input);
    }

    [Fact]
    public void PredictorClone_ModifyingCloneDemos_DoesNotAffectOriginal()
    {
        var client = new Mock<IChatClient>().Object;
        var original = new Predictor<string, TestOutput>(client);
        original.Demos.Add(("input1", new TestOutput { Value = "output1" }));

        var clone = (Predictor<string, TestOutput>)original.Clone();
        clone.Demos.Add(("input2", new TestOutput { Value = "output2" }));

        Assert.Single(original.Demos);
        Assert.Equal(2, clone.Demos.Count);
    }

    [Fact]
    public void PredictorClone_ModifyingOriginalDemos_DoesNotAffectClone()
    {
        var client = new Mock<IChatClient>().Object;
        var original = new Predictor<string, TestOutput>(client);
        original.Demos.Add(("input1", new TestOutput { Value = "output1" }));

        var clone = (Predictor<string, TestOutput>)original.Clone();
        original.Demos.Clear();

        Assert.Empty(original.Demos);
        Assert.Single(clone.Demos);
    }

    [Fact]
    public void PredictorClone_HasIndependentConfig()
    {
        var client = new Mock<IChatClient>().Object;
        var original = new Predictor<string, TestOutput>(client)
        {
            Config = new ChatOptions { Temperature = 0.7f, MaxOutputTokens = 100 }
        };

        var clone = (Predictor<string, TestOutput>)original.Clone();

        Assert.NotSame(original.Config, clone.Config);
        Assert.Equal(0.7f, clone.Config.Temperature);
        Assert.Equal(100, clone.Config.MaxOutputTokens);
    }

    [Fact]
    public void PredictorClone_ModifyingCloneConfig_DoesNotAffectOriginal()
    {
        var client = new Mock<IChatClient>().Object;
        var original = new Predictor<string, TestOutput>(client)
        {
            Config = new ChatOptions { Temperature = 0.7f }
        };

        var clone = (Predictor<string, TestOutput>)original.Clone();
        clone.Config.Temperature = 0.9f;

        Assert.Equal(0.7f, original.Config.Temperature);
        Assert.Equal(0.9f, clone.Config.Temperature);
    }

    [Fact]
    public void PredictorClone_CopiesStopSequences()
    {
        var client = new Mock<IChatClient>().Object;
        var original = new Predictor<string, TestOutput>(client)
        {
            Config = new ChatOptions { StopSequences = ["END", "STOP"] }
        };

        var clone = (Predictor<string, TestOutput>)original.Clone();

        Assert.NotSame(original.Config.StopSequences, clone.Config.StopSequences);
        Assert.Equal(["END", "STOP"], clone.Config.StopSequences);
    }

    [Fact]
    public void PredictorClone_ReturnsIPredictor()
    {
        var client = new Mock<IChatClient>().Object;
        var original = new Predictor<string, TestOutput>(client);

        IPredictor clone = original.Clone();

        Assert.IsType<Predictor<string, TestOutput>>(clone);
    }

    [Fact]
    public void PredictorClone_ModifyingCloneInstructions_DoesNotAffectOriginal()
    {
        var client = new Mock<IChatClient>().Object;
        var original = new Predictor<string, TestOutput>(client)
        {
            Instructions = "original"
        };

        var clone = (Predictor<string, TestOutput>)original.Clone();
        clone.Instructions = "modified";

        Assert.Equal("original", original.Instructions);
        Assert.Equal("modified", clone.Instructions);
    }

    [Fact]
    public void PredictorClone_WithEmptyDemos_HasEmptyCloneDemos()
    {
        var client = new Mock<IChatClient>().Object;
        var original = new Predictor<string, TestOutput>(client);

        var clone = (Predictor<string, TestOutput>)original.Clone();

        Assert.Empty(clone.Demos);
        Assert.NotSame(original.Demos, clone.Demos);
    }

    #endregion

    #region LmpModule.Clone<TModule> base

    [Fact]
    public void ModuleClone_ThrowsNotSupported_WhenCloneCoreNotOverridden()
    {
        var module = new NonPartialModule();

        Assert.Throws<NotSupportedException>(() => module.Clone<NonPartialModule>());
    }

    [Fact]
    public void ModuleClone_CallsCloneCore_AndClearsTrace()
    {
        var module = new ManualCloneModule();
        module.Trace = new Trace();
        module.Trace.Record("test", "in", "out");

        var clone = module.Clone<ManualCloneModule>();

        Assert.NotSame(module, clone);
        Assert.Null(clone.Trace);
        Assert.NotNull(module.Trace);
    }

    #endregion

    #region Test types

    public sealed class TestOutput
    {
        public required string Value { get; init; }
    }

    private sealed class NonPartialModule : LmpModule
    {
        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);
    }

    private sealed class ManualCloneModule : LmpModule
    {
        public override Task<object> ForwardAsync(
            object input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);

        protected override LmpModule CloneCore()
            => (ManualCloneModule)MemberwiseClone();
    }

    #endregion
}
