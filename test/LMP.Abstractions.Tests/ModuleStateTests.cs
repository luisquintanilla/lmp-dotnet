using System.Text.Json;

namespace LMP.Tests;

public class ModuleStateTests
{
    [Fact]
    public void ModuleState_ConstructsWithRequiredProperties()
    {
        var state = new ModuleState
        {
            Version = "1.0",
            Module = "TestModule",
            Predictors = new Dictionary<string, PredictorState>
            {
                ["classify"] = new PredictorState
                {
                    Instructions = "Classify tickets",
                    Demos = []
                }
            }
        };

        Assert.Equal("1.0", state.Version);
        Assert.Equal("TestModule", state.Module);
        Assert.Single(state.Predictors);
    }

    [Fact]
    public void PredictorState_ConstructsWithDemos()
    {
        var state = new PredictorState
        {
            Instructions = "Test",
            Demos =
            [
                new DemoEntry
                {
                    Input = new Dictionary<string, JsonElement>
                    {
                        ["text"] = JsonSerializer.SerializeToElement("hello")
                    },
                    Output = new Dictionary<string, JsonElement>
                    {
                        ["category"] = JsonSerializer.SerializeToElement("greeting")
                    }
                }
            ]
        };

        Assert.Single(state.Demos);
        Assert.Null(state.Config);
    }

    [Fact]
    public void PredictorState_WithConfig()
    {
        var state = new PredictorState
        {
            Instructions = "Test",
            Demos = [],
            Config = new Dictionary<string, JsonElement>
            {
                ["temperature"] = JsonSerializer.SerializeToElement(0.7)
            }
        };

        Assert.NotNull(state.Config);
        Assert.Single(state.Config);
    }

    [Fact]
    public void DemoEntry_EqualsWhenSameContent()
    {
        var input = new Dictionary<string, JsonElement>
        {
            ["text"] = JsonSerializer.SerializeToElement("hello")
        };
        var output = new Dictionary<string, JsonElement>
        {
            ["cat"] = JsonSerializer.SerializeToElement("greeting")
        };

        var demo1 = new DemoEntry { Input = input, Output = output };
        var demo2 = new DemoEntry { Input = input, Output = output };

        Assert.Equal(demo1, demo2);
    }

    [Fact]
    public void ModuleState_WithExpression_CreatesNewInstance()
    {
        var original = new ModuleState
        {
            Version = "1.0",
            Module = "TestModule",
            Predictors = new Dictionary<string, PredictorState>()
        };

        var modified = original with { Module = "ModifiedModule" };

        Assert.Equal("TestModule", original.Module);
        Assert.Equal("ModifiedModule", modified.Module);
        Assert.Equal(original.Version, modified.Version);
    }

    [Fact]
    public void PredictorState_WithExpression_CreatesNewInstance()
    {
        var original = new PredictorState
        {
            Instructions = "Original",
            Demos = []
        };

        var modified = original with { Instructions = "Modified" };

        Assert.Equal("Original", original.Instructions);
        Assert.Equal("Modified", modified.Instructions);
    }
}
