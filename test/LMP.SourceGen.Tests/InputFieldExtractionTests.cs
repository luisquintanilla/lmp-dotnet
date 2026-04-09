using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace LMP.SourceGen.Tests;

/// <summary>
/// Tests for input field extraction with priority-based description resolution:
/// XML doc param > [Description] on ctor params > [Description] on properties.
/// </summary>
public class InputFieldExtractionTests
{
    [Fact]
    public void ExtractsFields_FromRecordWithDescriptionOnCtorParams()
    {
        var source = """
            using System.ComponentModel;

            namespace TestApp;

            public record TicketInput(
                [Description("The raw ticket text")] string TicketText,
                [Description("Customer plan tier")] string AccountTier);
            """;

        var fields = ExtractInputFieldsFromType(source, "TicketInput");

        Assert.Equal(2, fields.Count);

        Assert.Equal("TicketText", fields[0].Name);
        Assert.Equal("string", fields[0].ClrTypeName);
        Assert.Equal("The raw ticket text", fields[0].Description);

        Assert.Equal("AccountTier", fields[1].Name);
        Assert.Equal("string", fields[1].ClrTypeName);
        Assert.Equal("Customer plan tier", fields[1].Description);
    }

    [Fact]
    public void ExtractsFields_FromRecordWithDescriptionOnProperties()
    {
        var source = """
            using System.ComponentModel;

            namespace TestApp;

            public record PropertyInput
            {
                [Description("The text field")]
                public required string Text { get; init; }

                [Description("The count field")]
                public required int Count { get; init; }
            }
            """;

        var fields = ExtractInputFieldsFromType(source, "PropertyInput");

        Assert.Equal(2, fields.Count);

        Assert.Equal("Text", fields[0].Name);
        Assert.Equal("The text field", fields[0].Description);

        Assert.Equal("Count", fields[1].Name);
        Assert.Equal("The count field", fields[1].Description);
    }

    [Fact]
    public void ExtractsFields_WithNoDescription_ReturnsNull()
    {
        var source = """
            namespace TestApp;

            public record SimpleInput(string FieldA, int FieldB);
            """;

        var fields = ExtractInputFieldsFromType(source, "SimpleInput");

        Assert.Equal(2, fields.Count);
        Assert.Null(fields[0].Description);
        Assert.Null(fields[1].Description);
    }

    [Fact]
    public void DescriptionPriority_CtorParamOverProperty()
    {
        // When both ctor param and property have [Description], ctor param wins
        var source = """
            using System.ComponentModel;

            namespace TestApp;

            public record MixedInput(
                [Description("From constructor")] string Field)
            {
                // Property-level description should be ignored because ctor param has it
                [Description("From property")]
                public string Field { get; init; } = Field;
            }
            """;

        var fields = ExtractInputFieldsFromType(source, "MixedInput");

        var field = Assert.Single(fields);
        Assert.Equal("From constructor", field.Description);
    }

    [Fact]
    public void ExtractsFields_SkipsStaticAndNonPublic()
    {
        var source = """
            using System.ComponentModel;

            namespace TestApp;

            public record FilteredInput
            {
                [Description("Public field")]
                public required string PublicField { get; init; }

                private string _privateField = "";
                internal string InternalField { get; init; } = "";
                public static string StaticField { get; set; } = "";
            }
            """;

        var fields = ExtractInputFieldsFromType(source, "FilteredInput");

        var field = Assert.Single(fields);
        Assert.Equal("PublicField", field.Name);
    }

    [Fact]
    public void ExtractsFields_ComplexTypes()
    {
        var source = """
            using System.Collections.Generic;

            namespace TestApp;

            public record ComplexInput(
                string Name,
                int Age,
                List<string> Tags);
            """;

        var fields = ExtractInputFieldsFromType(source, "ComplexInput");

        Assert.Equal(3, fields.Count);
        Assert.Equal("string", fields[0].ClrTypeName);
        Assert.Equal("int", fields[1].ClrTypeName);
        Assert.Equal("List<string>", fields[2].ClrTypeName);
    }

    private static EquatableArray<InputFieldModel> ExtractInputFieldsFromType(
        string source, string typeName)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.ComponentModel.DescriptionAttribute).Assembly.Location),
                .. GetNetCoreReferences(),
            ],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var typeSymbol = compilation.GetSymbolsWithName(typeName).OfType<INamedTypeSymbol>().Single();

        return ModelExtractor.ExtractInputFields(typeSymbol, System.Threading.CancellationToken.None);
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
}
