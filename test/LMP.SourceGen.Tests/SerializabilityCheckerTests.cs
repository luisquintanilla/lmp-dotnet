using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace LMP.SourceGen.Tests;

public class SerializabilityCheckerTests
{
    #region Serializable Types (should NOT be flagged)

    [Theory]
    [InlineData("string")]
    [InlineData("int")]
    [InlineData("double")]
    [InlineData("bool")]
    [InlineData("float")]
    [InlineData("decimal")]
    [InlineData("long")]
    [InlineData("char")]
    [InlineData("byte")]
    [InlineData("short")]
    [InlineData("object")]
    public void PrimitiveTypes_AreSerializable(string typeName)
    {
        var typeSymbol = GetPropertyTypeSymbol(typeName);
        Assert.False(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Theory]
    [InlineData("int?")]
    [InlineData("bool?")]
    [InlineData("double?")]
    public void NullableValueTypes_AreSerializable(string typeName)
    {
        var typeSymbol = GetPropertyTypeSymbol(typeName);
        Assert.False(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Theory]
    [InlineData("string[]")]
    [InlineData("int[]")]
    public void Arrays_OfSerializableTypes_AreSerializable(string typeName)
    {
        var typeSymbol = GetPropertyTypeSymbol(typeName);
        Assert.False(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Fact]
    public void ListOfString_IsSerializable()
    {
        var typeSymbol = GetPropertyTypeSymbol("System.Collections.Generic.List<string>");
        Assert.False(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Fact]
    public void DictionaryOfStringInt_IsSerializable()
    {
        var typeSymbol = GetPropertyTypeSymbol("System.Collections.Generic.Dictionary<string, int>");
        Assert.False(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Fact]
    public void DateTime_IsSerializable()
    {
        var typeSymbol = GetPropertyTypeSymbol("System.DateTime");
        Assert.False(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    #endregion

    #region Non-Serializable Types (should be flagged)

    [Fact]
    public void ActionOfString_IsNonSerializable()
    {
        var typeSymbol = GetPropertyTypeSymbol("System.Action<string>");
        Assert.True(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Fact]
    public void Action_IsNonSerializable()
    {
        var typeSymbol = GetPropertyTypeSymbol("System.Action");
        Assert.True(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Fact]
    public void FuncOfStringInt_IsNonSerializable()
    {
        var typeSymbol = GetPropertyTypeSymbol("System.Func<string, int>");
        Assert.True(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Fact]
    public void IntPtr_IsNonSerializable()
    {
        var typeSymbol = GetPropertyTypeSymbol("System.IntPtr");
        Assert.True(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Fact]
    public void UIntPtr_IsNonSerializable()
    {
        var typeSymbol = GetPropertyTypeSymbol("System.UIntPtr");
        Assert.True(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Fact]
    public void CancellationToken_IsNonSerializable()
    {
        var typeSymbol = GetPropertyTypeSymbol("System.Threading.CancellationToken");
        Assert.True(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Fact]
    public void CustomDelegate_IsNonSerializable()
    {
        var source = """
            public delegate void MyHandler(string s);
            public class Foo { public MyHandler? Prop { get; set; } }
            """;
        var typeSymbol = GetPropertyTypeSymbolFromSource(source, "Foo", "Prop");
        Assert.True(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Fact]
    public void TaskOfString_IsNonSerializable()
    {
        var typeSymbol = GetPropertyTypeSymbol("System.Threading.Tasks.Task<string>");
        Assert.True(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    [Fact]
    public void Task_IsNonSerializable()
    {
        var typeSymbol = GetPropertyTypeSymbol("System.Threading.Tasks.Task");
        Assert.True(SerializabilityChecker.IsNonSerializable(typeSymbol));
    }

    #endregion

    #region Helpers

    private static ITypeSymbol GetPropertyTypeSymbol(string typeName)
    {
        var source = "#nullable disable\npublic class Foo { public " + typeName + " Prop { get; set; } }";
        return GetPropertyTypeSymbolFromSource(source, "Foo", "Prop");
    }

    private static ITypeSymbol GetPropertyTypeSymbolFromSource(string source, string typeName, string propertyName)
    {
        var compilation = CreateCompilation(source);
        var tree = compilation.SyntaxTrees.First();
        var model = compilation.GetSemanticModel(tree);

        var typeDecl = tree.GetRoot()
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == typeName);

        var typeSymbol = model.GetDeclaredSymbol(typeDecl)!;
        var prop = typeSymbol.GetMembers(propertyName).OfType<IPropertySymbol>().Single();

        return prop.Type;
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var runtimeDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return CSharpCompilation.Create(
            "TestAssembly",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Collections.dll")),
            ],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    #endregion
}
