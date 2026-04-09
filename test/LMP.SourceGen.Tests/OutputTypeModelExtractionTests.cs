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
    public void ValidPartialRecord_ProducesJsonContextSourceOutput()
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
        // JsonContext emitted for valid partial records
        Assert.Single(runResult.GeneratedTrees);
        var generatedSource = runResult.GeneratedTrees[0].GetText().ToString();
        Assert.Contains("MyOutputJsonOptions", generatedSource);
        Assert.Contains("JsonSerializerOptions", generatedSource);
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

    #region LMP001 — Missing Description

    [Fact]
    public void LMP001_Fires_OnPropertyWithoutDescription()
    {
        var source = """
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Classify a support ticket")]
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

            [LmpSignature("Classify")]
            public partial record ClassifyTicket
            {
                [Description("Category")]
                public required string Category { get; init; }

                [Description("Urgency level")]
                public required int Urgency { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "LMP001"));
    }

    [Fact]
    public void LMP001_Fires_OnMultiplePropertiesMissingDescription()
    {
        var source = """
            using LMP;

            namespace TestApp;

            [LmpSignature("Extract")]
            public partial record Extraction
            {
                public required string Name { get; init; }
                public required string Location { get; init; }
                public required int Count { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp001s = diagnostics.Where(d => d.Id == "LMP001").ToArray();
        Assert.Equal(3, lmp001s.Length);
        Assert.Contains(lmp001s, d => d.GetMessage().Contains("Name"));
        Assert.Contains(lmp001s, d => d.GetMessage().Contains("Location"));
        Assert.Contains(lmp001s, d => d.GetMessage().Contains("Count"));
    }

    [Fact]
    public void LMP001_StillGeneratesCode_WhenWarningPresent()
    {
        var source = """
            using LMP;

            namespace TestApp;

            [LmpSignature("Classify")]
            public partial record MyType
            {
                public required string Value { get; init; }
            }
            """;

        var compilation = CreateCompilation(source);
        var generator = new LmpSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out var diagnostics);

        // LMP001 warning fires
        Assert.Single(diagnostics.Where(d => d.Id == "LMP001"));

        // But JsonContext is still generated
        var runResult = driver.GetRunResult();
        Assert.Single(runResult.GeneratedTrees);
        var generatedSource = runResult.GeneratedTrees[0].GetText().ToString();
        Assert.Contains("MyTypeJsonOptions", generatedSource);
    }

    [Fact]
    public void LMP001_Location_PointsToProperty()
    {
        var source = """
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record MyOutput
            {
                public required string MissingDesc { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp001 = Assert.Single(diagnostics.Where(d => d.Id == "LMP001"));
        Assert.NotEqual(Location.None, lmp001.Location);
        Assert.True(lmp001.Location.SourceSpan.Length > 0,
            "Diagnostic should point at the property");
    }

    [Fact]
    public void LMP001_DoesNotFire_OnNonPartialRecord()
    {
        // LMP003 fires but LMP001 should not (non-partial types don't get field extraction)
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

    #region LMP002 — Non-Serializable Output

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
            public partial record BadType
            {
                [Description("A function")]
                public required Func<string, int> Handler { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp002 = diagnostics.Where(d => d.Id == "LMP002").ToArray();
        Assert.Single(lmp002);
        Assert.Contains("Handler", lmp002[0].GetMessage());
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
            public partial record HasDelegate
            {
                [Description("A delegate")]
                public required MyDelegate OnEvent { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp002 = diagnostics.Where(d => d.Id == "LMP002").ToArray();
        Assert.Single(lmp002);
        Assert.Contains("OnEvent", lmp002[0].GetMessage());
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
            public partial record HasIntPtr
            {
                [Description("A pointer")]
                public required IntPtr Pointer { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Single(diagnostics.Where(d => d.Id == "LMP002"));
    }

    [Fact]
    public void LMP002_DoesNotFire_OnSerializableTypes()
    {
        var source = """
            using System.ComponentModel;
            using System.Collections.Generic;
            using LMP;

            namespace TestApp;

            [LmpSignature("Classify")]
            public partial record GoodType
            {
                [Description("Name")]
                public required string Name { get; init; }

                [Description("Count")]
                public required int Count { get; init; }

                [Description("Score")]
                public required double Score { get; init; }

                [Description("Active")]
                public required bool Active { get; init; }

                [Description("Tags")]
                public required List<string> Tags { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "LMP002"));
    }

    [Fact]
    public void LMP002_SkipsCodeGeneration_WhenNonSerializablePresent()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record BadOutput
            {
                [Description("Good")]
                public required string Good { get; init; }

                [Description("Bad")]
                public required Action BadAction { get; init; }
            }
            """;

        var compilation = CreateCompilation(source);
        var generator = new LmpSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out _, out var diagnostics);

        // LMP002 fires
        Assert.Single(diagnostics.Where(d => d.Id == "LMP002"));

        // No JsonContext generated (code gen skipped)
        var runResult = driver.GetRunResult();
        Assert.Empty(runResult.GeneratedTrees);
    }

    [Fact]
    public void LMP002_MultipleNonSerializableProperties_ReportsEach()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record MultipleErrors
            {
                [Description("D1")]
                public required Action<string> A { get; init; }

                [Description("D2")]
                public required Func<int> B { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp002s = diagnostics.Where(d => d.Id == "LMP002").ToArray();
        Assert.Equal(2, lmp002s.Length);
        Assert.Contains(lmp002s, d => d.GetMessage().Contains("A"));
        Assert.Contains(lmp002s, d => d.GetMessage().Contains("B"));
    }

    [Fact]
    public void LMP002_Location_PointsToProperty()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record HasBadProp
            {
                [Description("Bad")]
                public required Action<string> Callback { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        var lmp002 = Assert.Single(diagnostics.Where(d => d.Id == "LMP002"));
        Assert.NotEqual(Location.None, lmp002.Location);
        Assert.True(lmp002.Location.SourceSpan.Length > 0);
    }

    [Fact]
    public void LMP001_And_LMP002_CanFireOnSameType()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public partial record MixedIssues
            {
                public required string NoDescription { get; init; }

                [Description("Bad")]
                public required Action<string> Callback { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Single(diagnostics.Where(d => d.Id == "LMP001"));
        Assert.Single(diagnostics.Where(d => d.Id == "LMP002"));
    }

    [Fact]
    public void LMP002_DoesNotFire_OnNonPartialRecord()
    {
        var source = """
            using System;
            using System.ComponentModel;
            using LMP;

            namespace TestApp;

            [LmpSignature("Test")]
            public record NotPartial
            {
                [Description("Bad")]
                public required Action<string> Callback { get; init; }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "LMP002"));
        Assert.NotEmpty(diagnostics.Where(d => d.Id == "LMP003"));
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
            MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Collections.dll")),
        ];
    }

    #endregion
}
