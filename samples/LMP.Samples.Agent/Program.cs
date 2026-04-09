using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using LMP;
using LMP.Samples.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

// ──────────────────────────────────────────────────────────────
// LMP ReAct Agent — Sample (Azure OpenAI)
//
// Demonstrates the ReActAgent module with tool-augmented reasoning:
//   1. Define AIFunction tools (search_web, get_article, get_facts)
//   2. Create a ReActAgent with those tools
//   3. Run a research query through the Think → Act → Observe loop
//
// Uses Azure OpenAI with DefaultAzureCredential (managed identity).
// Configure endpoint and deployment via user secrets:
//   dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR_RESOURCE.openai.azure.com/"
//   dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4.1-nano"
// ──────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║   LMP — ReAct Agent Demo (Azure OpenAI)     ║");
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

// ── Configure Azure OpenAI via user secrets + managed identity ──

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

string endpoint = config["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException("Set AzureOpenAI:Endpoint in user secrets.");
string deployment = config["AzureOpenAI:Deployment"]
    ?? throw new InvalidOperationException("Set AzureOpenAI:Deployment in user secrets.");

IChatClient client = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

Console.WriteLine($"  Using: {deployment} @ {endpoint}");
Console.WriteLine();

// ── Create the ReAct agent ─────────────────────────────────
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



