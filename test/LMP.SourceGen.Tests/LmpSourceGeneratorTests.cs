using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace LMP.SourceGen.Tests;

public class LmpSourceGeneratorTests
{
    [Fact]
    public void Generator_RunsOnEmptyCompilation_NoErrors()
    {
        var compilation = CreateCompilation("");

        var generator = new LmpSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out var diagnostics);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Generator_RunsOnLmpSignatureType_NoErrors()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            public record TicketInput(
                [Description("The raw ticket text")] string TicketText);

            [LmpSignature("Classify the ticket severity.")]
            public partial record SimpleTicket
            {
                [Description("Low, Medium, High, Critical")]
                public required string Severity { get; init; }
            }
            """;

        var compilation = CreateCompilation(source);

        var generator = new LmpSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out var diagnostics);

        // No generator diagnostics
        Assert.Empty(diagnostics);

        // No compilation errors (warnings are okay for now since generator is empty)
        var errors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }

    [Fact]
    public void Generator_ProducesNoOutput_WhenEmpty()
    {
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
        driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out _);

        var runResult = driver.GetRunResult();
        // Empty generator produces no generated trees
        Assert.Empty(runResult.GeneratedTrees);
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
