# Testing Strategy

> **Derived from:** Spec Sections 16 (Phased Plan), 17 (Acceptance Criteria), 18 (Testing Strategy Summary).
>
> **Audience:** Developers writing and maintaining tests for the LMP framework.

---

## 1. Test Categories Overview

| Category | What It Tests | Project | Runner |
|----------|--------------|---------|--------|
| **Unit** | IR types, descriptors, graph construction, constraint evaluation | `LMP.Abstractions.Tests`, `LMP.Runtime.Tests`, `LMP.Compiler.Tests` | xUnit |
| **Integration** | Step execution with mock `IChatClient`, multi-step graph runs | `LMP.Runtime.Tests` | xUnit |
| **Golden/Snapshot** | Source generator output determinism | `LMP.Roslyn.Tests` | xUnit + Verify |
| **Analyzer** | Diagnostic detection and code fixes | `LMP.Roslyn.Tests` | xUnit + `Microsoft.CodeAnalysis.Testing` |
| **Compiler** | Optimization loop, constraint enforcement, selection | `LMP.Compiler.Tests` | xUnit |
| **Pipeline** | TPL Dataflow pipeline construction, M.E.AI middleware composition | `LMP.Runtime.Tests` | xUnit |
| **Artifact** | Serialization/deserialization, schema stability | `LMP.Compiler.Tests` | xUnit + Verify |
| **E2E** | Full pipeline: build → compile → save → load → run | `LMP.E2E.Tests` | xUnit |

---

## 2. Unit Tests

### What They Test

Core IR types (`SignatureDescriptor`, `ProgramDescriptor`, `StepDescriptor`, etc.), graph construction (`ProgramGraph`), constraint model (`ConstraintDescriptor`, constraint checking), weighted objective scoring, and record equality/immutability.

### How to Write One

1. Instantiate the IR type with known values.
2. Assert structural properties (fields, equality, `with` expressions).
3. No mocks needed — these are pure data types.

### Example

```csharp
using LMP;
using Xunit;

public class SignatureDescriptorTests
{
    [Fact]
    public void Descriptor_Has_Correct_Field_Count()
    {
        var descriptor = new SignatureDescriptor(
            Id: "triageticket",
            Name: "TriageTicket",
            Instructions: "You are a triage assistant.",
            Inputs: new[]
            {
                new FieldDescriptor("TicketText", "Input", "System.String",
                    "Raw ticket text", Required: true),
                new FieldDescriptor("AccountTier", "Input", "System.String",
                    "Customer tier", Required: true),
            },
            Outputs: new[]
            {
                new FieldDescriptor("Severity", "Output", "System.String",
                    "Severity level", Required: true),
            });

        Assert.Equal(2, descriptor.Inputs.Count);
        Assert.Equal(1, descriptor.Outputs.Count);
        Assert.Equal("triageticket", descriptor.Id);
    }

    [Fact]
    public void Descriptor_Record_Equality_Works()
    {
        var a = new FieldDescriptor("Severity", "Output", "System.String",
            "Severity level", Required: true);
        var b = new FieldDescriptor("Severity", "Output", "System.String",
            "Severity level", Required: true);

        Assert.Equal(a, b);  // Record structural equality
    }

    [Fact]
    public void With_Expression_Creates_Correct_Variant()
    {
        var original = new TunableParameterDescriptor(
            Id: "triage-temp", StepId: "triage",
            ParameterKind: ParameterKind.Temperature,
            Name: "Temperature",
            MinValue: 0.0, MaxValue: 1.0);

        var variant = original with { MaxValue = 0.7 };

        Assert.Equal(0.7, variant.MaxValue);
        Assert.Equal(0.0, variant.MinValue); // unchanged
    }
}
```

> **Junior Dev Note:** Records give you structural equality for free. Every `Assert.Equal` on two records compares all fields — no need to override `Equals()`.

---

## 3. Integration Tests (Runtime)

### What They Test

Step execution semantics with a mock `IChatClient`. Verifies that Predict, Retrieve, Evaluate, If, and Repair steps produce correct context and trace output when composed in a graph.

### How to Write One

1. Create a `FakeChatClient` that returns canned responses.
2. Build a `ProgramGraph` with one or more steps.
3. Execute with the `ProgramRunner`.
4. Assert on outputs and trace records.

### Example

```csharp
public class PredictStepIntegrationTests
{
    [Fact]
    public async Task Predict_Step_Produces_Structured_Output()
    {
        // Arrange
        var fakeChatClient = new FakeChatClient(responses: new Dictionary<string, string>
        {
            ["TriageTicket"] = """
            {
                "Severity": "High",
                "RouteToTeam": "Identity Platform",
                "DraftReply": "We are investigating the SSO issue.",
                "Rationale": "300+ users affected, enterprise tier.",
                "Escalate": true
            }
            """
        });

        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(fakeChatClient);
        services.AddLmpPrograms().AddProgram<SupportTriageProgram>();
        var provider = services.BuildServiceProvider();

        var program = provider.GetRequiredService<SupportTriageProgram>();

        // Act
        var result = await program.RunAsync(new TicketInput(
            TicketText: "SSO login fails for 300 EU users",
            AccountTier: "Enterprise"));

        // Assert
        Assert.Equal("High", result.Severity);
        Assert.Equal("Identity Platform", result.RouteToTeam);
        Assert.True(result.Escalate);
    }
}
```

---

## 3b. Binding Tier Tests

### What They Test

The three-tier binding model that connects step inputs/outputs:

- **Tier 1 (Convention):** Auto-binding by matching property names and types between steps.
- **Tier 2 (Attribute):** Explicit `[BindFrom("step-name", nameof(Output.Field))]` attribute binding.
- **Tier 3 (Interceptor):** C# 14 interceptor-based lambda binding (stable in .NET 10). Compile-time rewrite of lambda bindings into direct calls.
- **Tier 4 (Expression Tree):** Runtime-only fallback using `Expression<T>`.

### Example

```csharp
public class BindingTierTests
{
    [Fact]
    public async Task Tier1_Convention_Binding_Resolves_By_Name()
    {
        // Arrange: Steps with matching property names auto-bind
        var graph = Graph
            .StartWith(Step.Predict<ExtractTicketInfo>(name: "extract"))
            .Then(Step.Predict<TriageTicket>(name: "triage"));
            // TriageTicket.TicketText auto-binds to ExtractTicketInfo.TicketText
            // because the names and types match (convention-based)

        var runner = CreateRunner(graph, new FakeChatClient(/* ... */));
        var result = await runner.RunAsync(new TicketInput("SSO fails", "Enterprise"));

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Tier2_BindFrom_Attribute_Overrides_Convention()
    {
        // Arrange: [BindFrom] attribute explicitly wires step output to input
        // In the signature class:
        //   [BindFrom("retrieve-kb", nameof(RetrieveResult.Documents))]
        //   public required IReadOnlyList<string> KnowledgeSnippets { get; init; }

        var graph = Graph
            .StartWith(Step.Retrieve(name: "retrieve-kb", from: i => i.TicketText, topK: 5))
            .Then(Step.Predict<TriageTicket>(name: "triage"));
            // KnowledgeSnippets bound via [BindFrom] attribute, not convention

        var runner = CreateRunner(graph, new FakeChatClient(/* ... */));
        var result = await runner.RunAsync(new TicketInput("SSO fails", "Enterprise"));

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Tier3_Interceptor_Binding_Generates_Direct_Call()
    {
        // Arrange: C# 14 interceptor rewrites the lambda at compile time
        // The authored code uses a lambda:
        //   bind: (input, ctx) => new TriageTicket { ... }
        // The interceptor generator replaces it with a direct method call.

        var graph = Graph
            .StartWith(Step.Retrieve(name: "retrieve-kb", from: i => i.TicketText, topK: 5))
            .Then(Step.Predict<TriageTicket>(name: "triage",
                bind: (input, ctx) => new TriageTicket
                {
                    TicketText = input.TicketText,
                    AccountTier = input.AccountTier,
                    KnowledgeSnippets = ctx.OutputOf("retrieve-kb").Documents,
                    PolicySnippets = Array.Empty<string>()
                }));

        var runner = CreateRunner(graph, new FakeChatClient(/* ... */));
        var result = await runner.RunAsync(new TicketInput("SSO fails", "Enterprise"));

        Assert.NotNull(result);
        // Verify interceptor was used (no expression tree overhead)
        Assert.True(runner.LastExecutionUsedInterceptors);
    }

    [Fact]
    public async Task Tier4_ExpressionTree_Fallback_Works_At_Runtime()
    {
        // Arrange: Expression tree binding — runtime-only fallback
        // Used when interceptors are unavailable (e.g., dynamic program construction)
        var bindExpr = ExpressionBinding.Create<TicketInput, TriageTicket>(
            (input, ctx) => new TriageTicket
            {
                TicketText = input.TicketText,
                AccountTier = input.AccountTier,
                KnowledgeSnippets = ctx.OutputOf("retrieve-kb").Documents,
                PolicySnippets = Array.Empty<string>()
            });

        var graph = Graph
            .StartWith(Step.Retrieve(name: "retrieve-kb", from: i => i.TicketText, topK: 5))
            .Then(Step.Predict<TriageTicket>(name: "triage", bindExpression: bindExpr));

        var runner = CreateRunner(graph, new FakeChatClient(/* ... */));
        var result = await runner.RunAsync(new TicketInput("SSO fails", "Enterprise"));

        Assert.NotNull(result);
        Assert.False(runner.LastExecutionUsedInterceptors); // expression tree path
    }
}
```

> **Junior Dev Note:** The binding model is a tiered fallback: convention first, then attributes, then interceptors, then expression trees. For most programs, Tier 1 (convention) or Tier 2 (`[BindFrom]`) is sufficient. Interceptors (Tier 3) handle complex data flow with zero runtime overhead. Expression trees (Tier 4) are a runtime-only fallback for dynamically constructed programs.

---

## 4. Golden/Snapshot Tests (Source Generator)

### What They Test

That the source generator produces **deterministic, stable** `.g.cs` output for a given input signature or program. Any unintended change to generated code will cause the snapshot to fail.

### Pattern

Use the [Verify](https://github.com/VerifyTests/Verify) library for snapshot testing. Place expected outputs in a `Snapshots/` directory alongside the test.

### How to Write One

1. Create a Roslyn `CSharpCompilation` with your authored source.
2. Run the generator.
3. Capture the generated source text.
4. Call `Verify()` to compare against the snapshot file.

### Example

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

public class SignatureGeneratorSnapshotTests
{
    [Fact]
    public Task Generated_Descriptor_Matches_Snapshot()
    {
        // Arrange: the authored source
        var source = """
            using LMP;

            [LmpSignature(Instructions = "Classify severity.")]
            public partial class SimpleTicket
            {
                [Input(Description = "Ticket text")]
                public required string TicketText { get; init; }

                [Output(Description = "Severity level")]
                public required string Severity { get; init; }
            }
            """;

        // Act: run the generator
        var compilation = CreateCompilation(source);
        var generator = new LmpSignatureGenerator();
        var driver = CSharpGeneratorDriver.Create(generator)
            .RunGenerators(compilation);

        // Assert: snapshot verification
        return Verify(driver);
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location));

        return CSharpCompilation.Create("TestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
```

**Snapshot file** (`Snapshots/SignatureGeneratorSnapshotTests.Generated_Descriptor_Matches_Snapshot.verified.cs`):

```csharp
// <auto-generated />
internal static class SimpleTicket_Descriptor
{
    public static readonly SignatureDescriptor Instance = new(
        Id: "simpleticket",
        Name: "SimpleTicket",
        Instructions: "Classify severity.",
        Inputs: new[]
        {
            new FieldDescriptor("TicketText", "Input", "System.String",
                "Ticket text", true),
        },
        Outputs: new[]
        {
            new FieldDescriptor("Severity", "Output", "System.String",
                "Severity level", true),
        });
}
```

> **Junior Dev Note:** When you intentionally change the generator output, run `dotnet test` and the snapshot will fail. Review the `.received.cs` diff, and if it looks correct, copy it over the `.verified.cs` file to update the snapshot.

---

## 5. Analyzer Tests

### What They Test

That Roslyn analyzers correctly detect invalid authoring patterns and produce the expected diagnostic IDs (LMP001–LMP006).

### Pattern

Use `Microsoft.CodeAnalysis.CSharp.Testing.XUnit` and `CSharpAnalyzerVerifier`.

### Example

```csharp
using Microsoft.CodeAnalysis.Testing;
using Xunit;

public class MissingDescriptionAnalyzerTests
{
    [Fact]
    public async Task LMP001_Reported_When_Field_Has_No_Description()
    {
        var source = """
            using LMP;

            [LmpSignature(Instructions = "Triage tickets.")]
            public partial class BadSignature
            {
                [Input]  // Missing Description!
                public required string {|#0:TicketText|} { get; init; }

                [Output(Description = "Severity")]
                public required string Severity { get; init; }
            }
            """;

        var expected = new DiagnosticResult("LMP001", DiagnosticSeverity.Warning)
            .WithLocation(0)
            .WithMessage("Field 'TicketText' is missing a Description. " +
                         "Add Description to the [Input] attribute.");

        await CSharpAnalyzerVerifier<MissingDescriptionAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task LMP003_Reported_For_Duplicate_Step_Names()
    {
        var source = """
            using LMP;

            [LmpProgram("test-program")]
            public partial class BadProgram : LmpProgram<string, string>
            {
                public override ProgramGraph Build()
                {
                    var step1 = Step.Predict<SimpleTicket>(name: "triage", bind: (i, c) => ...);
                    var step2 = Step.Predict<SimpleTicket>(name: {|#0:"triage"|}, bind: (i, c) => ...);
                    // ...
                }
            }
            """;

        var expected = new DiagnosticResult("LMP003", DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithMessage("Duplicate step name 'triage' in program 'test-program'.");

        await CSharpAnalyzerVerifier<DuplicateStepNameAnalyzer>.VerifyAnalyzerAsync(source, expected);
    }
}
```

---

## 6. Compiler Tests

### What They Test

The optimization loop: candidate proposal, trial execution, constraint enforcement, selection of the best valid variant, and compile report generation.

### Pattern

Use a mock `IChatClient` and a mock evaluator so no real API calls are made. Verify that the compiler correctly rejects constraint-violating candidates and selects the best valid one.

### Example

```csharp
public class CompilerConstraintTests
{
    [Fact]
    public async Task Compiler_Rejects_Candidates_Violating_Hard_Constraints()
    {
        // Arrange
        var fakeClient = new FakeChatClient(/* deterministic responses */);
        var fakeEvaluator = new FakeEvaluator(scores: new Dictionary<string, double>
        {
            ["routing_accuracy"] = 0.90,
            ["policy_pass_rate"] = 0.80,  // Violates hard constraint
        });

        var spec = CompileSpec
            .For<SupportTriageProgram>()
            .WithTrainingSet("data/test-train.jsonl")
            .WithValidationSet("data/test-val.jsonl")
            .Optimize(search =>
            {
                search.Temperature(step: "triage", min: 0.0, max: 0.5);
            })
            .ScoreWith(Metrics.Weighted(("routing_accuracy", 1.0)))
            .Constrain(rules =>
            {
                rules.Require("policy_pass_rate", m => m["policy_pass_rate"] >= 1.0);
            })
            .UseOptimizer(Optimizers.RandomSearch(maxTrials: 5));

        var compiler = new ProgramCompiler(fakeClient, fakeEvaluator);

        // Act
        var report = await compiler.CompileAsync(spec);

        // Assert
        Assert.False(report.Approved);
        Assert.Equal(0, report.ValidTrialCount);
        Assert.True(report.RejectedTrialCount > 0);
    }

    [Fact]
    public async Task Compiler_Selects_Best_Valid_Variant()
    {
        var fakeClient = new FakeChatClient(/* deterministic */);
        var fakeEvaluator = new FakeEvaluator(scores: new Dictionary<string, double>
        {
            ["routing_accuracy"] = 0.92,
            ["policy_pass_rate"] = 1.0,
            ["p95_latency_ms"] = 1800,
            ["avg_cost_usd"] = 0.02,
        });

        var spec = CompileSpec
            .For<SupportTriageProgram>()
            .WithTrainingSet("data/test-train.jsonl")
            .WithValidationSet("data/test-val.jsonl")
            .Optimize(search =>
            {
                search.Temperature(step: "triage", min: 0.0, max: 0.5);
            })
            .ScoreWith(Metrics.Weighted(("routing_accuracy", 1.0)))
            .Constrain(rules =>
            {
                rules.Require("policy_pass_rate", m => m["policy_pass_rate"] >= 1.0);
                rules.Require("p95_latency_ms", m => m["p95_latency_ms"] <= 2500);
            })
            .UseOptimizer(Optimizers.RandomSearch(maxTrials: 10));

        var compiler = new ProgramCompiler(fakeClient, fakeEvaluator);

        // Act
        var report = await compiler.CompileAsync(spec);

        // Assert
        Assert.True(report.Approved);
        Assert.NotNull(report.BestVariantId);
        Assert.True(report.BestMetrics["routing_accuracy"] >= 0.90);
    }
}
```

---

## 6b. Pipeline and Middleware Tests

### What They Test

TPL Dataflow pipeline construction (steps compose into `TransformBlock`/`ActionBlock` graphs), M.E.AI middleware composition (`UseOpenTelemetry()`, `UseDistributedCache()`, `UseLogging()` are M.E.AI built-ins; LMP provides only `UseLmpStepContext()` and `UseLmpCostTracking()`), and predicate-based constraint evaluation.

### Pattern

Build a pipeline or middleware chain programmatically, execute against mock inputs, and verify the expected blocks/middleware are invoked in order.

### Example

```csharp
public class TplDataflowPipelineTests
{
    [Fact]
    public async Task Pipeline_Constructs_Dataflow_Graph_From_Steps()
    {
        // Arrange
        var graph = ProgramGraph.Create(
            Step.Retrieve("retrieve-kb"),
            Step.Predict("triage"),
            Step.Evaluate("groundedness-check"));

        var fakeClient = new FakeChatClient(/* deterministic */);
        var pipeline = new DataflowPipelineBuilder(fakeClient)
            .Build(graph);

        // Act
        var result = await pipeline.ProcessAsync(new TicketInput(
            TicketText: "SSO fails", AccountTier: "Enterprise"));

        // Assert — all steps executed via dataflow blocks
        Assert.NotNull(result);
        Assert.Equal(3, result.Trace.StepCount);
    }
}

public class MiddlewarePipelineTests
{
    [Fact]
    public void ChatClient_Uses_BuiltIn_MEAI_Middleware_Plus_LMP_Middleware()
    {
        // Arrange — M.E.AI built-in middleware + LMP-only middleware
        var client = new ChatClientBuilder(new FakeChatClient())
            .UseOpenTelemetry()          // M.E.AI built-in
            .UseDistributedCache(cache)  // M.E.AI built-in
            .UseLogging(logger)          // M.E.AI built-in
            .UseLmpStepContext()         // LMP-specific
            .UseLmpCostTracking()        // LMP-specific
            .Build();

        // Assert — pipeline is constructed without error
        Assert.NotNull(client);
    }
}

public class PredicateConstraintTests
{
    [Fact]
    public void Predicate_Constraint_Rejects_When_Lambda_Returns_False()
    {
        // Arrange
        var constraint = new PredicateConstraint(
            "policy_pass_rate",
            m => m["policy_pass_rate"] >= 1.0);

        var metrics = new Dictionary<string, double>
        {
            ["policy_pass_rate"] = 0.90
        };

        // Act
        var result = constraint.Evaluate(metrics);

        // Assert
        Assert.False(result.Passed);
        Assert.Equal("policy_pass_rate", result.ConstraintName);
    }

    [Fact]
    public void Predicate_Constraint_Passes_When_Lambda_Returns_True()
    {
        var constraint = new PredicateConstraint(
            "p95_latency_ms",
            m => m["p95_latency_ms"] <= 2500);

        var metrics = new Dictionary<string, double>
        {
            ["p95_latency_ms"] = 1800
        };

        var result = constraint.Evaluate(metrics);

        Assert.True(result.Passed);
    }
}
```

---

## 7. Artifact Snapshot Tests

### What They Test

That compiled artifact JSON serialization is deterministic, stable, and round-trippable. Catches accidental schema changes.

### Example

```csharp
public class ArtifactSerializationTests
{
    [Fact]
    public Task Artifact_JSON_Matches_Snapshot()
    {
        var artifact = new CompiledArtifact
        {
            Program = "support-triage",
            CompiledVersion = "0.1.0",
            VariantId = "triage-v7",
            BaseProgramHash = "sha256:abc123",
            SelectedParameters = new Dictionary<string, object>
            {
                ["triage.model"] = "gpt-4.1-mini",
                ["triage.temperature"] = 0.1,
                ["retrieve-kb.topK"] = 6,
            },
            ValidationMetrics = new Dictionary<string, double>
            {
                ["routing_accuracy"] = 0.89,
                ["policy_pass_rate"] = 1.0,
            },
            Approved = true,
        };

        var json = ArtifactSerializer.Serialize(artifact);
        return Verify(json);
    }

    [Fact]
    public void Artifact_Roundtrips_Correctly()
    {
        var original = new CompiledArtifact { /* ... */ };
        var json = ArtifactSerializer.Serialize(original);
        var loaded = ArtifactSerializer.Deserialize(json);

        Assert.Equal(original.VariantId, loaded.VariantId);
        Assert.Equal(original.SelectedParameters["triage.temperature"],
                     loaded.SelectedParameters["triage.temperature"]);
        Assert.Equal(original.Approved, loaded.Approved);
    }
}
```

---

## 8. End-to-End Test

### What It Tests

The full pipeline: build the sample program → compile with optimizer → save artifact → load artifact → run with artifact-pinned settings.

### Complete Example

```csharp
[Trait("Category", "E2E")]
public class FullPipelineE2ETests
{
    [Fact]
    public async Task Build_Compile_Save_Load_Run()
    {
        // --- Step 1: Build (verify generated code exists) ---
        var descriptor = TriageTicket_Descriptor.Instance;
        Assert.Equal("triageticket", descriptor.Id);
        Assert.Equal(4, descriptor.Inputs.Count);
        Assert.Equal(5, descriptor.Outputs.Count);

        // --- Step 2: Set up runtime with fake client ---
        var fakeClient = new FakeChatClient(responses: new Dictionary<string, string>
        {
            ["TriageTicket"] = """
            {
                "Severity": "Critical",
                "RouteToTeam": "Identity Platform",
                "DraftReply": "We are investigating.",
                "Rationale": "Enterprise tier, 300+ users.",
                "Escalate": true
            }
            """
        });

        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(fakeClient);
        services.AddSingleton<IDocumentRetriever>(new FakeDocumentRetriever());
        services.AddLmpPrograms().AddProgram<SupportTriageProgram>();
        var provider = services.BuildServiceProvider();

        // --- Step 3: Compile ---
        var compiler = provider.GetRequiredService<IProgramCompiler>();
        var spec = CompileSpec
            .For<SupportTriageProgram>()
            .WithTrainingSet("data/test-train.jsonl")
            .WithValidationSet("data/test-val.jsonl")
            .Optimize(s => { s.Temperature(step: "triage", min: 0.0, max: 0.5); })
            .ScoreWith(Metrics.Weighted(("routing_accuracy", 1.0)))
            .Constrain(rules => { rules.Require("policy_pass_rate", m => m["policy_pass_rate"] >= 1.0); })
            .UseOptimizer(Optimizers.RandomSearch(maxTrials: 3));

        var report = await compiler.CompileAsync(spec);
        Assert.True(report.Approved);

        // --- Step 4: Save artifact ---
        var artifactPath = Path.Combine(
            Path.GetTempPath(), $"test-artifact-{Guid.NewGuid()}.json");
        try
        {
            await ArtifactSerializer.SaveAsync(report.Artifact, artifactPath);
            Assert.True(File.Exists(artifactPath));

            // --- Step 5: Load artifact ---
            var loader = provider.GetRequiredService<ICompiledArtifactLoader>();
            var loadedArtifact = await loader.LoadAsync(artifactPath);
            Assert.Equal(report.Artifact.VariantId, loadedArtifact.VariantId);

            // --- Step 6: Run with artifact ---
            var program = provider.GetRequiredService<SupportTriageProgram>();
            program.ApplyArtifact(loadedArtifact);

            var result = await program.RunAsync(new TicketInput(
                TicketText: "SSO login fails",
                AccountTier: "Enterprise"));

            Assert.Equal("Critical", result.Severity);
            Assert.True(result.Escalate);
        }
        finally
        {
            File.Delete(artifactPath);
        }
    }
}
```

> **Junior Dev Note:** The E2E test uses `FakeChatClient` so it runs without an API key. It still exercises the full pipeline: DI setup → graph execution → compilation → serialization → reload → re-execution.

---

## 9. Test Infrastructure

### FakeChatClient

A mock implementation of `IChatClient` that returns pre-configured responses based on the signature name.

```csharp
public class FakeChatClient : IChatClient
{
    private readonly Dictionary<string, string> _responses;

    public FakeChatClient(Dictionary<string, string> responses)
    {
        _responses = responses;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Extract signature name from system prompt or metadata
        var signatureName = ExtractSignatureName(messages);
        var responseJson = _responses.GetValueOrDefault(signatureName, "{}");

        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, responseJson)));
    }

    public ChatClientMetadata Metadata => new("FakeModel");

    // ... IDisposable, other members
}
```

### FakeEvaluator

Returns configurable scores for compiler tests.

```csharp
public class FakeEvaluator : IEvaluator
{
    private readonly Dictionary<string, double> _scores;

    public FakeEvaluator(Dictionary<string, double> scores)
    {
        _scores = scores;
    }

    public Task<EvaluationResult> EvaluateAsync(/* ... */)
    {
        return Task.FromResult(new EvaluationResult(_scores));
    }
}
```

### FakeDocumentRetriever

Returns static document snippets for Retrieve steps.

```csharp
public class FakeDocumentRetriever : IDocumentRetriever
{
    public Task<IReadOnlyList<string>> RetrieveAsync(string query, int topK,
        CancellationToken ct = default)
    {
        var docs = new List<string>
        {
            "KB: SSO issues can be caused by certificate rotation.",
            "KB: Enterprise tier customers receive 4-hour SLA.",
            "Policy: Critical issues must be escalated within 1 hour.",
        };
        return Task.FromResult<IReadOnlyList<string>>(docs.Take(topK).ToList());
    }
}
```

---

## 10. Acceptance Criteria as Test Cases

These are the 10 acceptance criteria from spec Section 17, mapped to verifiable test cases.

| # | Criterion | Test Name | Test Project |
|---|-----------|-----------|-------------|
| 1 | Developer can author a signature using attributes | `Signature_Compiles_With_Attributes` | `LMP.Abstractions.Tests` |
| 2 | Source generation emits deterministic descriptor | `Generated_Descriptor_Matches_Snapshot` | `LMP.Roslyn.Tests` |
| 3 | Developer can author multi-step triage program | `MultiStep_Program_Graph_Is_Valid` | `LMP.Abstractions.Tests` |
| 4 | Runtime executes program using `IChatClient` | `Predict_Step_Produces_Structured_Output` | `LMP.Runtime.Tests` |
| 5 | Evaluators can score outputs on a dataset | `Evaluator_Returns_Scores_For_Dataset` | `LMP.Compiler.Tests` |
| 6 | Compile loop searches over ≥3 tunable dimensions | `Compiler_Explores_Three_Dimensions` | `LMP.Compiler.Tests` |
| 7 | Compiler emits best valid variant and report | `Compiler_Selects_Best_Valid_Variant` | `LMP.Compiler.Tests` |
| 8 | Compiled artifact can be saved and loaded | `Artifact_Roundtrips_Correctly` | `LMP.Compiler.Tests` |
| 9 | At least 3 meaningful diagnostics exist | `LMP001_LMP002_LMP003_Diagnostics_Fire` | `LMP.Roslyn.Tests` |
| 10 | Sample ticket triage demo works end to end | `Build_Compile_Save_Load_Run` | `LMP.E2E.Tests` |

### Example: Criterion 6 — Compile loop searches ≥3 dimensions

```csharp
[Fact]
public async Task Compiler_Explores_Three_Dimensions()
{
    var spec = CompileSpec
        .For<SupportTriageProgram>()
        .WithTrainingSet("data/test-train.jsonl")
        .WithValidationSet("data/test-val.jsonl")
        .Optimize(search =>
        {
            search.Temperature(step: "triage", min: 0.0, max: 0.7);       // Dimension 1
            search.Model(step: "triage", allowed: ["gpt-4.1-mini", "gpt-4.1"]); // Dimension 2
            search.RetrievalTopK(step: "retrieve-kb", min: 3, max: 8);    // Dimension 3
        })
        .ScoreWith(Metrics.Weighted(("routing_accuracy", 1.0)))
        .UseOptimizer(Optimizers.RandomSearch(maxTrials: 10));

    var compiler = new ProgramCompiler(new FakeChatClient(/*...*/), new FakeEvaluator(/*...*/));
    var report = await compiler.CompileAsync(spec);

    // The search space must have at least 3 tunable dimensions
    Assert.True(report.SearchSpaceDimensionCount >= 3);
    Assert.True(report.TrialsExecuted >= 3);
}
```

---

## 11. Code Coverage Expectations

| Project | Target | Notes |
|---------|--------|-------|
| `LMP.Abstractions` | ≥ 90% | Pure data types — easy to cover |
| `LMP.Runtime` | ≥ 80% | Mock `IChatClient` covers most paths |
| `LMP.Compiler` | ≥ 75% | Optimizer loop has many branches |
| `LMP.Roslyn` | ≥ 85% | Every diagnostic must have a test |
| `LMP.Evaluation` | ≥ 80% | Dataset loading, score aggregation |
| Overall | ≥ 80% | Measured by `dotnet test --collect:"XPlat Code Coverage"` |

### Running Coverage

```bash
dotnet test LMP.sln \
  --collect:"XPlat Code Coverage" \
  --results-directory ./coverage

# Generate HTML report (install reportgenerator first)
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:./coverage/**/coverage.cobertura.xml \
  -targetdir:./coverage/report -reporttypes:Html
```

> **Junior Dev Note:** Don't chase 100% coverage. Focus on testing *behavior*, not lines. A test that verifies "the compiler rejects constraint-violating candidates" is worth more than a test that exercises a trivial property getter.
