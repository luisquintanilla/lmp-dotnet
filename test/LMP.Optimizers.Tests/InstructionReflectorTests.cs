using LMP.Optimizers;
using Microsoft.Extensions.AI;
namespace LMP.Tests;

public sealed class InstructionReflectorTests
{
    [Fact]
    public async Task ReflectAsync_OnCompositeTraces_MatchesByFullPath()
    {
        // Build a failure trace whose PredictorName carries the composite prefix
        // ("child_0.foo"), then invoke ReflectAsync with the matching predictorPath.
        // Verifies the filter in InstructionReflector selects trace entries by fully
        // qualified path — the §22 promise at the reflection layer.
        var trace = new Trace();
        trace.Record("child_0.foo", input: "in-text", output: "wrong-output");

        var example = new Example<string, string>("in-text", "expected");
        var failures = new List<(Example Example, object Output, float Score, Trace Trace)>
        {
            (example, "wrong-output", 0.0f, trace),
        };

        var client = new CapturingChatClient();

        var result = await InstructionReflector.ReflectAsync(
            reflectionClient: client,
            predictorPath: "child_0.foo",
            currentInstruction: "Initial instruction",
            failedTraces: failures,
            cancellationToken: CancellationToken.None);

        // Reflector saw the matching trace → produced (non-empty) instruction and
        // included the fully-qualified path in its prompt.
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.NotNull(client.LastUserPrompt);
        Assert.Contains("child_0.foo", client.LastUserPrompt);
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public string? LastUserPrompt { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastUserPrompt = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "Improved instruction.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
