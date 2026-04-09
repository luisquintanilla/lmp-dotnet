using System.Text.Json;

namespace LMP.Tests;

public class ModuleStateSerializerContextTests
{
    [Fact]
    public void SerializeDeserialize_RoundTrips()
    {
        var original = new ModuleState
        {
            Version = "1.0",
            Module = "TestModule",
            Predictors = new Dictionary<string, PredictorState>
            {
                ["classify"] = new PredictorState
                {
                    Instructions = "Classify a ticket",
                    Demos =
                    [
                        new DemoEntry
                        {
                            Input = new Dictionary<string, JsonElement>
                            {
                                ["ticketText"] = JsonSerializer.SerializeToElement("I was charged twice")
                            },
                            Output = new Dictionary<string, JsonElement>
                            {
                                ["category"] = JsonSerializer.SerializeToElement("billing"),
                                ["urgency"] = JsonSerializer.SerializeToElement(3)
                            }
                        }
                    ],
                    Config = new Dictionary<string, JsonElement>
                    {
                        ["temperature"] = JsonSerializer.SerializeToElement(0.7)
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(original, ModuleStateSerializerContext.Default.ModuleState);
        var deserialized = JsonSerializer.Deserialize(json, ModuleStateSerializerContext.Default.ModuleState);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Version, deserialized.Version);
        Assert.Equal(original.Module, deserialized.Module);
        Assert.Single(deserialized.Predictors);

        var predictor = deserialized.Predictors["classify"];
        Assert.Equal("Classify a ticket", predictor.Instructions);
        Assert.Single(predictor.Demos);
        Assert.NotNull(predictor.Config);
        Assert.Equal(0.7, predictor.Config["temperature"].GetDouble(), 0.001);
    }

    [Fact]
    public void Serialize_UsesCamelCase()
    {
        var state = new ModuleState
        {
            Version = "1.0",
            Module = "Test",
            Predictors = new Dictionary<string, PredictorState>()
        };

        var json = JsonSerializer.Serialize(state, ModuleStateSerializerContext.Default.ModuleState);

        Assert.Contains("\"version\"", json);
        Assert.Contains("\"module\"", json);
        Assert.Contains("\"predictors\"", json);
    }

    [Fact]
    public void Serialize_IsIndented()
    {
        var state = new ModuleState
        {
            Version = "1.0",
            Module = "Test",
            Predictors = new Dictionary<string, PredictorState>()
        };

        var json = JsonSerializer.Serialize(state, ModuleStateSerializerContext.Default.ModuleState);

        Assert.Contains("\n", json);
    }

    [Fact]
    public void Serialize_OmitsNullConfig()
    {
        var state = new ModuleState
        {
            Version = "1.0",
            Module = "Test",
            Predictors = new Dictionary<string, PredictorState>
            {
                ["p"] = new PredictorState
                {
                    Instructions = "test",
                    Demos = [],
                    Config = null
                }
            }
        };

        var json = JsonSerializer.Serialize(state, ModuleStateSerializerContext.Default.ModuleState);

        Assert.DoesNotContain("\"config\"", json);
    }
}
