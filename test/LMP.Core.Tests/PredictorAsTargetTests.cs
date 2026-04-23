using Microsoft.Extensions.AI;
using Moq;

namespace LMP.Tests;

/// <summary>
/// T2a tests: verifies <see cref="Predictor{TInput, TOutput}"/> directly implements
/// <see cref="IOptimizationTarget"/> and emits the leaf fractal parameter space
/// (<c>instructions</c> + <c>demos</c>).
/// </summary>
public sealed class PredictorAsTargetTests
{
    private sealed record QAIn(string Question);
    private sealed record QAOut(string Answer);

    private static IChatClient MockClient() => new Mock<IChatClient>().Object;

    private static Predictor<QAIn, QAOut> NewPredictor(string instructions = "")
    {
        var p = new Predictor<QAIn, QAOut>(MockClient());
        p.Instructions = instructions;
        return p;
    }

    [Fact]
    public void Predictor_IsAn_IOptimizationTarget()
    {
        var p = NewPredictor();
        Assert.IsAssignableFrom<IOptimizationTarget>(p);
    }

    [Fact]
    public void Shape_IsSingleTurn()
    {
        IOptimizationTarget target = NewPredictor();
        Assert.Equal(TargetShape.SingleTurn, target.Shape);
    }

    [Fact]
    public void GetParameterSpace_EmitsInstructionsAndDemos()
    {
        var p = NewPredictor("You are helpful.");
        p.Demos.Add((new QAIn("Q1"), new QAOut("A1")));
        p.Demos.Add((new QAIn("Q2"), new QAOut("A2")));

        var space = ((IOptimizationTarget)p).GetParameterSpace();

        Assert.Equal(2, space.Parameters.Count);

        var instrKind = Assert.IsType<StringValued>(space.Parameters["instructions"]);
        Assert.Equal("You are helpful.", instrKind.InitialValue);

        var demosKind = Assert.IsType<Subset>(space.Parameters["demos"]);
        Assert.Equal(0, demosKind.MinSize);
        Assert.Equal(2, demosKind.MaxSize);
        Assert.Equal(2, demosKind.Pool.Count);
        // Pool items are the boxed tuples from p.Demos.
        Assert.Contains(demosKind.Pool,
            o => o is ValueTuple<QAIn, QAOut> t && t.Item1.Question == "Q1");
    }

    [Fact]
    public void GetParameterSpace_EmptyDemos_EmitsEmptyPoolSubset()
    {
        var p = NewPredictor();
        var space = ((IOptimizationTarget)p).GetParameterSpace();

        var demosKind = Assert.IsType<Subset>(space.Parameters["demos"]);
        Assert.Empty(demosKind.Pool);
        Assert.Equal(0, demosKind.MinSize);
        Assert.Equal(0, demosKind.MaxSize);
    }

    [Fact]
    public void WithParameters_Empty_ClonesAndReturnsEquivalent()
    {
        var p = NewPredictor("Seed");
        p.Demos.Add((new QAIn("Q"), new QAOut("A")));

        var result = ((IOptimizationTarget)p).WithParameters(ParameterAssignment.Empty);

        var clone = Assert.IsType<Predictor<QAIn, QAOut>>(result);
        Assert.NotSame(p, clone);
        Assert.Equal(p.Instructions, clone.Instructions);
        Assert.Equal(p.Demos.Count, clone.Demos.Count);
    }

    [Fact]
    public void WithParameters_InstructionsKey_UpdatesClonedInstructions()
    {
        var p = NewPredictor("Original");
        var assignment = ParameterAssignment.Empty.With("instructions", "Updated");

        var result = ((IOptimizationTarget)p).WithParameters(assignment);

        var clone = Assert.IsType<Predictor<QAIn, QAOut>>(result);
        Assert.Equal("Updated", clone.Instructions);
        Assert.Equal("Original", p.Instructions); // original untouched
    }

    [Fact]
    public void WithParameters_DemosKey_UpdatesClonedDemos()
    {
        var p = NewPredictor();
        p.Demos.Add((new QAIn("OldQ"), new QAOut("OldA")));

        IReadOnlyList<object> newDemos = new object[]
        {
            (new QAIn("NewQ1"), new QAOut("NewA1")),
            (new QAIn("NewQ2"), new QAOut("NewA2")),
        };
        var assignment = ParameterAssignment.Empty.With("demos", newDemos);

        var result = ((IOptimizationTarget)p).WithParameters(assignment);

        var clone = Assert.IsType<Predictor<QAIn, QAOut>>(result);
        Assert.Equal(2, clone.Demos.Count);
        Assert.Equal("NewQ1", clone.Demos[0].Input.Question);
        Assert.Equal("NewA2", clone.Demos[1].Output.Answer);
        // Original untouched.
        Assert.Single(p.Demos);
        Assert.Equal("OldQ", p.Demos[0].Input.Question);
    }

    [Fact]
    public void WithParameters_UnknownKey_ThrowsArgumentException()
    {
        var p = NewPredictor();
        var assignment = ParameterAssignment.Empty.With("temperature", 0.5);

        var ex = Assert.Throws<ArgumentException>(
            () => ((IOptimizationTarget)p).WithParameters(assignment));

        Assert.Contains("temperature", ex.Message);
        Assert.Contains("instructions", ex.Message);
        Assert.Contains("demos", ex.Message);
    }

    [Fact]
    public void GetService_ReturnsSelfForPredictorAndIPredictor()
    {
        var p = NewPredictor();
        IOptimizationTarget target = p;

        Assert.Same(p, target.GetService<Predictor<QAIn, QAOut>>());
        Assert.Same(p, target.GetService<IPredictor>());
    }

    [Fact]
    public async Task ExecuteAsync_CastsInputAndReturnsTypedOutputBoxed()
    {
        var fake = new FakeChatClient();
        fake.EnqueueResponse(new QAOut("42"));
        var p = new Predictor<QAIn, QAOut>(fake);

        var (output, trace) = await ((IOptimizationTarget)p).ExecuteAsync(new QAIn("What?"));

        var qa = Assert.IsType<QAOut>(output);
        Assert.Equal("42", qa.Answer);
        Assert.Single(trace.Entries);
    }

    [Fact]
    public async Task ExecuteAsync_WrongInputType_ThrowsArgumentException()
    {
        var p = NewPredictor();
        await Assert.ThrowsAsync<ArgumentException>(
            () => ((IOptimizationTarget)p).ExecuteAsync("not a QAIn"));
    }
}
