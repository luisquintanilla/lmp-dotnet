# LMP MSBuild Targets Specification

> **Layer 2 of the Three-Layer Build Architecture.** These targets run automatically during `dotnet build` (after the C# compiler finishes) and during `dotnet publish`. They bridge the gap between source generators (Layer 1, inside Roslyn) and the CLI optimization tool (Layer 3, explicit invocation).

---

## 1. Why MSBuild Targets?

Source generators run **inside** the Roslyn compiler. They can emit C# code and report diagnostics, but they **cannot**:
- Access the filesystem to write non-C# files (IR JSON)
- Read the compiled assembly via reflection (only syntax/semantic models)
- Run external tools or processes
- Execute complex validation that requires the fully compiled type system

MSBuild targets run **after** the compiler finishes. They can do all of the above.

**Precedent:** EF Core uses the exact same pattern — source generators for design-time IntelliSense, MSBuild tasks for `OptimizeDbContext` code generation.
- Source: [EF Core MSBuild Targets](https://github.com/dotnet/efcore/blob/main/src/EFCore.Tasks/buildTransitive/Microsoft.EntityFrameworkCore.Tasks.targets)
- Source: [OptimizeDbContext Task](https://github.com/dotnet/efcore/blob/main/src/EFCore.Tasks/Tasks/OptimizeDbContext.cs)

---

## 2. Target Overview

| Target | Hook | Condition | Purpose |
|--------|------|-----------|---------|
| `LmpEmitIr` | `AfterCompile` | Always (can be disabled via `$(LmpEmitIr)`) | Emit IR JSON from compiled assembly |
| `LmpValidateGraph` | After `LmpEmitIr` | Always (can be disabled via `$(LmpValidateGraph)`) | Validate program graph completeness |
| `LmpEmbedArtifact` | `PrepareForPublish` | `$(LmpArtifactPath) != ''` | Copy compiled artifact to publish output |

---

## 3. Target Definitions

### 3.1 LMP.Runtime.props (imported first)

```xml
<Project>
  <!-- Default properties — consumers can override -->
  <PropertyGroup>
    <LmpEmitIr Condition="'$(LmpEmitIr)' == ''">true</LmpEmitIr>
    <LmpValidateGraph Condition="'$(LmpValidateGraph)' == ''">true</LmpValidateGraph>
    <LmpIrOutputDir Condition="'$(LmpIrOutputDir)' == ''">$(IntermediateOutputPath)lmp\</LmpIrOutputDir>
    <LmpArtifactPath Condition="'$(LmpArtifactPath)' == ''"></LmpArtifactPath>
    <!-- AOT-safe configuration binding -->
    <EnableConfigurationBindingGenerator Condition="'$(EnableConfigurationBindingGenerator)' == ''">true</EnableConfigurationBindingGenerator>
  </PropertyGroup>
</Project>
```

### 3.2 LMP.Runtime.targets (imported last)

```xml
<Project>
  <!-- Register the MSBuild task assembly -->
  <UsingTask
    TaskName="LMP.Tasks.EmitIrTask"
    AssemblyFile="$(MSBuildThisFileDirectory)..\tools\net10.0\LMP.Tasks.dll" />
  <UsingTask
    TaskName="LMP.Tasks.ValidateGraphTask"
    AssemblyFile="$(MSBuildThisFileDirectory)..\tools\net10.0\LMP.Tasks.dll" />

  <!-- ═══════════════════════════════════════════════════════ -->
  <!-- Target 1: Emit IR JSON from compiled assembly          -->
  <!-- ═══════════════════════════════════════════════════════ -->
  <Target
    Name="LmpEmitIr"
    AfterTargets="CoreCompile"
    Condition="'$(LmpEmitIr)' == 'true'"
    Inputs="@(IntermediateAssembly)"
    Outputs="$(LmpIrOutputDir)%(IntermediateAssembly.Filename).ir.json">

    <MakeDir Directories="$(LmpIrOutputDir)" />

    <EmitIrTask
      Assembly="%(IntermediateAssembly.FullPath)"
      OutputDirectory="$(LmpIrOutputDir)"
      ProjectName="$(MSBuildProjectName)">
      <Output TaskParameter="IrFiles" ItemName="LmpIrFile" />
    </EmitIrTask>

    <Message
      Importance="normal"
      Text="LMP: Emitted %(LmpIrFile.Identity)" />
  </Target>

  <!-- ═══════════════════════════════════════════════════════ -->
  <!-- Target 2: Validate program graph completeness          -->
  <!-- ═══════════════════════════════════════════════════════ -->
  <Target
    Name="LmpValidateGraph"
    AfterTargets="LmpEmitIr"
    Condition="'$(LmpValidateGraph)' == 'true'"
    Inputs="@(LmpIrFile)"
    Outputs="$(LmpIrOutputDir)validation-results.json">

    <ValidateGraphTask
      IrFiles="@(LmpIrFile)"
      OutputFile="$(LmpIrOutputDir)validation-results.json">
      <Output TaskParameter="Errors" ItemName="LmpGraphError" />
      <Output TaskParameter="Warnings" ItemName="LmpGraphWarning" />
    </ValidateGraphTask>

    <!-- Report warnings as MSBuild warnings -->
    <Warning
      Condition="'@(LmpGraphWarning)' != ''"
      Text="%(LmpGraphWarning.Identity)"
      Code="%(LmpGraphWarning.Code)" />

    <!-- Report errors as MSBuild errors (fail the build) -->
    <Error
      Condition="'@(LmpGraphError)' != ''"
      Text="%(LmpGraphError.Identity)"
      Code="%(LmpGraphError.Code)" />
  </Target>

  <!-- ═══════════════════════════════════════════════════════ -->
  <!-- Target 3: Embed compiled artifact during publish       -->
  <!-- ═══════════════════════════════════════════════════════ -->
  <Target
    Name="LmpEmbedArtifact"
    BeforeTargets="PrepareForPublish"
    Condition="'$(LmpArtifactPath)' != '' and Exists('$(LmpArtifactPath)')">

    <ItemGroup>
      <ContentWithTargetPath
        Include="$(LmpArtifactPath)"
        TargetPath="artifacts\%(Filename)%(Extension)"
        CopyToOutputDirectory="PreserveNewest"
        CopyToPublishDirectory="PreserveNewest" />
    </ItemGroup>

    <Message
      Importance="high"
      Text="LMP: Embedding artifact $(LmpArtifactPath) into publish output." />
  </Target>
</Project>
```

---

## 4. MSBuild Task Implementations

### 4.1 EmitIrTask

```csharp
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.Reflection;
using System.Text.Json;

namespace LMP.Tasks;

/// <summary>
/// MSBuild task that loads the compiled assembly, discovers all ProgramDescriptor
/// instances via reflection, and emits IR JSON files to the output directory.
/// </summary>
public sealed class EmitIrTask : Task
{
    [Required]
    public ITaskItem Assembly { get; set; } = null!;

    [Required]
    public string OutputDirectory { get; set; } = null!;

    public string? ProjectName { get; set; }

    [Output]
    public ITaskItem[] IrFiles { get; private set; } = [];

    public override bool Execute()
    {
        try
        {
            // Load the compiled assembly in a collectible context
            // to avoid locking the file for subsequent builds.
            var context = new MetadataLoadContext(
                new PathAssemblyResolver(GetAssemblyPaths()));
            using var _ = context;

            var assembly = context.LoadFromAssemblyPath(Assembly.ItemSpec);
            var programs = DiscoverProgramDescriptors(assembly);

            var outputFiles = new List<ITaskItem>();

            foreach (var (programName, ir) in programs)
            {
                var outputPath = Path.Combine(
                    OutputDirectory, $"{programName}.ir.json");

                File.WriteAllText(outputPath,
                    JsonSerializer.Serialize(ir, IrSerializerContext.Default.ProgramIr));

                outputFiles.Add(new TaskItem(outputPath));
                Log.LogMessage(MessageImportance.Normal,
                    $"LMP: Emitted IR for program '{programName}'");
            }

            IrFiles = outputFiles.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }

    private IEnumerable<(string Name, ProgramIr Ir)> DiscoverProgramDescriptors(
        Assembly assembly)
    {
        // Find all types with [LmpProgram] attribute
        foreach (var type in assembly.GetTypes())
        {
            var attr = type.CustomAttributes
                .FirstOrDefault(a => a.AttributeType.Name == "LmpProgramAttribute");
            if (attr is null) continue;

            // Read the source-generated static Descriptor property
            var descriptorProp = type.GetProperty("Descriptor",
                BindingFlags.Public | BindingFlags.Static);
            if (descriptorProp is null)
            {
                Log.LogWarning($"LMP: Program type '{type.FullName}' has " +
                    "[LmpProgram] but no static Descriptor property. " +
                    "Was the source generator run?");
                continue;
            }

            // Extract IR from descriptor metadata
            var programName = type.Name;
            var ir = ExtractIrFromDescriptor(type);
            yield return (programName, ir);
        }
    }

    private string[] GetAssemblyPaths()
    {
        // Include the target assembly directory + runtime assemblies
        var dir = Path.GetDirectoryName(Assembly.ItemSpec)!;
        return Directory.GetFiles(dir, "*.dll")
            .Concat(Directory.GetFiles(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll"))
            .ToArray();
    }

    private static ProgramIr ExtractIrFromDescriptor(Type programType)
    {
        // Extract step metadata, binding metadata, and graph structure
        // from the source-generated descriptor properties.
        // Implementation reads [LmpStep], [BindFrom], and graph topology
        // attributes emitted by the source generator.
        return new ProgramIr
        {
            ProgramName = programType.Name,
            // ... populated from reflection metadata
        };
    }
}
```

### 4.2 ValidateGraphTask

```csharp
namespace LMP.Tasks;

/// <summary>
/// MSBuild task that validates IR JSON files for structural completeness.
/// Reports unresolved bindings, dangling step references, and type mismatches
/// as MSBuild warnings and errors.
/// </summary>
public sealed class ValidateGraphTask : Task
{
    [Required]
    public ITaskItem[] IrFiles { get; set; } = [];

    public string? OutputFile { get; set; }

    [Output]
    public ITaskItem[] Errors { get; private set; } = [];

    [Output]
    public ITaskItem[] Warnings { get; private set; } = [];

    public override bool Execute()
    {
        var errors = new List<ITaskItem>();
        var warnings = new List<ITaskItem>();

        foreach (var irFile in IrFiles)
        {
            var ir = JsonSerializer.Deserialize<ProgramIr>(
                File.ReadAllText(irFile.ItemSpec),
                IrSerializerContext.Default.ProgramIr);

            if (ir is null)
            {
                errors.Add(CreateDiagnostic("LMP100",
                    $"Failed to parse IR file: {irFile.ItemSpec}"));
                continue;
            }

            // Validate all step bindings are resolved
            ValidateBindings(ir, errors, warnings);

            // Validate no dangling step references
            ValidateStepReferences(ir, errors, warnings);

            // Validate output types are JSON-serializable
            ValidateOutputTypes(ir, warnings);

            // Validate no cycles in the directed graph
            ValidateDag(ir, errors);
        }

        Errors = errors.ToArray();
        Warnings = warnings.ToArray();

        // Write validation results for CI/CD consumption
        if (OutputFile is not null)
        {
            var results = new ValidationResults
            {
                ErrorCount = errors.Count,
                WarningCount = warnings.Count,
                Programs = IrFiles.Select(f => f.ItemSpec).ToArray()
            };
            File.WriteAllText(OutputFile,
                JsonSerializer.Serialize(results));
        }

        return errors.Count == 0;
    }

    private void ValidateBindings(ProgramIr ir,
        List<ITaskItem> errors, List<ITaskItem> warnings)
    {
        foreach (var step in ir.Steps)
        {
            foreach (var binding in step.InputBindings)
            {
                if (!ir.Steps.Any(s => s.Name == binding.SourceStep))
                {
                    errors.Add(CreateDiagnostic("LMP101",
                        $"Step '{step.Name}' has binding from " +
                        $"non-existent step '{binding.SourceStep}' " +
                        $"in program '{ir.ProgramName}'."));
                }
            }
        }
    }

    private void ValidateStepReferences(ProgramIr ir,
        List<ITaskItem> errors, List<ITaskItem> warnings)
    {
        var stepNames = ir.Steps.Select(s => s.Name).ToHashSet();
        var duplicates = ir.Steps
            .GroupBy(s => s.Name)
            .Where(g => g.Count() > 1);

        foreach (var dup in duplicates)
        {
            errors.Add(CreateDiagnostic("LMP102",
                $"Duplicate step name '{dup.Key}' in program " +
                $"'{ir.ProgramName}'. Step names must be unique."));
        }
    }

    private void ValidateOutputTypes(ProgramIr ir,
        List<ITaskItem> warnings)
    {
        foreach (var step in ir.Steps.Where(s => s.Kind == "Predict"))
        {
            if (step.OutputTypeName is null)
            {
                warnings.Add(CreateDiagnostic("LMP103",
                    $"Predict step '{step.Name}' has no output type. " +
                    "Consider adding a typed output for safety."));
            }
        }
    }

    private void ValidateDag(ProgramIr ir, List<ITaskItem> errors)
    {
        // Topological sort — if cycle detected, report error
        var visited = new HashSet<string>();
        var inStack = new HashSet<string>();

        foreach (var step in ir.Steps)
        {
            if (HasCycle(step.Name, ir, visited, inStack))
            {
                errors.Add(CreateDiagnostic("LMP104",
                    $"Cycle detected in program '{ir.ProgramName}' " +
                    $"involving step '{step.Name}'. " +
                    "LMP programs must be directed acyclic graphs."));
                break;
            }
        }
    }

    private static bool HasCycle(string stepName, ProgramIr ir,
        HashSet<string> visited, HashSet<string> inStack)
    {
        if (inStack.Contains(stepName)) return true;
        if (visited.Contains(stepName)) return false;

        visited.Add(stepName);
        inStack.Add(stepName);

        var step = ir.Steps.FirstOrDefault(s => s.Name == stepName);
        if (step is not null)
        {
            foreach (var dep in step.InputBindings)
            {
                if (HasCycle(dep.SourceStep, ir, visited, inStack))
                    return true;
            }
        }

        inStack.Remove(stepName);
        return false;
    }

    private static TaskItem CreateDiagnostic(string code, string message)
    {
        var item = new TaskItem(message);
        item.SetMetadata("Code", code);
        return item;
    }
}
```

---

## 5. Diagnostic Codes (Build-Time)

| Code | Severity | Description |
|------|----------|-------------|
| LMP100 | Error | Failed to parse IR file |
| LMP101 | Error | Binding references non-existent step |
| LMP102 | Error | Duplicate step name in program |
| LMP103 | Warning | Predict step has no typed output |
| LMP104 | Error | Cycle detected in program graph |

These complement the source generator diagnostics (LMP001–LMP007) which run inside Roslyn.

---

## 6. Consumer Experience

### 6.1 Implicit (Zero Configuration)

When a project references the LMP NuGet package, the targets run automatically:

```
> dotnet build
  LMP: Emitted obj/lmp/SupportTriageProgram.ir.json
  LMP: Graph validation passed — 3 steps, 2 bindings, 0 errors.
  Build succeeded.
```

### 6.2 Disabling Targets

Projects can opt out via MSBuild properties:

```xml
<PropertyGroup>
  <!-- Disable IR emission (e.g., in test projects) -->
  <LmpEmitIr>false</LmpEmitIr>
  <!-- Disable graph validation -->
  <LmpValidateGraph>false</LmpValidateGraph>
</PropertyGroup>
```

### 6.3 Publishing with Artifacts

```xml
<PropertyGroup>
  <LmpArtifactPath>artifacts/support-triage-v1.0.0.json</LmpArtifactPath>
</PropertyGroup>
```

```
> dotnet publish
  LMP: Embedding artifact artifacts/support-triage-v1.0.0.json into publish output.
  Publish succeeded.
```

---

## 7. NuGet Package Layout

The MSBuild targets ship in the LMP NuGet package:

```
LMP.Runtime.1.0.0.nupkg
├── lib/net10.0/
│   └── LMP.Runtime.dll                     # Runtime assembly
├── analyzers/dotnet/cs/
│   └── LMP.SourceGenerators.dll            # Source generator (Layer 1)
├── buildTransitive/
│   ├── LMP.Runtime.props                   # Default properties
│   └── LMP.Runtime.targets                 # MSBuild targets (Layer 2)
├── tools/net10.0/
│   └── LMP.Tasks.dll                       # MSBuild task assembly
└── contentFiles/any/any/
    └── .lmp/
        └── README.md                       # Brief explainer of LMP build integration
```

The `buildTransitive/` folder ensures targets are imported by **all** projects in the dependency chain — not just the direct consumer. This is critical: if `MyApp` references `MyLibrary` which references `LMP.Runtime`, `MyApp` also gets the validation targets.

Source: [MSBuild .props and .targets in a package](https://learn.microsoft.com/nuget/concepts/msbuild-props-and-targets)

---

## 8. Incremental Build Support

Both targets use MSBuild's `Inputs`/`Outputs` attributes for incremental build support:

- **LmpEmitIr:** Input = compiled assembly. Output = IR JSON. Only re-runs if the assembly changes.
- **LmpValidateGraph:** Input = IR JSON. Output = validation results. Only re-runs if IR changes.

This means repeated `dotnet build` calls with no code changes skip both targets entirely — zero overhead.

---

## 9. Post-MVP: LMP.Sdk Custom MSBuild SDK

In Phase 3 (post-MVP), the targets, source generator, and runtime library consolidate into a custom MSBuild SDK:

```xml
<!-- Before (MVP) -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="LMP.Abstractions" Version="1.*" />
    <PackageReference Include="LMP.Runtime" Version="1.*" />
  </ItemGroup>
</Project>

<!-- After (Phase 3: LMP.Sdk) -->
<Project Sdk="LMP.Sdk/1.0.0">
  <!-- Everything is configured automatically -->
</Project>
```

The SDK package structure follows the standard MSBuild SDK convention:

```
LMP.Sdk.1.0.0.nupkg
├── Sdk/
│   ├── Sdk.props          # Imports Microsoft.NET.Sdk, sets LMP defaults
│   └── Sdk.targets        # Imports Microsoft.NET.Sdk targets, adds LMP targets
├── lib/net10.0/
├── analyzers/dotnet/cs/
├── buildTransitive/
└── tools/net10.0/
```

Source: [Reference an MSBuild Project SDK](https://learn.microsoft.com/visualstudio/msbuild/how-to-use-project-sdk)
Source: [Creating your own MSBuild SDK](https://codingwithcalvin.net/creating-your-own-msbuild-sdk-it-s-easier-than-you-think/)
Source: [Microsoft MSBuild SDKs](https://github.com/microsoft/MSBuildSdks)
