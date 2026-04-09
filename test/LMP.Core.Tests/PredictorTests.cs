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

    #region Clone tests

    [Fact]
    public void Clone_ReturnsIPredictor()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());

        var clone = predictor.Clone();

        Assert.IsAssignableFrom<IPredictor>(clone);
    }

    [Fact]
    public void Clone_ReturnsSameConcreteType()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());

        var clone = predictor.Clone();

        Assert.IsType<Predictor<string, TestOutput>>(clone);
    }

    [Fact]
    public void Clone_CopiesName()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());
        predictor.Name = "classify";

        var clone = (Predictor<string, TestOutput>)predictor.Clone();

        Assert.Equal("classify", clone.Name);
    }

    [Fact]
    public void Clone_CopiesInstructions()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());
        predictor.Instructions = "Classify tickets";

        var clone = (Predictor<string, TestOutput>)predictor.Clone();

        Assert.Equal("Classify tickets", clone.Instructions);
    }

    [Fact]
    public void Clone_CopiesDemos()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());
        predictor.Demos.Add(("input1", new TestOutput { Value = "output1" }));

        var clone = (Predictor<string, TestOutput>)predictor.Clone();

        Assert.Single(clone.Demos);
        Assert.Equal("input1", clone.Demos[0].Input);
        Assert.Equal("output1", clone.Demos[0].Output.Value);
    }

    [Fact]
    public void Clone_DemosAreIndependent()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());
        predictor.Demos.Add(("shared", new TestOutput { Value = "shared" }));

        var clone = (Predictor<string, TestOutput>)predictor.Clone();

        // Modify clone's demos
        clone.Demos.Add(("new", new TestOutput { Value = "new" }));

        // Original should not be affected
        Assert.Single(predictor.Demos);
        Assert.Equal(2, clone.Demos.Count);
    }

    [Fact]
    public void Clone_ModifyingOriginalDemos_DoesNotAffectClone()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());
        predictor.Demos.Add(("original", new TestOutput { Value = "original" }));

        var clone = (Predictor<string, TestOutput>)predictor.Clone();

        // Modify original's demos
        predictor.Demos.Add(("added", new TestOutput { Value = "added" }));

        // Clone should not be affected
        Assert.Single(clone.Demos);
    }

    [Fact]
    public void Clone_ConfigIsIndependent()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());
        predictor.Config = new ChatOptions { Temperature = 0.5f };

        var clone = (Predictor<string, TestOutput>)predictor.Clone();

        // Config should be a separate instance with same values
        Assert.NotSame(predictor.Config, clone.Config);
        Assert.Equal(0.5f, clone.Config.Temperature);

        // Modifying clone's config should not affect original
        clone.Config.Temperature = 0.9f;
        Assert.Equal(0.5f, predictor.Config.Temperature);
    }

    [Fact]
    public void Clone_InstructionsAreIndependent()
    {
        var predictor = new Predictor<string, TestOutput>(CreateMockClient());
        predictor.Instructions = "Original";

        var clone = (Predictor<string, TestOutput>)predictor.Clone();

        // Modify clone's instructions
        clone.Instructions = "Modified";

        Assert.Equal("Original", predictor.Instructions);
        Assert.Equal("Modified", clone.Instructions);
    }

    [Fact]
    public void Clone_ViaIPredictor_ReturnsClone()
    {
        IPredictor predictor = new Predictor<string, TestOutput>(CreateMockClient());
        predictor.Instructions = "Test";

        var clone = predictor.Clone();

        Assert.NotSame(predictor, clone);
        Assert.Equal("Test", clone.Instructions);
    }

    #endregion

    public class TestOutput
    {
        public string Value { get; init; } = string.Empty;
    }
}
