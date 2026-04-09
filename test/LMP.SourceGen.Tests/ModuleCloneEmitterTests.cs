using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace LMP.SourceGen.Tests;

/// <summary>
/// Tests for CloneCore() emission by <see cref="ModuleEmitter"/>.
/// </summary>
public class ModuleCloneEmitterTests
{
    #region Direct emitter tests — CloneCore generation

    [Fact]
    public void GenerateSource_EmitsCloneCoreOverride()
    {
        var model = CreateModuleModel();
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("protected override LmpModule CloneCore()", source);
    }

    [Fact]
    public void GenerateSource_CloneCore_UsesMemberwiseClone()
    {
        var model = CreateModuleModel(typeName: "MyModule");
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("var clone = (MyModule)MemberwiseClone();", source);
    }

    [Fact]
    public void GenerateSource_CloneCore_ReturnsClone()
    {
        var model = CreateModuleModel();
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("return clone;", source);
    }

    [Fact]
    public void GenerateSource_ReadonlyField_EmitsUnsafeAccessor()
    {
        var model = CreateModuleModel(fields: new[]
        {
            new PredictorFieldModel(
                "_predict",
                "global::App.Input",
                "global::App.Output",
                "global::LMP.Predictor<global::App.Input, global::App.Output>",
                CanAssignDirectly: false,
                UnsafeAccessorFieldName: "_predict"),
        });
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("[UnsafeAccessor(UnsafeAccessorKind.Field, Name = \"_predict\")]", source);
        Assert.Contains("private static extern ref global::LMP.Predictor<global::App.Input, global::App.Output> __cloneRef__predict(TestModule instance);", source);
    }

    [Fact]
    public void GenerateSource_ReadonlyField_UsesAccessorInCloneCore()
    {
        var model = CreateModuleModel(fields: new[]
        {
            new PredictorFieldModel(
                "_predict",
                "global::App.Input",
                "global::App.Output",
                "global::LMP.Predictor<global::App.Input, global::App.Output>",
                CanAssignDirectly: false,
                UnsafeAccessorFieldName: "_predict"),
        });
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("__cloneRef__predict(clone) = (global::LMP.Predictor<global::App.Input, global::App.Output>)((global::LMP.IPredictor)_predict).Clone();", source);
    }

    [Fact]
    public void GenerateSource_NonReadonlyField_UsesDirectAssignment()
    {
        var model = CreateModuleModel(fields: new[]
        {
            new PredictorFieldModel(
                "_predict",
                "global::App.Input",
                "global::App.Output",
                "global::LMP.Predictor<global::App.Input, global::App.Output>",
                CanAssignDirectly: true),
        });
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("clone._predict = (global::LMP.Predictor<global::App.Input, global::App.Output>)((global::LMP.IPredictor)_predict).Clone();", source);
        Assert.DoesNotContain("UnsafeAccessor", source);
    }

    [Fact]
    public void GenerateSource_MultipleReadonlyFields_EmitsMultipleAccessors()
    {
        var model = CreateModuleModel(fields: new[]
        {
            new PredictorFieldModel(
                "_classify",
                "global::App.TicketInput",
                "global::App.ClassifyTicket",
                "global::LMP.Predictor<global::App.TicketInput, global::App.ClassifyTicket>",
                CanAssignDirectly: false,
                UnsafeAccessorFieldName: "_classify"),
            new PredictorFieldModel(
                "_draft",
                "global::App.ClassifyTicket",
                "global::App.DraftReply",
                "global::LMP.Predictor<global::App.ClassifyTicket, global::App.DraftReply>",
                CanAssignDirectly: false,
                UnsafeAccessorFieldName: "_draft"),
        });
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("__cloneRef__classify", source);
        Assert.Contains("__cloneRef__draft", source);
        Assert.Contains("[UnsafeAccessor(UnsafeAccessorKind.Field, Name = \"_classify\")]", source);
        Assert.Contains("[UnsafeAccessor(UnsafeAccessorKind.Field, Name = \"_draft\")]", source);
    }

    [Fact]
    public void GenerateSource_MixedReadonlyAndWritable_EmitsCorrectCode()
    {
        var model = CreateModuleModel(fields: new[]
        {
            new PredictorFieldModel(
                "_readonlyField",
                "global::A",
                "global::B",
                "global::LMP.Predictor<global::A, global::B>",
                CanAssignDirectly: false,
                UnsafeAccessorFieldName: "_readonlyField"),
            new PredictorFieldModel(
                "writableField",
                "global::C",
                "global::D",
                "global::LMP.Predictor<global::C, global::D>",
                CanAssignDirectly: true),
        });
        var source = ModuleEmitter.GenerateSource(model);

        // Readonly uses accessor
        Assert.Contains("__cloneRef__readonlyField(clone)", source);
        // Writable uses direct assignment
        Assert.Contains("clone.writableField =", source);
        // Only one UnsafeAccessor
        Assert.Single(source.Split("[UnsafeAccessor(").Skip(1));
    }

    [Fact]
    public void GenerateSource_EmptyFieldTypeFQN_FallsBackToPredictor()
    {
        // Tests the fallback when FieldTypeFQN is empty (backward compat with old model)
        var model = CreateModuleModel(fields: new[]
        {
            new PredictorFieldModel("_predict", "global::A.Input", "global::A.Output",
                FieldTypeFQN: ""),
        });
        var source = ModuleEmitter.GenerateSource(model);

        // Should compute fallback type
        Assert.Contains("global::LMP.Predictor<global::A.Input, global::A.Output>", source);
        Assert.DoesNotContain("()(", source); // No empty cast
    }

    [Fact]
    public void GenerateSource_GetOnlyPropertyBackingField_UsesBackingFieldAccessor()
    {
        var model = CreateModuleModel(fields: new[]
        {
            new PredictorFieldModel(
                "Predict",
                "global::A",
                "global::B",
                "global::LMP.Predictor<global::A, global::B>",
                CanAssignDirectly: false,
                UnsafeAccessorFieldName: "<Predict>k__BackingField"),
        });
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("[UnsafeAccessor(UnsafeAccessorKind.Field, Name = \"<Predict>k__BackingField\")]", source);
        // Accessor name is derived from FieldName, not the backing field name
        Assert.Contains("__cloneRef_Predict", source);
    }

    [Fact]
    public void GenerateSource_CloneCore_IncludesUsingForUnsafeAccessor()
    {
        var model = CreateModuleModel();
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("using System.Runtime.CompilerServices;", source);
    }

    [Fact]
    public void GenerateSource_CloneCore_ProducesSyntacticallyValidCSharp()
    {
        var model = CreateModuleModel(fields: new[]
        {
            new PredictorFieldModel(
                "_classify",
                "global::TestApp.TicketInput",
                "global::TestApp.ClassifyTicket",
                "global::LMP.Predictor<global::TestApp.TicketInput, global::TestApp.ClassifyTicket>",
                CanAssignDirectly: false,
                UnsafeAccessorFieldName: "_classify"),
        });
        var source = ModuleEmitter.GenerateSource(model);

        var tree = CSharpSyntaxTree.ParseText(source);
        var syntaxDiagnostics = tree.GetDiagnostics().ToArray();

        Assert.Empty(syntaxDiagnostics);
    }

    [Fact]
    public void GenerateSource_CloneCore_StructureOrder()
    {
        var model = CreateModuleModel(
            ns: "Demo",
            typeName: "TicketTriageModule",
            fields: new[]
            {
                new PredictorFieldModel(
                    "_classify",
                    "global::Demo.TicketInput",
                    "global::Demo.ClassifyTicket",
                    "global::LMP.Predictor<global::Demo.TicketInput, global::Demo.ClassifyTicket>",
                    CanAssignDirectly: false,
                    UnsafeAccessorFieldName: "_classify"),
            });
        var source = ModuleEmitter.GenerateSource(model);

        var getPredictorsIdx = source.IndexOf("GetPredictors()");
        var cloneCoreIdx = source.IndexOf("CloneCore()");
        var accessorIdx = source.IndexOf("[UnsafeAccessor");

        Assert.True(getPredictorsIdx > 0, "GetPredictors should be present");
        Assert.True(cloneCoreIdx > getPredictorsIdx, "CloneCore should follow GetPredictors");
        Assert.True(accessorIdx > cloneCoreIdx, "UnsafeAccessor should follow CloneCore");
    }

    #endregion

    #region Pipeline integration tests — full generator

    [Fact]
    public void Pipeline_EmitsCloneCore_ForModuleWithReadonlyFields()
    {
        var source = GetModulePipelineSource();
        var (diagnostics, runResult) = RunGenerator(source);

        Assert.Empty(diagnostics);

        var predictorsFile = runResult.Results[0].GeneratedSources
            .FirstOrDefault(s => s.HintName.Contains("Predictors"));
        Assert.True(predictorsFile.HintName is not null,
            "Expected a .Predictors.g.cs file to be generated");

        var generatedSource = predictorsFile.SourceText.ToString();
        Assert.Contains("protected override LmpModule CloneCore()", generatedSource);
        Assert.Contains("MemberwiseClone()", generatedSource);
    }

    [Fact]
    public void Pipeline_ReadonlyFields_EmitUnsafeAccessor()
    {
        var source = GetModulePipelineSource();
        var (_, runResult) = RunGenerator(source);

        var generatedSource = runResult.Results[0].GeneratedSources
            .First(s => s.HintName.Contains("Predictors"))
            .SourceText.ToString();

        Assert.Contains("[UnsafeAccessor(UnsafeAccessorKind.Field", generatedSource);
        Assert.Contains("_classify", generatedSource);
        Assert.Contains("_draft", generatedSource);
    }

    [Fact]
    public void Pipeline_WritableProperty_UsesDirectAssignment()
    {
        var source = """
            namespace LMP
            {
                public abstract class LmpModule
                {
                    public virtual System.Collections.Generic.IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => System.Array.Empty<(string, IPredictor)>();
                    protected virtual LmpModule CloneCore() => throw new System.NotSupportedException();
                }
                public interface IPredictor
                {
                    IPredictor Clone();
                }
                public class Predictor<TInput, TOutput> : IPredictor where TOutput : class
                {
                    public Predictor(object client) { }
                    public IPredictor Clone() => this;
                }
            }

            namespace TestApp
            {
                using LMP;
                public record Input1(string Text);
                public record Output1 { public required string Result { get; init; } }

                public partial class PropModule : LmpModule
                {
                    public Predictor<Input1, Output1> Predict { get; set; } = null!;

                    public override System.Threading.Tasks.Task<object> ForwardAsync(
                        object input, System.Threading.CancellationToken ct = default)
                        => System.Threading.Tasks.Task.FromResult<object>(null!);
                }
            }
            """;
        var (_, runResult) = RunGenerator(source);

        var generatedSource = runResult.Results[0].GeneratedSources
            .First(s => s.HintName.Contains("Predictors"))
            .SourceText.ToString();

        // Writable property should use direct assignment, not UnsafeAccessor
        Assert.Contains("clone.Predict =", generatedSource);
        Assert.DoesNotContain("UnsafeAccessor", generatedSource);
    }

    #endregion

    #region Helpers

    private static ModuleModel CreateModuleModel(
        string ns = "TestApp",
        string typeName = "TestModule",
        PredictorFieldModel[]? fields = null)
    {
        fields ??= new[]
        {
            new PredictorFieldModel(
                "_classify",
                "global::TestApp.TicketInput",
                "global::TestApp.ClassifyTicket",
                "global::LMP.Predictor<global::TestApp.TicketInput, global::TestApp.ClassifyTicket>",
                CanAssignDirectly: false,
                UnsafeAccessorFieldName: "_classify"),
            new PredictorFieldModel(
                "_draft",
                "global::TestApp.ClassifyTicket",
                "global::TestApp.DraftReply",
                "global::LMP.Predictor<global::TestApp.ClassifyTicket, global::TestApp.DraftReply>",
                CanAssignDirectly: false,
                UnsafeAccessorFieldName: "_draft"),
        };

        return new ModuleModel(
            Namespace: ns,
            TypeName: typeName,
            PredictorFields: new EquatableArray<PredictorFieldModel>(fields.ToImmutableArray()));
    }

    private static string GetModulePipelineSource() => """
        namespace LMP
        {
            public abstract class LmpModule
            {
                public virtual System.Collections.Generic.IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => System.Array.Empty<(string, IPredictor)>();
                protected virtual LmpModule CloneCore() => throw new System.NotSupportedException();
            }
            public interface IPredictor
            {
                IPredictor Clone();
            }
            public class Predictor<TInput, TOutput> : IPredictor where TOutput : class
            {
                public Predictor(object client) { }
                public IPredictor Clone() => this;
            }
        }

        namespace TestApp
        {
            using LMP;

            public record TicketInput(string TicketText);
            public record ClassifyTicket { public required string Category { get; init; } }
            public partial record DraftReply { public required string Text { get; init; } }

            public partial class TicketTriageModule : LmpModule
            {
                private readonly Predictor<TicketInput, ClassifyTicket> _classify;
                private readonly Predictor<ClassifyTicket, DraftReply> _draft;

                public TicketTriageModule(object client)
                {
                    _classify = new Predictor<TicketInput, ClassifyTicket>(client);
                    _draft = new Predictor<ClassifyTicket, DraftReply>(client);
                }

                public override System.Threading.Tasks.Task<object> ForwardAsync(
                    object input, System.Threading.CancellationToken ct = default)
                    => System.Threading.Tasks.Task.FromResult<object>(null!);
            }
        }
        """;

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
            ? System.Array.Empty<Microsoft.CodeAnalysis.SyntaxTree>()
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
