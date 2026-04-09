using System.Collections;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

public class IPredictorTests
{
    private sealed class StubPredictor : IPredictor
    {
        public string Name { get; set; } = "test";
        public string Instructions { get; set; } = "Test instructions";
        public IList Demos => new List<string>();
        public ChatOptions Config { get; set; } = new();
        public PredictorState GetState() => new()
        {
            Instructions = Instructions,
            Demos = [],
            Config = null
        };
        public void LoadState(PredictorState state)
        {
            Instructions = state.Instructions;
        }
    }

    [Fact]
    public void StubImplementsInterface_WithoutError()
    {
        IPredictor predictor = new StubPredictor();

        Assert.Equal("test", predictor.Name);
        Assert.Equal("Test instructions", predictor.Instructions);
        Assert.NotNull(predictor.Demos);
        Assert.NotNull(predictor.Config);
    }

    [Fact]
    public void GetState_ReturnsPredictorState()
    {
        IPredictor predictor = new StubPredictor { Instructions = "Custom" };

        var state = predictor.GetState();

        Assert.Equal("Custom", state.Instructions);
        Assert.Empty(state.Demos);
    }

    [Fact]
    public void LoadState_RestoresInstructions()
    {
        var predictor = new StubPredictor();
        var state = new PredictorState
        {
            Instructions = "Loaded instructions",
            Demos = []
        };

        predictor.LoadState(state);

        Assert.Equal("Loaded instructions", predictor.Instructions);
    }
}
