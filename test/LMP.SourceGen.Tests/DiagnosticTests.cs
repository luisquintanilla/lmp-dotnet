using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace LMP.SourceGen.Tests;

/// <summary>
/// Tests for LMP001 (Missing property description) and LMP002 (Non-serializable output type) diagnostics.
/// </summary>
public class DiagnosticTests
{
    #region LMP001 — Missing Property Description

    [Fact]
    public void LMP001_Fires_OnPropertyWithoutDescription()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Classify tickets")]
            public partial record ClassifyTicket
            {
                [Description("Category: billing, technical, account")]
                public required string Category { get; init; }

                public required int Urgency { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp001 = diagnostics.Where(d => d.Id == "LMP001").ToArray();
        Assert.Single(lmp001);
        Assert.Equal(DiagnosticSeverity.Warning, lmp001[0].Severity);
        Assert.Contains("Urgency", lmp001[0].GetMessage());
        Assert.Contains("ClassifyTicket", lmp001[0].GetMessage());
    }

    [Fact]
    public void LMP001_DoesNotFire_WhenAllPropertiesHaveDescription()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Classify tickets")]
            public partial record ClassifyTicket
            {
                [Description("Category: billing, technical, account")]
                public required string Category { get; init; }

                [Description("Urgency from 1 to 5")]
                public required int Urgency { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "LMP001"));
    }

    [Fact]
    public void LMP001_Fires_OnMultiplePropertiesWithoutDescription()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Extract entities")]
            public partial record Entities
            {
                public required string Name { get; init; }
                public required string Location { get; init; }

                [Description("The type of entity")]
                public required string EntityType { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp001 = diagnostics.Where(d => d.Id == "LMP001").ToArray();
        Assert.Equal(2, lmp001.Length);
        Assert.Contains(lmp001, d => d.GetMessage().Contains("Name"));
        Assert.Contains(lmp001, d => d.GetMessage().Contains("Location"));
    }

    [Fact]
    public void LMP001_HasValidLocation()
    {
        var source = """
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                public required string MissingDesc { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp001 = diagnostics.Where(d => d.Id == "LMP001").ToArray();
        Assert.Single(lmp001);
        Assert.NotEqual(Location.None, lmp001[0].Location);
        Assert.True(lmp001[0].Location.SourceSpan.Length > 0,
            "Diagnostic should point at the property");
    }

    [Fact]
    public void LMP001_StillGeneratesCode()
    {
        // LMP001 is a Warning — code generation should still proceed
        var source = """
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                public required string Value { get; init; }
            }
            """;

        var compilation = CreateCompilation(source);
        var generator = new LmpSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out var diagnostics);

        // LMP001 is reported
        Assert.Single(diagnostics.Where(d => d.Id == "LMP001"));

        // JsonContext is still generated despite the warning
        var runResult = driver.GetRunResult();
        Assert.Single(runResult.GeneratedTrees);
        var generatedSource = runResult.GeneratedTrees[0].GetText().ToString();
        Assert.Contains("TestOutputJsonContext", generatedSource);
    }

    [Fact]
    public void LMP001_DoesNotFire_OnNonPartialRecord()
    {
        // LMP003 should fire instead; LMP001 should not fire for non-partial records
        var source = """
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public record NotPartial
            {
                public required string Value { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "LMP001"));
        Assert.NotEmpty(diagnostics.Where(d => d.Id == "LMP003"));
    }

    #endregion

    #region LMP002 — Non-Serializable Output Type

    [Fact]
    public void LMP002_Fires_OnActionProperty()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Extract entities")]
            public partial record Entities
            {
                [Description("Matched entities")]
                public required Action<string> Callback { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp002 = diagnostics.Where(d => d.Id == "LMP002").ToArray();
        Assert.Single(lmp002);
        Assert.Equal(DiagnosticSeverity.Error, lmp002[0].Severity);
        Assert.Contains("Callback", lmp002[0].GetMessage());
        Assert.Contains("Entities", lmp002[0].GetMessage());
    }

    [Fact]
    public void LMP002_Fires_OnFuncProperty()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("A func")]
                public required Func<string, int> Transform { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp002 = diagnostics.Where(d => d.Id == "LMP002").ToArray();
        Assert.Single(lmp002);
        Assert.Contains("Transform", lmp002[0].GetMessage());
    }

    [Fact]
    public void LMP002_Fires_OnDelegateProperty()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            public delegate void MyDelegate(string s);

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("A delegate")]
                public required MyDelegate Handler { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp002 = diagnostics.Where(d => d.Id == "LMP002").ToArray();
        Assert.Single(lmp002);
        Assert.Contains("Handler", lmp002[0].GetMessage());
    }

    [Fact]
    public void LMP002_Fires_OnIntPtrProperty()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("A pointer")]
                public required IntPtr Pointer { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Single(diagnostics.Where(d => d.Id == "LMP002"));
    }

    [Fact]
    public void LMP002_Fires_OnMultipleNonSerializableProperties()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("A callback")]
                public required Action Callback { get; init; }

                [Description("A transform")]
                public required Func<int> Transform { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp002 = diagnostics.Where(d => d.Id == "LMP002").ToArray();
        Assert.Equal(2, lmp002.Length);
        Assert.Contains(lmp002, d => d.GetMessage().Contains("Callback"));
        Assert.Contains(lmp002, d => d.GetMessage().Contains("Transform"));
    }

    [Fact]
    public void LMP002_HasValidLocation()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("Bad")]
                public required Action BadProp { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp002 = diagnostics.Where(d => d.Id == "LMP002").ToArray();
        Assert.Single(lmp002);
        Assert.NotEqual(Location.None, lmp002[0].Location);
        Assert.True(lmp002[0].Location.SourceSpan.Length > 0,
            "Diagnostic should point at the property");
    }

    [Fact]
    public void LMP002_SkipsJsonContextGeneration()
    {
        // LMP002 is an Error — code generation should be skipped for the entire type
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("Good")]
                public required string GoodProp { get; init; }

                [Description("Bad")]
                public required Action BadProp { get; init; }
            }
            """;

        var compilation = CreateCompilation(source);
        var generator = new LmpSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out var diagnostics);

        // LMP002 fires
        Assert.Single(diagnostics.Where(d => d.Id == "LMP002"));

        // No code generation (JsonContext or otherwise)
        var runResult = driver.GetRunResult();
        Assert.Empty(runResult.GeneratedTrees);
    }

    [Fact]
    public void LMP002_DoesNotFire_OnSerializableTypes()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("A string")]
                public required string Text { get; init; }

                [Description("A number")]
                public required int Count { get; init; }

                [Description("A bool")]
                public required bool Flag { get; init; }

                [Description("A decimal")]
                public required decimal Amount { get; init; }

                [Description("A double")]
                public required double Ratio { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "LMP002"));
    }

    [Fact]
    public void LMP002_DoesNotFire_OnNullableValueTypes()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("Optional count")]
                public int? Count { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "LMP002"));
    }

    [Fact]
    public void LMP002_DoesNotFire_OnArrayOfSerializableType()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("Tags")]
                public required string[] Tags { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "LMP002"));
    }

    [Fact]
    public void LMP002_DoesNotFire_OnNonPartialRecord()
    {
        // LMP003 fires instead; LMP002 should not fire for non-partial records
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public record NotPartial
            {
                [Description("Bad")]
                public required Action BadProp { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "LMP002"));
        Assert.NotEmpty(diagnostics.Where(d => d.Id == "LMP003"));
    }

    #endregion

    #region LMP001 + LMP002 Combined

    [Fact]
    public void LMP001_And_LMP002_CanFireOnSameType()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                public required string MissingDesc { get; init; }

                [Description("Bad type")]
                public required Action BadProp { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Single(diagnostics.Where(d => d.Id == "LMP001"));
        Assert.Single(diagnostics.Where(d => d.Id == "LMP002"));
    }

    [Fact]
    public void LMP001_And_LMP002_CanFireOnSameProperty()
    {
        // A property that is both missing description and non-serializable
        var source = """
            using System;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                public required Action BadPropNoDesc { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        // Both LMP001 (no [Description]) and LMP002 (non-serializable) fire
        Assert.Single(diagnostics.Where(d => d.Id == "LMP001"));
        Assert.Single(diagnostics.Where(d => d.Id == "LMP002"));
    }

    [Fact]
    public void NoDiagnostics_WhenAllPropertiesAreValid()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Classify tickets")]
            public partial record ClassifyTicket
            {
                [Description("Category: billing, technical, account")]
                public required string Category { get; init; }

                [Description("Urgency from 1 to 5")]
                public required int Urgency { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MixedTypes_LMP002_OnlyAffectsTypeWithError()
    {
        // Type with LMP002 error should have no code gen;
        // Valid type should still get code gen
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Good type")]
            public partial record GoodOutput
            {
                [Description("A value")]
                public required string Value { get; init; }
            }

            [LmpSignature("Bad type")]
            public partial record BadOutput
            {
                [Description("Bad")]
                public required Action Callback { get; init; }
            }
            """;

        var compilation = CreateCompilation(source);
        var generator = new LmpSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out var diagnostics);

        // Only LMP002 for BadOutput
        var lmp002 = diagnostics.Where(d => d.Id == "LMP002").ToArray();
        Assert.Single(lmp002);
        Assert.Contains("BadOutput", lmp002[0].GetMessage());

        // Only GoodOutput gets JsonContext
        var runResult = driver.GetRunResult();
        Assert.Single(runResult.GeneratedTrees);
        var generated = runResult.GeneratedTrees[0].GetText().ToString();
        Assert.Contains("GoodOutputJsonContext", generated);
        Assert.DoesNotContain("BadOutputJsonContext", generated);
    }

    #endregion

    #region SerializabilityChecker coverage via diagnostics

    [Fact]
    public void LMP002_Fires_OnStreamProperty()
    {
        var source = """
            using System.ComponentModel;
            using System.IO;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("A stream")]
                public required Stream Data { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Single(diagnostics.Where(d => d.Id == "LMP002"));
    }

    [Fact]
    public void LMP002_Fires_OnTaskProperty()
    {
        var source = """
            using System.ComponentModel;
            using System.Threading.Tasks;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("A task")]
                public required Task Work { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Single(diagnostics.Where(d => d.Id == "LMP002"));
    }

    [Fact]
    public void LMP002_Fires_OnExpressionProperty()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using System.Linq.Expressions;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("An expression")]
                public required Expression<Func<int>> Expr { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Single(diagnostics.Where(d => d.Id == "LMP002"));
    }

    [Fact]
    public void LMP002_Fires_OnCancellationTokenProperty()
    {
        var source = """
            using System.ComponentModel;
            using System.Threading;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record TestOutput
            {
                [Description("A token")]
                public required CancellationToken Token { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Single(diagnostics.Where(d => d.Id == "LMP002"));
    }

    #endregion

    #region Helpers

    private static ImmutableArray<Diagnostic> RunGeneratorDiagnostics(string source)
    {
        var compilation = CreateCompilation(source);
        var generator = new LmpSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out var diagnostics);
        return diagnostics;
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var syntaxTrees = string.IsNullOrWhiteSpace(source)
            ? System.Array.Empty<SyntaxTree>()
            : new[] { CSharpSyntaxTree.ParseText(source) };

        return CSharpCompilation.Create(
            "TestAssembly",
            syntaxTrees: syntaxTrees,
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(LmpSignatureAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.ComponentModel.DescriptionAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonSerializer).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Text.Json.Serialization.JsonSerializerContext).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
                .. GetNetCoreReferences(),
            ],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static MetadataReference[] GetNetCoreReferences()
    {
        var runtimeDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return
        [
            MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Collections.dll")),
        ];
    }

    #endregion
}
