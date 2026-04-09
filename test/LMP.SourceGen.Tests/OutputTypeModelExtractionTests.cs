using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace LMP.SourceGen.Tests;

public class OutputTypeModelExtractionTests
{
    #region LMP003 — Non-Partial Record

    [Fact]
    public void LMP003_Fires_OnClass()
    {
        var source = """
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial class NotARecord
            {
                public required string Value { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp003 = diagnostics.Where(d => d.Id == "LMP003").ToArray();
        Assert.Single(lmp003);
        Assert.Equal(DiagnosticSeverity.Error, lmp003[0].Severity);
        Assert.Contains("NotARecord", lmp003[0].GetMessage());
        Assert.Contains("a class", lmp003[0].GetMessage());
    }

    [Fact]
    public void LMP003_Fires_OnNonPartialRecord()
    {
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

        var lmp003 = diagnostics.Where(d => d.Id == "LMP003").ToArray();
        Assert.Single(lmp003);
        Assert.Equal(DiagnosticSeverity.Error, lmp003[0].Severity);
        Assert.Contains("NotPartial", lmp003[0].GetMessage());
        Assert.Contains("a non-partial record", lmp003[0].GetMessage());
    }

    [Fact]
    public void NoLMP003_OnValidPartialRecord()
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
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "LMP003"));
    }

    [Fact]
    public void LMP003_Location_HasValidSpan()
    {
        var source = """
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public record BadType
            {
                public required string Value { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp003 = Assert.Single(diagnostics.Where(d => d.Id == "LMP003"));
        // Verify the diagnostic has a location (not Location.None)
        Assert.NotEqual(Location.None, lmp003.Location);
        Assert.True(lmp003.Location.SourceSpan.Length > 0,
            "Diagnostic should point at the type identifier");
    }

    [Fact]
    public void LMP003_MultipleInvalidTypes_ReportsEach()
    {
        var source = """
            using LMP;

            namespace TestApp;

            [LmpSignature("Test1")]
            public partial class ClassType
            {
                public required string A { get; init; }
            }

            [LmpSignature("Test2")]
            public record NonPartialRecord
            {
                public required string B { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp003s = diagnostics.Where(d => d.Id == "LMP003").ToArray();
        Assert.Equal(2, lmp003s.Length);
        Assert.Contains(lmp003s, d => d.GetMessage().Contains("ClassType"));
        Assert.Contains(lmp003s, d => d.GetMessage().Contains("NonPartialRecord"));
    }

    [Fact]
    public void ValidPartialRecord_ProducesNoSourceOutput()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Classify")]
            public partial record MyOutput
            {
                [Description("Value")]
                public required string Value { get; init; }
            }
            """;

        var compilation = CreateCompilation(source);
        var generator = new LmpSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out _);

        var runResult = driver.GetRunResult();
        // No source emitted yet — emitters come in Phase 2.3
        Assert.Empty(runResult.GeneratedTrees);
    }

    #endregion

    #region Input Field Extraction

    [Fact]
    public void ExtractInputFields_ReadsDescriptionFromCtorParam()
    {
        var source = """
            using System.ComponentModel;

            public record TicketInput(
                [Description("The raw ticket text")] string TicketText,
                [Description("Customer plan tier")] string AccountTier);
            """;

        var typeSymbol = GetTypeSymbol(source, "TicketInput");
        var fields = ModelExtractor.ExtractInputFields(typeSymbol, CancellationToken.None);

        Assert.Equal(2, fields.Count);
        Assert.Equal("TicketText", fields[0].Name);
        Assert.Equal("The raw ticket text", fields[0].Description);
        Assert.Equal("string", fields[0].ClrTypeName);
        Assert.Equal("AccountTier", fields[1].Name);
        Assert.Equal("Customer plan tier", fields[1].Description);
    }

    [Fact]
    public void ExtractInputFields_ReadsDescriptionFromProperty()
    {
        var source = """
            using System.ComponentModel;

            public record TicketInput
            {
                [Description("The raw ticket text")]
                public required string TicketText { get; init; }

                public required string NoDesc { get; init; }
            }
            """;

        var typeSymbol = GetTypeSymbol(source, "TicketInput");
        var fields = ModelExtractor.ExtractInputFields(typeSymbol, CancellationToken.None);

        Assert.Equal(2, fields.Count);
        Assert.Equal("TicketText", fields[0].Name);
        Assert.Equal("The raw ticket text", fields[0].Description);
        Assert.Equal("NoDesc", fields[1].Name);
        Assert.Null(fields[1].Description);
    }

    [Fact]
    public void ExtractInputFields_CtorParamPriority_OverProperty()
    {
        // When a ctor param has [Description] and the property also has [Description],
        // ctor param takes priority (it's checked first after XML docs)
        var source = """
            using System.ComponentModel;

            public record DualDesc(
                [Description("From ctor")] string Value)
            {
                [Description("From property")]
                public string Value { get; init; } = Value;
            }
            """;

        var typeSymbol = GetTypeSymbol(source, "DualDesc");
        var fields = ModelExtractor.ExtractInputFields(typeSymbol, CancellationToken.None);

        Assert.Single(fields);
        Assert.Equal("From ctor", fields[0].Description);
    }

    [Fact]
    public void ExtractInputFields_SkipsStaticAndNonPublic()
    {
        var source = """
            using System.ComponentModel;

            public record MixedInput
            {
                [Description("Public")]
                public required string PublicProp { get; init; }

                private string PrivateProp { get; init; } = "";

                public static string StaticProp { get; set; } = "";
            }
            """;

        var typeSymbol = GetTypeSymbol(source, "MixedInput");
        var fields = ModelExtractor.ExtractInputFields(typeSymbol, CancellationToken.None);

        Assert.Single(fields);
        Assert.Equal("PublicProp", fields[0].Name);
    }

    #endregion

    #region GetDescriptionFromAttribute

    [Fact]
    public void GetDescriptionFromAttribute_ReturnsDescription()
    {
        var source = """
            using System.ComponentModel;

            public class Foo
            {
                [Description("Hello world")]
                public string Bar { get; set; } = "";
            }
            """;

        var typeSymbol = GetTypeSymbol(source, "Foo");
        var prop = typeSymbol.GetMembers("Bar").OfType<IPropertySymbol>().Single();
        var desc = ModelExtractor.GetDescriptionFromAttribute(prop);

        Assert.Equal("Hello world", desc);
    }

    [Fact]
    public void GetDescriptionFromAttribute_ReturnsNull_WhenMissing()
    {
        var source = """
            public class Foo
            {
                public string Bar { get; set; } = "";
            }
            """;

        var typeSymbol = GetTypeSymbol(source, "Foo");
        var prop = typeSymbol.GetMembers("Bar").OfType<IPropertySymbol>().Single();
        var desc = ModelExtractor.GetDescriptionFromAttribute(prop);

        Assert.Null(desc);
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

    private static INamedTypeSymbol GetTypeSymbol(string source, string typeName)
    {
        var compilation = CreateCompilation(source);
        var tree = compilation.SyntaxTrees.First();
        var model = compilation.GetSemanticModel(tree);

        var typeDecl = tree.GetRoot()
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == typeName);

        return model.GetDeclaredSymbol(typeDecl)!;
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

    #endregion
}
