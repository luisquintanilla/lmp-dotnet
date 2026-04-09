using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace LMP.SourceGen.Tests;

/// <summary>
/// Tests for Phase 2.2 — Output Type Model Extraction and LMP003 diagnostics.
/// </summary>
public class ModelExtractionTests
{
    [Fact]
    public void ValidPartialRecord_ProducesNoDiagnostics()
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

        var (diagnostics, _) = RunGenerator(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void NonPartialRecord_ReportsLMP003()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Summarize text")]
            public record Summary
            {
                [Description("One-line summary")]
                public required string Text { get; init; }
            }
            """;

        var (diagnostics, _) = RunGenerator(source);

        var lmp003 = Assert.Single(diagnostics);
        Assert.Equal("LMP003", lmp003.Id);
        Assert.Equal(DiagnosticSeverity.Error, lmp003.Severity);
        Assert.Contains("Summary", lmp003.GetMessage());
        Assert.Contains("a non-partial record", lmp003.GetMessage());
    }

    [Fact]
    public void Class_ReportsLMP003()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Summarize text")]
            public partial class Summary2
            {
                [Description("One-line summary")]
                public required string Text { get; init; }
            }
            """;

        var (diagnostics, _) = RunGenerator(source);

        var lmp003 = Assert.Single(diagnostics);
        Assert.Equal("LMP003", lmp003.Id);
        Assert.Equal(DiagnosticSeverity.Error, lmp003.Severity);
        Assert.Contains("Summary2", lmp003.GetMessage());
        Assert.Contains("a class", lmp003.GetMessage());
    }

    [Fact]
    public void NonPartialClass_ReportsLMP003_AsClass()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Summarize")]
            public class NotARecord
            {
                [Description("Text")]
                public required string Text { get; init; }
            }
            """;

        var (diagnostics, _) = RunGenerator(source);

        var lmp003 = Assert.Single(diagnostics);
        Assert.Equal("LMP003", lmp003.Id);
        Assert.Contains("a class", lmp003.GetMessage());
    }

    [Fact]
    public void LMP003_HasCorrectLocation()
    {
        var source = """
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public record NonPartial
            {
                public required string Value { get; init; }
            }
            """;

        var (diagnostics, _) = RunGenerator(source);

        var lmp003 = Assert.Single(diagnostics);
        // Location is reconstructed from file path + spans, so check span is set
        var location = lmp003.Location;
        Assert.NotEqual(default, location.SourceSpan);
    }

    [Fact]
    public void ValidPartialRecord_DoesNotGenerateCode()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Classify tickets")]
            public partial record ClassifyTicket
            {
                [Description("Category")]
                public required string Category { get; init; }
            }
            """;

        var (diagnostics, runResult) = RunGenerator(source);

        Assert.Empty(diagnostics);
        // JsonContext is emitted for valid partial records
        Assert.Single(runResult.GeneratedTrees);
        var generatedSource = runResult.GeneratedTrees[0].GetText().ToString();
        Assert.Contains("ClassifyTicketJsonContext", generatedSource);
    }

    [Fact]
    public void InvalidType_SkipsCodeGeneration()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public record NonPartial
            {
                [Description("Val")]
                public required string Value { get; init; }
            }
            """;

        var (diagnostics, runResult) = RunGenerator(source);

        // LMP003 is reported
        Assert.Single(diagnostics);
        // No code is generated for invalid types
        Assert.Empty(runResult.GeneratedTrees);
    }

    [Fact]
    public void MultipleTypes_MixedValidity()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Valid type")]
            public partial record ValidOutput
            {
                [Description("A field")]
                public required string Field1 { get; init; }
            }

            [LmpSignature("Invalid type")]
            public class InvalidOutput
            {
                [Description("A field")]
                public required string Field2 { get; init; }
            }
            """;

        var (diagnostics, runResult) = RunGenerator(source);

        // Only one diagnostic (for the class)
        var lmp003 = Assert.Single(diagnostics);
        Assert.Contains("InvalidOutput", lmp003.GetMessage());

        // JsonContext generated for the valid partial record only
        Assert.Single(runResult.GeneratedTrees);
        var generatedSource = runResult.GeneratedTrees[0].GetText().ToString();
        Assert.Contains("ValidOutputJsonContext", generatedSource);
    }

    [Fact]
    public void EmptyCompilation_NoDiagnostics()
    {
        var (diagnostics, runResult) = RunGenerator("");

        Assert.Empty(diagnostics);
        Assert.Empty(runResult.GeneratedTrees);
    }

    [Fact]
    public void TypeWithoutLmpSignature_Ignored()
    {
        var source = """
            using System.ComponentModel;

            namespace TestApp;

            public partial record RegularRecord
            {
                [Description("Normal field")]
                public required string Field { get; init; }
            }
            """;

        var (diagnostics, _) = RunGenerator(source);

        Assert.Empty(diagnostics);
    }

    private static (Diagnostic[] Diagnostics, GeneratorDriverRunResult RunResult) RunGenerator(string source)
    {
        var compilation = CreateCompilation(source);

        var generator = new LmpSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out var diagnostics);

        return (diagnostics.ToArray(), driver.GetRunResult());
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var syntaxTrees = string.IsNullOrWhiteSpace(source)
            ? []
            : new[] { CSharpSyntaxTree.ParseText(source) };

        return CSharpCompilation.Create(
            "TestAssembly",
            syntaxTrees: syntaxTrees,
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(LmpSignatureAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.ComponentModel.DescriptionAttribute).Assembly.Location),
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
        ];
    }
}
