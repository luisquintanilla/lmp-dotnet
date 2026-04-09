using System.Text.Json;
using LMP;
using LMP.Samples.Agent;
using Microsoft.Extensions.AI;

// ──────────────────────────────────────────────────────────────
// LMP ReAct Agent — Sample
//
// Demonstrates the ReActAgent module with tool-augmented reasoning:
//   1. Define AIFunction tools (search_web, get_article, get_facts)
//   2. Create a ReActAgent with those tools
//   3. Run a research query through the Think → Act → Observe loop
//
// Uses a mock chat client so the sample runs without an API key.
// Replace MockAgentChatClient with a real IChatClient for production.
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — ReAct Agent Demo                    ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine();

// ── Define tools ────────────────────────────────────────────

var searchWeb = AIFunctionFactory.Create(
    (string query) =>
    {
        Console.WriteLine($"  🔍 [search_web] query=\"{query}\"");
        return new[] { "https://example.com/quantum-computing-overview", "https://example.com/qubits-explained" };
    },
    "search_web",
    "Search the web for articles on a topic. Returns a list of URLs.");

var getArticle = AIFunctionFactory.Create(
    (string url) =>
    {
        Console.WriteLine($"  📄 [get_article] url=\"{url}\"");
        return url.Contains("overview")
            ? "Quantum computing uses qubits that can exist in superposition, enabling parallel computation. " +
              "Major companies like IBM and Google are investing heavily in this technology."
            : "Qubits are the fundamental unit of quantum information. Unlike classical bits, they can represent " +
              "0, 1, or both simultaneously through superposition.";
    },
    "get_article",
    "Fetch and summarize an article at the given URL.");

var getFacts = AIFunctionFactory.Create(
    (string text) =>
    {
        Console.WriteLine($"  📋 [get_facts] extracting facts from {text.Length} chars");
        return new[]
        {
            "Quantum computers use qubits instead of classical bits",
            "Qubits leverage superposition to represent multiple states simultaneously",
            "IBM and Google are leading investors in quantum computing"
        };
    },
    "get_facts",
    "Extract key facts from a block of text.");

// ── Create the ReAct agent ─────────────────────────────────

IChatClient client = new MockAgentChatClient();
var agent = new ReActAgent<ResearchInput, ResearchReport>(
    client,
    tools: [searchWeb, getArticle, getFacts],
    maxSteps: 5);

agent.Instructions = "You are a research assistant. Use the available tools to search for information, " +
                     "read articles, and extract facts before producing your final report.";

// ── Run the agent ──────────────────────────────────────────

Console.WriteLine("Running research agent on topic: \"Quantum Computing\"");
Console.WriteLine("─────────────────────────────────────────────────────");
Console.WriteLine();

var input = new ResearchInput("Quantum Computing");
var report = await agent.PredictAsync(input);

Console.WriteLine();
Console.WriteLine("── Research Report ────────────────────────────────");
Console.WriteLine($"  Summary:      {report.Summary}");
Console.WriteLine($"  Source Count:  {report.SourceCount}");
Console.WriteLine($"  Key Facts:");
foreach (var fact in report.KeyFacts)
    Console.WriteLine($"    • {fact}");

Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   Demo Complete!                             ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");

// ── Mock Chat Client ────────────────────────────────────────

/// <summary>
/// A deterministic mock that simulates the ReAct Think → Act → Observe loop:
///   Call 1: tool call to search_web
///   Call 2: tool call to get_article (first URL)
///   Call 3: tool call to get_facts
///   Call 4: final structured JSON answer
///
/// Replace with a real <see cref="IChatClient"/> for production use.
/// </summary>
file sealed class MockAgentChatClient : IChatClient
{
    private int _callCount;

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _callCount++;

        ChatResponse response = _callCount switch
        {
            // Step 1: Agent decides to search the web
            1 => MakeToolCallResponse("search_web", new { query = "Quantum Computing" }),

            // Step 2: Agent reads the first article
            2 => MakeToolCallResponse("get_article", new { url = "https://example.com/quantum-computing-overview" }),

            // Step 3: Agent extracts facts from the article text
            3 => MakeToolCallResponse("get_facts", new { text = "Quantum computing uses qubits that can exist in superposition." }),

            // Step 4: Agent produces the final structured answer
            _ => MakeFinalResponse()
        };

        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming not supported in mock.");

    private static ChatResponse MakeToolCallResponse(string functionName, object arguments)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(arguments));

        var callContent = new FunctionCallContent(
            callId: Guid.NewGuid().ToString("N"),
            name: functionName,
            arguments: args);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [callContent]));
    }

    private static ChatResponse MakeFinalResponse()
    {
        var json = JsonSerializer.Serialize(new
        {
            summary = "Quantum computing is a rapidly advancing field that uses qubits to perform " +
                      "computations beyond the reach of classical computers. Major technology companies " +
                      "are investing heavily in its development.",
            keyFacts = new[]
            {
                "Quantum computers use qubits instead of classical bits",
                "Qubits leverage superposition to represent multiple states simultaneously",
                "IBM and Google are leading investors in quantum computing"
            },
            sourceCount = 2
        });

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, json));
    }
}
