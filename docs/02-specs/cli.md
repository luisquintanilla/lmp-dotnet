# CLI Specification

> **Source of truth**: `spec.org` §14 (CLI), §11 (Artifact Model), §12 (Public API Shape), §15 (Repository Layout).
> Downstream docs must not contradict `spec.org`.

---

## 1. CLI Overview

| Property | Value |
|---|---|
| Tool name | `dotnet lmp` |
| Project | `src/LMP.Cli` |
| Package ID | `LMP.Cli` |
| Install (global) | `dotnet tool install -g LMP.Cli` |
| Install (local) | `dotnet tool install LMP.Cli` (with tool-manifest) |
| Framework | `net10.0` or later |
| Command-line parser | `System.CommandLine` |
| Hosting | `Microsoft.Extensions.Hosting` |

The CLI is a **thin orchestration shell**. All domain logic lives in `LMP.Compiler`, `LMP.Runtime`, and `LMP.Evaluation`. The CLI's job is to wire DI, parse arguments, invoke library APIs, and format output.

### Design Principles

1. **Thin wrapper** — the CLI must not contain compile, evaluation, or runtime logic.
2. **Same infrastructure** — the CLI uses the same `IServiceCollection` / `ILogger` / `IConfiguration` stack as library consumers.
3. **Graceful cancellation** — every long-running command respects `CancellationToken` propagated from Ctrl+C via `IHostApplicationLifetime`.
4. **Structured exit codes** — deterministic, documented exit codes for every outcome.

---

## 2. Command Reference

### Exit Code Table

| Code | Meaning |
|------|---------|
| `0` | Success |
| `1` | Unhandled / unknown error |
| `2` | Invalid arguments or missing required option |
| `3` | Project or program not found |
| `4` | Compilation failed (no valid candidate) |
| `5` | Evaluation failed |
| `6` | Artifact load / validation error |
| `7` | Input file parse error |

---

### 2.1 `dotnet lmp compile`

Compiles an LM program by running the optimization loop and emitting a deployable artifact.

```text
dotnet lmp compile
    --project <path>          # Required. Path to the .csproj containing the program.
    --program <name>          # Required. [LmpProgram] name, e.g. "support-triage".
    --train <file>            # Required. Path to training JSONL file.
    --validate <file>         # Required. Path to validation JSONL file.
    --output <dir>            # Optional. Directory for artifact + report. Default: ./lmp-output
    --max-trials <int>        # Optional. Maximum optimization trials. Default: 20
    --optimizer <name>        # Optional. Optimizer backend name. Default: "random"
    --json                    # Optional. Emit machine-readable JSON output instead of human-readable text.
```

#### Step-by-step behavior

1. **Build the project.** Run `dotnet build` on `--project`. Exit `3` if build fails.
2. **Discover the program.** Load the built assembly. Scan for a type decorated with `[LmpProgram("<name>")]` matching `--program`. Exit `3` if not found.
3. **Construct DI container.** Create a `HostApplicationBuilder`, register `IChatClient`, logging, OpenTelemetry, and LMP services via `services.AddLmpPrograms()`.
4. **Build CompileSpec.** Load training and validation JSONL files. Wire the optimizer backend resolved from `--optimizer`. Set `--max-trials`.
5. **Run compilation.** Call `IProgramCompiler.CompileAsync(compileSpec, ct)`. The compiler executes trials, enforces constraints, selects the best valid variant, and returns a `CompileReport`.
6. **Write artifact.** Serialize the compiled artifact to `<output>/<program>-artifact.json`.
7. **Write report.** Serialize the compile report to `<output>/<program>-report.json`.
8. **Print summary.** Display trial count, best score, constraint pass/fail, and artifact path.

#### Example

```terminal
$ dotnet lmp compile \
    --project src/LMP.Samples.SupportTriage \
    --program support-triage \
    --train data/support-triage-train.jsonl \
    --validate data/support-triage-val.jsonl \
    --output ./artifacts \
    --max-trials 30

LMP Compile — support-triage
════════════════════════════════════════
Optimizer      : random
Max trials     : 30
Training set   : 120 examples
Validation set : 40 examples

Trial  1/30  score=0.621  constraints=FAIL  (policy_pass_rate=0.85)
Trial  2/30  score=0.734  constraints=PASS
Trial  3/30  score=0.710  constraints=PASS
...
Trial 30/30  score=0.759  constraints=PASS

Best valid trial : #17
Best score       : 0.812
Constraints      : ALL PASS
  policy_pass_rate = 1.000  (>= 1.0 ✓)
  p95_latency_ms   = 1842   (<= 2500 ✓)
  avg_cost_usd     = 0.021  (<= 0.03 ✓)

Artifact written : ./artifacts/support-triage-artifact.json
Report written   : ./artifacts/support-triage-report.json
```

---

### 2.2 `dotnet lmp run`

Executes an LM program on a single input.

```text
dotnet lmp run
    --project <path>          # Required. Path to .csproj containing the program.
    --program <name>          # Required. [LmpProgram] name.
    --input <file>            # Required. Path to a JSON file with the input object.
    --artifact <file>         # Optional. Path to a compiled artifact. When omitted, runs with authored defaults.
    --json                    # Optional. Emit raw JSON output.
```

#### Step-by-step behavior

1. **Build the project.** Run `dotnet build` on `--project`. Exit `3` on failure.
2. **Discover the program.** Load assembly and locate the `[LmpProgram]` type by name. Exit `3` if not found.
3. **Load input.** Deserialize `--input` JSON into the program's `TIn` type using `System.Text.Json`. Exit `7` on parse failure.
4. **Load artifact (if provided).** Deserialize and validate the artifact via `ICompiledArtifactLoader`. Exit `6` if the artifact is invalid or incompatible.
5. **Construct DI container.** Same as `compile`. If an artifact is loaded, apply its selected parameters (model, temperature, few-shot examples, etc.) to override program defaults.
6. **Execute.** Call `program.RunAsync(input, ct)`.
7. **Print result.** Serialize `TOut` to formatted JSON.

#### Example — without artifact (authored defaults)

```terminal
$ dotnet lmp run \
    --project src/LMP.Samples.SupportTriage \
    --program support-triage \
    --input data/single-ticket.json

{
  "severity": "High",
  "routeToTeam": "Identity Platform",
  "draftReply": "We are investigating the SSO login failures...",
  "escalate": true,
  "groundednessScore": 0.94,
  "policyPassed": true
}
```

#### Example — with compiled artifact

```terminal
$ dotnet lmp run \
    --project src/LMP.Samples.SupportTriage \
    --program support-triage \
    --input data/single-ticket.json \
    --artifact ./artifacts/support-triage-artifact.json

Using artifact: support-triage v3 (compiled 2025-01-15T10:32:00Z)
{
  "severity": "Critical",
  "routeToTeam": "Identity Platform",
  "draftReply": "We have identified the root cause of the SSO failures...",
  "escalate": true,
  "groundednessScore": 0.97,
  "policyPassed": true
}
```

---

### 2.3 `dotnet lmp eval`

Runs a program against a labeled dataset and reports aggregate metrics.

```text
dotnet lmp eval
    --project <path>          # Required. Path to .csproj.
    --program <name>          # Required. [LmpProgram] name.
    --dataset <file>          # Required. Path to evaluation JSONL file.
    --artifact <file>         # Optional. Artifact to apply before evaluation.
    --json                    # Optional. Emit machine-readable JSON output.
```

#### Step-by-step behavior

1. **Build the project.** Run `dotnet build`. Exit `3` on failure.
2. **Discover the program.** Locate by `[LmpProgram]` name. Exit `3` if not found.
3. **Load artifact (if provided).** Validate and apply parameters. Exit `6` on error.
4. **Load dataset.** Parse JSONL. Each line must have inputs and expected outputs. Exit `7` on parse error.
5. **Construct DI container.** Register all services, apply artifact overrides if present.
6. **Run evaluation loop.** For each example, execute the program and evaluate outputs against expected values using the program's metric and evaluator definitions.
7. **Aggregate metrics.** Compute averages, pass rates, and per-constraint results.
8. **Print report.** Display a summary table. Exit `5` if the evaluation harness itself fails (not if metrics are low).

#### Example

```terminal
$ dotnet lmp eval \
    --project src/LMP.Samples.SupportTriage \
    --program support-triage \
    --dataset data/support-triage-val.jsonl \
    --artifact ./artifacts/support-triage-artifact.json

LMP Eval — support-triage (artifact v3)
════════════════════════════════════════
Dataset          : 40 examples
Artifact         : support-triage v3

Metrics:
  routing_accuracy   : 0.875
  severity_accuracy  : 0.900
  groundedness       : 0.921
  policy_pass_rate   : 1.000

Weighted score     : 0.812
Constraints        : ALL PASS
```

---

### 2.4 `dotnet lmp inspect-artifact`

Displays the contents of a compiled artifact in human-readable form.

```text
dotnet lmp inspect-artifact
    --artifact <file>         # Required. Path to artifact JSON file.
    --json                    # Optional. Emit raw artifact JSON instead of formatted view.
```

#### Step-by-step behavior

1. **Load artifact.** Deserialize the artifact file. Exit `6` if the file is missing or malformed.
2. **Validate structure.** Confirm all required fields are present (program id, variant id, base program hash, selected parameters, validation metrics, provenance, approval state).
3. **Print formatted view.** Display a human-readable breakdown of the artifact contents.

#### Example

```terminal
$ dotnet lmp inspect-artifact \
    --artifact ./artifacts/support-triage-artifact.json

Artifact: support-triage
════════════════════════════════════════
Program ID       : support-triage
Variant ID       : variant-017
Compiled Version : 3
Base Program Hash: a4f8c91b
Approval State   : pending

Selected Parameters:
  triage.instruction_variant : 2
  triage.few_shot_count      : 4
  triage.model               : gpt-4.1-mini
  triage.temperature         : 0.30
  retrieve-kb.topK           : 7
  retrieve-policy.topK       : 3

Validation Metrics:
  routing_accuracy   : 0.875
  severity_accuracy  : 0.900
  groundedness       : 0.921
  policy_pass_rate   : 1.000
  weighted_score     : 0.812

Provenance:
  Compiled at      : 2025-01-15T10:32:00Z
  Optimizer        : random
  Total trials     : 30
  Valid trials     : 22
  Training examples: 120
```

---

### 2.5 `dotnet lmp pack` (Post-MVP)

Packages a compiled artifact as a NuGet package for distribution via Azure Artifacts, GitHub Packages, or private feeds.

```text
dotnet lmp pack
    --artifact <file>         # Required. Path to compiled artifact JSON.
    --id <package-id>         # Required. NuGet package ID (e.g., MyCompany.Lmp.SupportTriage).
    --version <semver>        # Required. SemVer version for the package.
    --output <dir>            # Optional. Output directory for .nupkg. Default: ./nupkgs
    --project <path>          # Optional. Path to the project that defines the program types.
    --authors <names>         # Optional. Package authors.
    --description <text>      # Optional. Package description.
```

#### Step-by-step behavior

1. **Validate artifact.** Load and verify the compiled artifact (content hash check). Exit `6` if invalid.
2. **Build project.** If `--project` is specified, run `dotnet build` to get the compiled types assembly.
3. **Create NuGet structure.** Generate `.nuspec` with:
   - `lib/net10.0/` — project assembly (if `--project` specified)
   - `contentFiles/any/any/artifacts/` — compiled artifact JSON
   - `build/` and `buildTransitive/` — auto-registration `.targets` file
4. **Pack.** Invoke `dotnet pack` or NuGet SDK to create the `.nupkg` file.
5. **Report.** Print package path, size, and contents summary.

#### Example

```terminal
$ dotnet lmp pack \
    --artifact ./artifacts/support-triage-v1.0.0.json \
    --id MyCompany.Lmp.SupportTriage \
    --version 1.0.0 \
    --project ./src/SupportTriage

Packing LMP artifact...
  Artifact : support-triage-v1.0.0.json (12.4 KB)
  Package  : MyCompany.Lmp.SupportTriage.1.0.0.nupkg
  Contents :
    lib/net10.0/SupportTriage.dll
    contentFiles/any/any/artifacts/support-triage-v1.0.0.json
    buildTransitive/MyCompany.Lmp.SupportTriage.targets
  Output   : ./nupkgs/MyCompany.Lmp.SupportTriage.1.0.0.nupkg

Push to a feed:
  dotnet nuget push ./nupkgs/MyCompany.Lmp.SupportTriage.1.0.0.nupkg --source MyFeed
```

#### Auto-generated .targets file

The `.targets` file is generated automatically and ensures consuming projects get the artifact in their output:

```xml
<Project>
  <ItemGroup>
    <None Include="$(MSBuildThisFileDirectory)..\contentFiles\any\any\artifacts\*.json"
          Link="artifacts\%(Filename)%(Extension)"
          CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

---

## 3. Implementation Architecture

### 3.1 Entry Point

`System.CommandLine` parses arguments; `Microsoft.Extensions.Hosting` provides DI, logging, and config. `IHostApplicationLifetime` propagates Ctrl+C as cooperative `CancellationToken` cancellation.

### 3.2 Program Discovery

1. Run `dotnet build` on the target project.
2. Load the output assembly via a collectible `AssemblyLoadContext`.
3. Scan for types annotated with `[LmpProgram("name")]`.
4. Match `--program` against the attribute's name.

### 3.3 DI Wiring

`HostApplicationBuilder` registers `IChatClient`, `ILogger`, `IConfiguration`, `IProgramCompiler`, `ICompiledArtifactLoader`, and evaluation services from the LMP libraries.

### 3.4 Complete `Program.cs`

```csharp
using System.CommandLine;
using System.CommandLine.Invocation;
using LMP.Cli.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var rootCommand = new RootCommand("LMP — Language Model Program CLI");

// ── compile ──────────────────────────────────────────────────
var compileCmd = new Command("compile", "Compile an LM program via optimization");
compileCmd.AddOption(new Option<string>("--project") { IsRequired = true });
compileCmd.AddOption(new Option<string>("--program") { IsRequired = true });
compileCmd.AddOption(new Option<string>("--train") { IsRequired = true });
compileCmd.AddOption(new Option<string>("--validate") { IsRequired = true });
compileCmd.AddOption(new Option<string>("--output", () => "./lmp-output"));
compileCmd.AddOption(new Option<int>("--max-trials", () => 20));
compileCmd.AddOption(new Option<string>("--optimizer", () => "random"));
compileCmd.AddOption(new Option<bool>("--json", () => false));
compileCmd.SetHandler(async (InvocationContext ctx) =>
{
    var host = CreateHost(ctx);
    var handler = host.Services.GetRequiredService<CompileCommandHandler>();
    ctx.ExitCode = await handler.ExecuteAsync(
        project:   ctx.ParseResult.GetValueForOption<string>("--project")!,
        program:   ctx.ParseResult.GetValueForOption<string>("--program")!,
        train:     ctx.ParseResult.GetValueForOption<string>("--train")!,
        validate:  ctx.ParseResult.GetValueForOption<string>("--validate")!,
        output:    ctx.ParseResult.GetValueForOption<string>("--output")!,
        maxTrials: ctx.ParseResult.GetValueForOption<int>("--max-trials"),
        optimizer: ctx.ParseResult.GetValueForOption<string>("--optimizer")!,
        json:      ctx.ParseResult.GetValueForOption<bool>("--json"),
        ct:        ctx.GetCancellationToken());
});
rootCommand.AddCommand(compileCmd);

// ── run ──────────────────────────────────────────────────────
var runCmd = new Command("run", "Run an LM program on a single input");
runCmd.AddOption(new Option<string>("--project") { IsRequired = true });
runCmd.AddOption(new Option<string>("--program") { IsRequired = true });
runCmd.AddOption(new Option<string>("--input") { IsRequired = true });
runCmd.AddOption(new Option<string?>("--artifact"));
runCmd.AddOption(new Option<bool>("--json", () => false));
runCmd.SetHandler(async (InvocationContext ctx) =>
{
    var host = CreateHost(ctx);
    var handler = host.Services.GetRequiredService<RunCommandHandler>();
    ctx.ExitCode = await handler.ExecuteAsync(
        project:  ctx.ParseResult.GetValueForOption<string>("--project")!,
        program:  ctx.ParseResult.GetValueForOption<string>("--program")!,
        input:    ctx.ParseResult.GetValueForOption<string>("--input")!,
        artifact: ctx.ParseResult.GetValueForOption<string?>("--artifact"),
        json:     ctx.ParseResult.GetValueForOption<bool>("--json"),
        ct:       ctx.GetCancellationToken());
});
rootCommand.AddCommand(runCmd);

// ── eval ─────────────────────────────────────────────────────
var evalCmd = new Command("eval", "Evaluate an LM program against a dataset");
evalCmd.AddOption(new Option<string>("--project") { IsRequired = true });
evalCmd.AddOption(new Option<string>("--program") { IsRequired = true });
evalCmd.AddOption(new Option<string>("--dataset") { IsRequired = true });
evalCmd.AddOption(new Option<string?>("--artifact"));
evalCmd.AddOption(new Option<bool>("--json", () => false));
evalCmd.SetHandler(async (InvocationContext ctx) =>
{
    var host = CreateHost(ctx);
    var handler = host.Services.GetRequiredService<EvalCommandHandler>();
    ctx.ExitCode = await handler.ExecuteAsync(
        project:  ctx.ParseResult.GetValueForOption<string>("--project")!,
        program:  ctx.ParseResult.GetValueForOption<string>("--program")!,
        dataset:  ctx.ParseResult.GetValueForOption<string>("--dataset")!,
        artifact: ctx.ParseResult.GetValueForOption<string?>("--artifact"),
        json:     ctx.ParseResult.GetValueForOption<bool>("--json"),
        ct:       ctx.GetCancellationToken());
});
rootCommand.AddCommand(evalCmd);

// ── inspect-artifact ─────────────────────────────────────────
var inspectCmd = new Command("inspect-artifact", "Display compiled artifact contents");
inspectCmd.AddOption(new Option<string>("--artifact") { IsRequired = true });
inspectCmd.AddOption(new Option<bool>("--json", () => false));
inspectCmd.SetHandler(async (InvocationContext ctx) =>
{
    var host = CreateHost(ctx);
    var handler = host.Services.GetRequiredService<InspectArtifactCommandHandler>();
    ctx.ExitCode = await handler.ExecuteAsync(
        artifact: ctx.ParseResult.GetValueForOption<string>("--artifact")!,
        json:     ctx.ParseResult.GetValueForOption<bool>("--json"),
        ct:       ctx.GetCancellationToken());
});
rootCommand.AddCommand(inspectCmd);

return await rootCommand.InvokeAsync(args);

// ── Host factory ─────────────────────────────────────────────
static IHost CreateHost(InvocationContext _)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddSingleton<CompileCommandHandler>();
    builder.Services.AddSingleton<RunCommandHandler>();
    builder.Services.AddSingleton<EvalCommandHandler>();
    builder.Services.AddSingleton<InspectArtifactCommandHandler>();
    // LMP library services — compiler, runtime, artifact loader
    builder.Services.AddLmpCli();
    return builder.Build();
}
```

### 3.5 Command Handler Pattern

Each command handler is a plain class resolved from DI. It receives library services via constructor injection and returns an exit code from `ExecuteAsync`.

```csharp
namespace LMP.Cli.Commands;

using LMP.Compilation;
using Microsoft.Extensions.Logging;

public sealed class CompileCommandHandler(
    IProgramCompiler compiler,
    IProgramDiscovery discovery,
    ILogger<CompileCommandHandler> logger)
{
    public async Task<int> ExecuteAsync(
        string project, string program, string train, string validate,
        string output, int maxTrials, string optimizer, bool json,
        CancellationToken ct)
    {
        // 1. Build project
        var buildResult = await ProjectBuilder.BuildAsync(project, ct);
        if (!buildResult.Success) { logger.LogError("Build failed"); return 3; }

        // 2. Discover program
        var programType = discovery.FindProgram(buildResult.OutputAssembly, program);
        if (programType is null) { logger.LogError("Program '{Name}' not found", program); return 3; }

        // 3. Load datasets
        var trainSet = DatasetLoader.LoadJsonl(train);
        var valSet = DatasetLoader.LoadJsonl(validate);

        // 4. Build CompileSpec
        var spec = CompileSpecFactory.Create(programType, trainSet, valSet, maxTrials, optimizer);

        // 5. Compile
        var report = await compiler.CompileAsync(spec, ct);
        if (report.BestVariant is null) { logger.LogError("No valid candidate found"); return 4; }

        // 6. Write outputs
        Directory.CreateDirectory(output);
        await ArtifactWriter.WriteAsync(report.Artifact, Path.Combine(output, $"{program}-artifact.json"));
        await ReportWriter.WriteAsync(report, Path.Combine(output, $"{program}-report.json"));

        // 7. Print summary
        OutputFormatter.PrintCompileSummary(report, output, json);
        return 0;
    }
}
```

The `RunCommandHandler`, `EvalCommandHandler`, and `InspectArtifactCommandHandler` follow the same pattern: validate → discover → DI → invoke library → format → exit code.

---

## 4. NuGet Packaging

### 4.1 `.csproj` Setup (PackAsTool)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>lmp</ToolCommandName>
    <PackageId>LMP.Cli</PackageId>
    <Version>0.1.0</Version>
    <Description>CLI for the LMP Language Model Programming framework</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="System.CommandLine" Version="2.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.*" />
    <ProjectReference Include="..\LMP.Compiler\LMP.Compiler.csproj" />
    <ProjectReference Include="..\LMP.Runtime\LMP.Runtime.csproj" />
    <ProjectReference Include="..\LMP.Evaluation\LMP.Evaluation.csproj" />
  </ItemGroup>
</Project>
```

### 4.2 Installation

```bash
dotnet tool install -g LMP.Cli                  # global
dotnet new tool-manifest && dotnet tool install LMP.Cli  # local (per-repo)
dotnet tool restore                              # CI / fresh clone
```

### 4.3 Version Management

The `<Version>` property in the `.csproj` is the single source of truth. CI overrides with `dotnet pack -p:Version=$(BUILD_VERSION)`.

---

## 5. Error Handling and Output

### 5.1 Output Format

All commands emit **human-readable text** by default. Pass `--json` to get **machine-readable JSON** on stdout. Errors and progress always go to stderr.

### 5.2 JSON Output Schema (eval example)

```json
{
  "command": "eval",
  "program": "support-triage",
  "artifact": "support-triage v3",
  "datasetSize": 40,
  "metrics": {
    "routing_accuracy": 0.875,
    "severity_accuracy": 0.900,
    "groundedness": 0.921,
    "policy_pass_rate": 1.000
  },
  "weightedScore": 0.812,
  "constraintsPassed": true,
  "exitCode": 0
}
```

### 5.3 Compilation Progress

During `compile`, one line per trial is written to stderr:

```text
Trial  1/30  score=0.621  constraints=FAIL  (policy_pass_rate=0.85)
```

The compiler reports progress via `IProgress<TrialProgress>`.

### 5.4 Error Surfacing

Errors go to stderr with a category prefix that maps to the exit code table:

```text
ERROR [project]  Build failed for src/LMP.Samples.SupportTriage.
ERROR [discovery] No type with [LmpProgram("support-triage")] found.
ERROR [input]    Failed to parse data/single-ticket.json: missing 'ticketText'.
ERROR [artifact] Artifact hash mismatch: expected a4f8c91b, got b3e7d012.
ERROR [compile]  No candidate satisfied all hard constraints.
```

---

## 6. Complete Implementation

### 6.1 Service Registration Extension

```csharp
namespace LMP.Cli;

using LMP.Cli.Commands;
using LMP.Cli.Infrastructure;
using LMP.Compilation;
using LMP.Runtime;
using Microsoft.Extensions.DependencyInjection;

public static class CliServiceExtensions
{
    public static IServiceCollection AddLmpCli(this IServiceCollection services)
    {
        // Core LMP library services
        services.AddLmpRuntime();
        services.AddLmpCompiler();
        services.AddLmpEvaluation();

        // CLI infrastructure
        services.AddSingleton<IProgramDiscovery, ReflectionProgramDiscovery>();
        services.AddSingleton<OutputFormatter>();

        // Command handlers
        services.AddSingleton<CompileCommandHandler>();
        services.AddSingleton<RunCommandHandler>();
        services.AddSingleton<EvalCommandHandler>();
        services.AddSingleton<InspectArtifactCommandHandler>();

        return services;
    }
}
```

### 6.2 Program Discovery Service

```csharp
namespace LMP.Cli.Infrastructure;

using System.Reflection;
using System.Runtime.Loader;

public interface IProgramDiscovery
{
    Type? FindProgram(string assemblyPath, string programName);
}

public sealed class ReflectionProgramDiscovery : IProgramDiscovery
{
    public Type? FindProgram(string assemblyPath, string programName)
    {
        var context = new AssemblyLoadContext("LmpCliDiscovery", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            return assembly.GetTypes()
                .FirstOrDefault(t =>
                    t.GetCustomAttribute<LmpProgramAttribute>() is { } attr
                    && string.Equals(attr.Name, programName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            context.Unload();
        }
    }
}
```

### 6.3 Project Builder Utility

```csharp
namespace LMP.Cli.Infrastructure;

using System.Diagnostics;

public static class ProjectBuilder
{
    public sealed record BuildResult(bool Success, string OutputAssembly, string DiagnosticOutput);

    public static async Task<BuildResult> BuildAsync(string projectPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet", $"build \"{projectPath}\" -c Release --nologo -v q")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        // Resolve output DLL from project path
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var outputDll = Path.Combine(projectDir, "bin", "Release", "net10.0", $"{projectName}.dll");

        return new BuildResult(
            Success: process.ExitCode == 0,
            OutputAssembly: outputDll,
            DiagnosticOutput: stdout + stderr);
    }
}
```

### 6.4 File Layout

```
src/LMP.Cli/
├── Program.cs                              # Root command setup (§3.4)
├── LMP.Cli.csproj                          # PackAsTool config (§4.1)
├── CliServiceExtensions.cs                 # DI registration (§6.1)
├── Commands/
│   ├── CompileCommandHandler.cs            # §3.5
│   ├── RunCommandHandler.cs
│   ├── EvalCommandHandler.cs
│   └── InspectArtifactCommandHandler.cs
└── Infrastructure/
    ├── IProgramDiscovery.cs                # §6.2
    ├── ReflectionProgramDiscovery.cs       # §6.2
    ├── ProjectBuilder.cs                   # §6.3
    ├── DatasetLoader.cs                    # JSONL parser
    ├── ArtifactWriter.cs                   # Artifact serialization
    ├── ReportWriter.cs                     # Report serialization
    └── OutputFormatter.cs                  # Human / JSON output formatting
```
