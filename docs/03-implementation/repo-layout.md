# Repository Layout Plan

> **Derived from:** Spec Section 15 (Repository Layout), with supporting context from Sections 7, 12, 14, 16.
>
> **Audience:** Implementers setting up the solution from scratch.

---

## 1. Complete Solution Layout

```
LMP.sln
│
├── /src
│   ├── /LMP.Abstractions          # Shared contracts, attributes, IR types
│   ├── /LMP.Runtime               # Program execution engine
│   ├── /LMP.Compiler              # Optimization loop, trial orchestration
│   ├── /LMP.Roslyn                # Analyzers + source generators
│   ├── /LMP.Interceptors          # C# 14 interceptor-based lambda binding
│   ├── /LMP.Tasks                 # MSBuild tasks (EmitIr, ValidateGraph)
│   ├── /LMP.Evaluation            # Dataset runner, score aggregation
│   ├── /LMP.Cli                   # dotnet-lmp global tool
│   └── /LMP.Samples.SupportTriage # Canonical ticket-triage demo
│
│   # Post-MVP
│   # ├── /LMP.Aspire.Hosting     # Aspire component: AddLmpCompiler()
│   # ├── /LMP.Sdk                # Custom MSBuild SDK: <Project Sdk="LMP.Sdk/1.0.0">
│
├── /tests
│   ├── /LMP.Abstractions.Tests    # Unit tests for IR types and contracts
│   ├── /LMP.Runtime.Tests         # Unit tests for step execution
│   ├── /LMP.Compiler.Tests        # Compiler loop and constraint tests
│   ├── /LMP.Roslyn.Tests          # Analyzer + generator snapshot tests
│   └── /LMP.E2E.Tests             # Full build→compile→save→load→run
│
├── /docs
│   ├── /00-product
│   ├── /01-architecture
│   ├── /02-specs
│   ├── /03-implementation
│   └── /04-demo
│
├── /data                          # Sample JSONL datasets for demo/tests
│   ├── support-triage-train.jsonl
│   └── support-triage-val.jsonl
│
├── /artifacts                     # Output directory for compiled artifacts
│   └── (generated at compile time)
│
├── Directory.Build.props          # Shared MSBuild properties
├── Directory.Packages.props       # Central package management
├── global.json                    # SDK version pin
├── .editorconfig                  # Code style
└── README.md
```

---

## 2. Project Purposes and Dependencies

### 2.1 LMP.Abstractions

**Purpose:** The shared contract layer. Contains attributes (`[LmpSignature]`, `[Input]`, `[Output]`, `[LmpProgram]`), base classes (`LmpProgram<TIn, TOut>`), the complete LM Program IR (all descriptor records), and public API interfaces.

**May reference:** Nothing in the solution — leaf dependency.

**Key types:** `SignatureDescriptor`, `ProgramDescriptor`, `StepDescriptor`, `FieldDescriptor`, `EdgeDescriptor`, `TunableParameterDescriptor`, `VariantDescriptor`, `TrialResultDescriptor`, `CompileReportDescriptor`, `RuntimeTraceDescriptor`, `ConstraintDescriptor`, `ProgramGraph`, `Step` (static factory).

> **Junior Dev Note:** This project is the foundation. If you're unsure where a type belongs, ask: "Does this type need to be visible to both the runtime AND the compiler?" If yes, it goes in Abstractions.

### 2.2 LMP.Runtime

**Purpose:** Executes LM programs. Owns step execution (Predict, Retrieve, Evaluate, If, Repair), the execution context, prompt assembly, trace collection, `IChatClient` integration, and artifact application at runtime.

**May reference:** `LMP.Abstractions`.

**Key types:** `ProgramRunner`, `StepExecutor`, `ExecutionContext`, `PromptAssembler`, `RuntimeTraceCollector`, `IDocumentRetriever`.

### 2.3 LMP.Compiler

**Purpose:** Orchestrates the optimization loop. Owns search-space construction, trial execution, constraint enforcement, selection of the best valid variant, compile report generation, and artifact emission.

**May reference:** `LMP.Abstractions`, `LMP.Runtime`, `LMP.Evaluation`.

**Key types:** `ProgramCompiler` (implements `IProgramCompiler`), `CompileSpec`, `IOptimizerBackend`, `RandomSearchBackend`, `ConstraintEvaluator`, `ArtifactSerializer`.

> **Junior Dev Note:** The Compiler depends on Runtime because it needs to *run* candidate variants on training data. This is the only reason for this dependency — the compiler never leaks runtime internals into the artifact.

### 2.4 LMP.Roslyn

**Purpose:** Build-time tooling. Contains Roslyn analyzers (LMP001–LMP006), code fixes, and the incremental source generator that emits `.g.cs` descriptor files.

**May reference:** `LMP.Abstractions` (for attribute type names and IR shapes only). Must NOT reference `LMP.Runtime` or `LMP.Compiler`.

**Key types:** `LmpSignatureGenerator` (implements `IIncrementalGenerator`), `LmpProgramGenerator`, `MissingDescriptionAnalyzer`, `DuplicateStepNameAnalyzer`.

**Special packaging note:** This project produces an analyzer/generator assembly. Its `.csproj` must set `<EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>` and its NuGet package must pack the assembly into the `analyzers/dotnet/cs` folder.

### 2.5 LMP.Interceptors

**Purpose:** C# 14 interceptor-based lambda binding (Tier 3 of the three-tier binding model). Generates interceptor methods at compile time that replace expression-tree-based data flow with direct, AOT-safe method calls. Stable in .NET 10 / C# 14.

**May reference:** `LMP.Abstractions`.

**Key types:** `LmpInterceptorGenerator` (implements `IIncrementalGenerator`), `BindFromInterceptorEmitter`.

> **Junior Dev Note:** Interceptors rewrite lambda binding calls at compile time, eliminating the runtime cost of expression tree interpretation. This is the preferred binding tier for new code — convention-based (Tier 1) or `[BindFrom]` attribute (Tier 2) for simple cases, interceptors (Tier 3) for complex data flow, expression trees (Tier 4) as a runtime-only fallback.

### 2.6 LMP.Evaluation

**Purpose:** Dataset loading, evaluator invocation, and score aggregation. Integrates with `Microsoft.Extensions.AI.Evaluation` for production-grade evaluators (groundedness, coherence, fluency).

**May reference:** `LMP.Abstractions`.

**Key types:** `DatasetLoader`, `EvaluationRunner`, `ScoreAggregator`, `CustomPolicyEvaluator`.

### 2.7 LMP.Cli

**Purpose:** The `dotnet lmp` global tool. Thin orchestration layer that calls into Runtime, Compiler, and Evaluation.

**May reference:** `LMP.Abstractions`, `LMP.Runtime`, `LMP.Compiler`, `LMP.Evaluation`.

**Key types:** `CompileCommand`, `RunCommand`, `EvalCommand`, `InspectArtifactCommand`.

### 2.8 LMP.Samples.SupportTriage

**Purpose:** The canonical MVP demo application. Contains the `TriageTicket` signature, `SupportTriageProgram`, input/output records, and a `Program.cs` entry point.

**May reference:** Only public APIs from `LMP.Abstractions`. Receives `LMP.Roslyn` as an analyzer reference (not a project reference).

---

## 3. Dependency Rules (Enforced)

```
                    ┌─────────────────┐
                    │ LMP.Abstractions│  ◄── Leaf. No project refs.
                    └──────┬──────────┘
                           │
              ┌────────────┼────────────┬──────────────┐
              ▼            ▼            ▼              ▼
        ┌──────────┐ ┌──────────┐ ┌───────────┐ ┌──────────┐
        │LMP.Roslyn│ │LMP.Runtime│ │LMP.Eval   │ │ Samples  │
        │(analyzer)│ │          │ │           │ │(app only)│
        └──────────┘ └─────┬────┘ └─────┬─────┘ └──────────┘
                           │            │
                           ▼            ▼
                      ┌─────────────────────┐
                      │   LMP.Compiler      │
                      │ (refs: Abstractions, │
                      │  Runtime, Evaluation)│
                      └──────────┬──────────┘
                                 │
                                 ▼
                          ┌────────────┐
                          │  LMP.Cli   │
                          │ (refs: all)│
                          └────────────┘
```

### Hard Rules

| Rule | Rationale |
|------|-----------|
| `LMP.Abstractions` references **no** solution projects | Prevents circular deps; keeps contracts portable |
| `LMP.Roslyn` references **only** `LMP.Abstractions` | Analyzers/generators run inside the compiler process — no runtime code allowed |
| `LMP.Runtime` references **only** `LMP.Abstractions` | Runtime must not know about optimization |
| `LMP.Evaluation` references **only** `LMP.Abstractions` | Evaluation is a utility layer |
| `LMP.Compiler` references `Abstractions`, `Runtime`, `Evaluation` | Needs to run trials and score them |
| `LMP.Cli` may reference everything | It's the composition root |
| `LMP.Samples.*` references **only** public API types | Proves the public API is sufficient |

> **Junior Dev Note:** If you find yourself adding a `<ProjectReference>` from Runtime to Compiler, stop. That's a dependency violation. The compiler calls the runtime, not the other way around.

---

## 4. Complete .csproj Snippets

### 4.1 LMP.Abstractions

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>LMP</RootNamespace>
  </PropertyGroup>

  <!-- No project references — leaf dependency -->

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="10.*" />
    <PackageReference Include="System.Text.Json" Version="10.*" />
    <PackageReference Include="System.Collections.Immutable" Version="10.*" />
  </ItemGroup>
</Project>
```

### 4.2 LMP.Runtime

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>LMP.Runtime</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\LMP.Abstractions\LMP.Abstractions.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.AI" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.*" />
    <PackageReference Include="System.Diagnostics.DiagnosticSource" Version="10.*" />
    <PackageReference Include="System.Threading.Tasks.Dataflow" Version="10.*" />
    <PackageReference Include="System.Numerics.Tensors" Version="10.*" />
  </ItemGroup>
</Project>
```

### 4.3 LMP.Compiler

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>LMP.Compilation</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\LMP.Abstractions\LMP.Abstractions.csproj" />
    <ProjectReference Include="..\LMP.Runtime\LMP.Runtime.csproj" />
    <ProjectReference Include="..\LMP.Evaluation\LMP.Evaluation.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="System.Threading.RateLimiting" Version="10.*" />
    <PackageReference Include="System.Text.Json" Version="10.*" />
    <PackageReference Include="System.IO.Hashing" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Resilience" Version="10.*" />
  </ItemGroup>
</Project>
```

### 4.4 LMP.Roslyn

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <RootNamespace>LMP.Roslyn</RootNamespace>
    <IsRoslynComponent>true</IsRoslynComponent>
  </PropertyGroup>

  <ItemGroup>
    <!-- Analyzers/generators must target netstandard2.0 -->
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.*"
                      PrivateAssets="all" />
    <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" Version="3.*"
                      PrivateAssets="all" />
  </ItemGroup>

  <!-- Note: references LMP.Abstractions only for attribute names (compile-time constants).
       The generator discovers types by attribute name, not by loading the assembly. -->
</Project>
```

> **Junior Dev Note:** Source generators MUST target `netstandard2.0` — this is a Roslyn requirement. You cannot use .NET 10 APIs inside the generator itself. The generator inspects source code using the Roslyn API; it doesn't execute your framework code.

### 4.5 LMP.Evaluation

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>LMP.Evaluation</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\LMP.Abstractions\LMP.Abstractions.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.AI.Evaluation" Version="10.*" />
  </ItemGroup>
</Project>
```

### 4.6 LMP.Cli

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>LMP.Cli</RootNamespace>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>dotnet-lmp</ToolCommandName>
    <!-- AOT-safe configuration binding (.NET 8+) -->
    <EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\LMP.Abstractions\LMP.Abstractions.csproj" />
    <ProjectReference Include="..\LMP.Runtime\LMP.Runtime.csproj" />
    <ProjectReference Include="..\LMP.Compiler\LMP.Compiler.csproj" />
    <ProjectReference Include="..\LMP.Evaluation\LMP.Evaluation.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="System.CommandLine" Version="2.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.*" />
  </ItemGroup>
</Project>
```

### 4.7 LMP.Samples.SupportTriage

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>LMP.Samples.SupportTriage</RootNamespace>
    <!-- AOT-safe configuration binding (.NET 8+) -->
    <EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\LMP.Abstractions\LMP.Abstractions.csproj" />
    <ProjectReference Include="..\LMP.Runtime\LMP.Runtime.csproj" />
  </ItemGroup>

  <!-- Analyzer/generator reference — not a project reference -->
  <ItemGroup>
    <ProjectReference Include="..\LMP.Roslyn\LMP.Roslyn.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.AI" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.*" />
  </ItemGroup>
</Project>
```

---

## 5. Build Order and Verification

### Topological Build Order

```
1. LMP.Abstractions        (no deps)
2. LMP.Roslyn              (depends on Abstractions attribute names only)
3. LMP.Interceptors        (depends on Abstractions; C# 14 interceptor generator)
4. LMP.Runtime             (depends on Abstractions)
5. LMP.Evaluation          (depends on Abstractions)
6. LMP.Compiler            (depends on Abstractions, Runtime, Evaluation)
7. LMP.Cli                 (depends on all library projects)
8. LMP.Samples.*           (depends on Abstractions, Runtime; uses Roslyn + Interceptors as analyzers)
```

### Verification Commands

```bash
# Clean build from root
dotnet build LMP.sln --no-incremental

# Run all tests
dotnet test LMP.sln --no-build --verbosity normal

# Verify no circular dependency issues
dotnet build src/LMP.Abstractions/LMP.Abstractions.csproj
dotnet build src/LMP.Roslyn/LMP.Roslyn.csproj
dotnet build src/LMP.Runtime/LMP.Runtime.csproj
dotnet build src/LMP.Evaluation/LMP.Evaluation.csproj
dotnet build src/LMP.Compiler/LMP.Compiler.csproj
dotnet build src/LMP.Cli/LMP.Cli.csproj
dotnet build src/LMP.Samples.SupportTriage/LMP.Samples.SupportTriage.csproj
```

---

## 6. Directory Conventions

| Content | Location | Example |
|---------|----------|---------|
| Generated `.g.cs` files | Emitted to `obj/` by source generator (visible in IDE under Dependencies → Analyzers) | `TriageTicket.g.cs` |
| Golden test snapshots | `/tests/<Project>.Tests/Snapshots/` | `TriageTicket_Descriptor.verified.cs` |
| Sample data files | `/data/` at repo root | `support-triage-train.jsonl` |
| Compiled artifacts | `/artifacts/` at repo root (gitignored) | `support-triage/artifact.json` |
| Test fixtures/fakes | `/tests/<Project>.Tests/Fakes/` | `FakeChatClient.cs` |
| Shared build props | Repo root `Directory.Build.props` | Central `<TreatWarningsAsErrors>` |

---

## 7. Sample Project Structure

```
/src/LMP.Samples.SupportTriage
├── Program.cs                  # Entry point: DI setup, RunAsync call
├── TriageTicket.cs             # [LmpSignature] with Input/Output fields
├── SupportTriageProgram.cs     # [LmpProgram] with Build() graph
├── Models/
│   ├── TicketInput.cs          # sealed record TicketInput(...)
│   └── TriageResult.cs         # sealed record TriageResult(...)
├── Evaluators/
│   └── CustomPolicyEvaluator.cs
└── obj/
    └── Generated/
        └── LMP.Roslyn/
            ├── TriageTicket.g.cs
            └── SupportTriageProgram.g.cs
```

> **Junior Dev Note:** The files under `obj/Generated/` are auto-generated during build. You never edit them directly. They appear in your IDE under the "Analyzers" node in Solution Explorer. If they look wrong, rebuild and check for LMP diagnostics in the Error List.

---

## 8. CI/CD Pipeline Recommendations

### GitHub Actions Workflow Outline

```yaml
name: CI
on: [push, pull_request]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore
        run: dotnet restore LMP.sln

      - name: Build (Release)
        run: dotnet build LMP.sln -c Release --no-restore

      - name: Unit + Analyzer Tests
        run: dotnet test LMP.sln -c Release --no-build --filter "Category!=E2E"

      - name: E2E Tests (requires API key)
        if: github.event_name == 'push' && github.ref == 'refs/heads/main'
        env:
          OPENAI_API_KEY: ${{ secrets.OPENAI_API_KEY }}
        run: dotnet test LMP.sln -c Release --no-build --filter "Category=E2E"
```

### CI Checks

| Check | When | Blocks Merge? |
|-------|------|---------------|
| `dotnet build` (Release, no warnings) | Every PR | Yes |
| Unit + analyzer + snapshot tests | Every PR | Yes |
| E2E tests with mock `IChatClient` | Every PR | Yes |
| E2E tests with live API | Main branch only | No (advisory) |
| NuGet pack validation | Release tags | Yes |

### Artifact Publishing

```bash
# Pack the CLI as a global tool
dotnet pack src/LMP.Cli/LMP.Cli.csproj -c Release -o ./nupkgs

# Pack the framework libraries
dotnet pack src/LMP.Abstractions/LMP.Abstractions.csproj -c Release -o ./nupkgs
dotnet pack src/LMP.Runtime/LMP.Runtime.csproj -c Release -o ./nupkgs
dotnet pack src/LMP.Roslyn/LMP.Roslyn.csproj -c Release -o ./nupkgs
```

---

## Appendix: Directory.Build.props

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
```

> **Junior Dev Note:** `TreatWarningsAsErrors` is on deliberately. When you see a build warning, fix it — don't suppress it without team approval. This includes LMP001–LMP007 diagnostics from the analyzers and LMP100–LMP104 diagnostics from the MSBuild validation targets.

---

## Appendix: LMP.Tasks Project

The MSBuild tasks project (Layer 2 of the Three-Layer Build Architecture). Ships inside the LMP NuGet package in the `tools/` folder and is invoked automatically during `dotnet build`.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>LMP.Tasks</RootNamespace>
    <!-- MSBuild tasks must NOT be in lib/ — they go in tools/ -->
    <IsPackable>false</IsPackable>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Build.Utilities.Core" Version="17.*"
                      PrivateAssets="all" ExcludeAssets="runtime" />
    <PackageReference Include="Microsoft.Build.Framework" Version="17.*"
                      PrivateAssets="all" ExcludeAssets="runtime" />
    <ProjectReference Include="..\LMP.Abstractions\LMP.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

**Key references:**
- For task implementations: see `docs/02-specs/msbuild-targets.md`
- For NuGet packaging: the LMP.Runtime.csproj packs this assembly into `tools/net10.0/`

---

## Appendix: Post-MVP — LMP.Sdk (Custom MSBuild SDK)

In Phase 3 (post-MVP), the multiple `<PackageReference>` entries consolidate into a single SDK reference:

```xml
<!-- Before (MVP — explicit references) -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="LMP.Abstractions" Version="1.*" />
    <PackageReference Include="LMP.Runtime" Version="1.*" />
    <!-- Source generator + MSBuild targets come via LMP.Runtime package -->
  </ItemGroup>
</Project>

<!-- After (Phase 3 — LMP.Sdk) -->
<Project Sdk="LMP.Sdk/1.0.0">
  <!-- Everything is configured automatically:
       - Source generator (analyzers/dotnet/cs/)
       - MSBuild targets (Sdk.targets)
       - Runtime + Abstractions libraries (lib/net10.0/)
       - EnableConfigurationBindingGenerator=true
       - AOT compatibility flags -->
</Project>
```

**LMP.Sdk NuGet package structure:**

```
LMP.Sdk.1.0.0.nupkg
├── Sdk/
│   ├── Sdk.props          # Imports Microsoft.NET.Sdk, sets LMP defaults
│   └── Sdk.targets        # Imports Microsoft.NET.Sdk targets, adds LMP targets
├── lib/net10.0/
│   ├── LMP.Abstractions.dll
│   └── LMP.Runtime.dll
├── analyzers/dotnet/cs/
│   └── LMP.SourceGenerators.dll
├── buildTransitive/
│   ├── LMP.Sdk.props
│   └── LMP.Sdk.targets
└── tools/net10.0/
    └── LMP.Tasks.dll
```

**Sdk.props:**
```xml
<Project>
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
  <PropertyGroup>
    <EnableConfigurationBindingGenerator
      Condition="'$(EnableConfigurationBindingGenerator)' == ''">true</EnableConfigurationBindingGenerator>
    <LmpEmitIr Condition="'$(LmpEmitIr)' == ''">true</LmpEmitIr>
    <LmpValidateGraph Condition="'$(LmpValidateGraph)' == ''">true</LmpValidateGraph>
  </PropertyGroup>
</Project>
```

**Sdk.targets:**
```xml
<Project>
  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
  <!-- LMP build targets imported here -->
  <Import Project="$(MSBuildThisFileDirectory)..\buildTransitive\LMP.Sdk.targets" />
</Project>
```

**Consumer project with LMP.Sdk:**
```xml
<Project Sdk="LMP.Sdk/1.0.0">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
</Project>
```

Pin the SDK version globally in `global.json`:
```json
{
  "msbuild-sdks": {
    "LMP.Sdk": "1.0.0"
  }
}
```

Source: [Reference an MSBuild Project SDK](https://learn.microsoft.com/visualstudio/msbuild/how-to-use-project-sdk)
Source: [Creating your own MSBuild SDK](https://codingwithcalvin.net/creating-your-own-msbuild-sdk-it-s-easier-than-you-think/)
Source: [Microsoft MSBuild SDKs](https://github.com/microsoft/MSBuildSdks)
