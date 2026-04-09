using System.Collections;
using Microsoft.Extensions.AI;
using Moq;

namespace LMP.Tests;

public class PredictorTests
{
    private static IChatClient CreateMockClient()
    {
        return new Mock<IChatClient>().Object;
    }

    [Fact]
    public void Constructor_SetsClient()
    {
        var client = CreateMockClient();

        var predictor = new Predictor<string, TestOutput>(client);

        Assert.NotNull(predictor);
    }

    [Fact]
    public void Constructor_ThrowsOnNullClient()
    {
        Assert.Throws<ArgumentNullException>(() => new Predictor<string, TestOutput>(null!));
    }

    [Fact]
    public void Name_DefaultsToTypeArrow()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());

        Assert.Equal("String\u2192TestOutput", predictor.Name);
    }

    [Fact]
    public void Name_CanBeSet()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());

        predictor.Name = "classify";

        Assert.Equal("classify", predictor.Name);
    }

    [Fact]
    public void Instructions_DefaultsToEmpty()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());

        Assert.Equal(string.Empty, predictor.Instructions);
    }

    [Fact]
    public void Instructions_CanBeSet()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());

        predictor.Instructions = "Classify tickets";

        Assert.Equal("Classify tickets", predictor.Instructions);
    }

    [Fact]
    public void Demos_DefaultsToEmpty()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());

        Assert.Empty(predictor.Demos);
    }

    [Fact]
    public void Demos_CanBeSet()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());
        var demo = ("input", new TestOutput { Value = "output" });

        predictor.Demos = [demo];

        Assert.Single(predictor.Demos);
        Assert.Equal("input", predictor.Demos[0].Input);
    }

    [Fact]
    public void Config_DefaultsToNewChatOptions()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());

        Assert.NotNull(predictor.Config);
    }

    [Fact]
    public void Config_CanBeSet()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());
        var config = new ChatOptions { Temperature = 0.7f };

        predictor.Config = config;

        Assert.Equal(0.7f, predictor.Config.Temperature);
    }

    [Fact]
    public void ImplementsIPredictor()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());

        Assert.IsAssignableFrom<IPredictor>(predictor);
    }

    [Fact]
    public void IPredictor_Demos_ReturnsNonGenericList()
    {
        IPredictor predictor = new Predictor<string, TestOutput>(CreateMockClient());

        IList demos = predictor.Demos;

        Assert.NotNull(demos);
    }

    [Fact]
    public async Task PredictAsync_CallsClient()
    {
        var mockClient = new Mock<IChatClient>();
        mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    System.Text.Json.JsonSerializer.Serialize(new TestOutput { Value = "result" }))));

        var predictor = new Predictor<string, TestOutput>(mockClient.Object);

        var result = await predictor.PredictAsync("test");

        Assert.Equal("result", result.Value);
        mockClient.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetState_ReturnsCurrentState()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());
        predictor.Instructions = "Test instructions";

        var state = predictor.GetState();

        Assert.Equal("Test instructions", state.Instructions);
        Assert.Empty(state.Demos);
    }

    [Fact]
    public void LoadState_RestoresInstructions()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());
        var state = new PredictorState
        {
            Instructions = "Loaded instructions",
            Demos = []
        };

        predictor.LoadState(state);

        Assert.Equal("Loaded instructions", predictor.Instructions);
    }

    [Fact]
    public void LoadState_ThrowsOnNull()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());

        Assert.Throws<ArgumentNullException>(() => predictor.LoadState(null!));
    }

    public class TestOutput
    {
        public string Value { get; init; } = string.Empty;
    }
}
