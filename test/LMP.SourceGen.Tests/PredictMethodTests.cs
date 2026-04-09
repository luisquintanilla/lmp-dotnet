using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace LMP.SourceGen.Tests;

/// <summary>
/// Tests for Phase 8 — [Predict] partial method sugar.
/// Verifies that the source generator discovers [Predict]-decorated partial methods
/// on LmpModule subclasses, emits backing Predictor fields, method implementations,
/// and includes them in GetPredictors().
/// </summary>
public class PredictMethodTests
{
    #region Direct emitter tests — ModuleEmitter.GenerateSource with PredictMethods

    [Fact]
    public void GenerateSource_EmitsBackingField_ForPredictMethod()
    {
        var model = CreateModuleModelWithPredictMethods();
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("__predict_ClassifyAsync", source);
        Assert.Contains("global::LMP.Predictor<global::TestApp.TicketInput, global::TestApp.ClassifyTicket>?", source);
    }

    [Fact]
    public void GenerateSource_EmitsPartialMethodBody()
    {
        var model = CreateModuleModelWithPredictMethods();
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("public partial async Task<global::TestApp.ClassifyTicket> ClassifyAsync(", source);
        Assert.Contains("return await __predict_ClassifyAsync.PredictAsync(input, Trace);", source);
    }

    [Fact]
    public void GenerateSource_IncludesPredictMethodInGetPredictors()
    {
        var model = CreateModuleModelWithPredictMethods();
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("(\"ClassifyAsync\", __predict_ClassifyAsync),", source);
    }

    [Fact]
    public void GenerateSource_MultiplePredictMethods_EmitsAll()
    {
        var model = CreateModuleModelWithMultiplePredictMethods();
        var source = ModuleEmitter.GenerateSource(model);

        // Both backing fields
        Assert.Contains("__predict_ClassifyAsync", source);
        Assert.Contains("__predict_DraftAsync", source);

        // Both method bodies
        Assert.Contains("public partial async Task<global::TestApp.ClassifyTicket> ClassifyAsync(", source);
        Assert.Contains("public partial async Task<global::TestApp.DraftReply> DraftAsync(", source);

        // Both in GetPredictors
        Assert.Contains("(\"ClassifyAsync\", __predict_ClassifyAsync),", source);
        Assert.Contains("(\"DraftAsync\", __predict_DraftAsync),", source);
    }

    [Fact]
    public void GenerateSource_MixedFieldsAndPredictMethods_IncludesAll()
    {
        var model = new ModuleModel(
            Namespace: "TestApp",
            TypeName: "MixedModule",
            PredictorFields: new EquatableArray<PredictorFieldModel>(ImmutableArray.Create(
                new PredictorFieldModel("_explicit", "global::TestApp.Input1", "global::TestApp.Output1"))),
            PredictMethods: new EquatableArray<PredictMethodModel>(ImmutableArray.Create(
                new PredictMethodModel("PredictAsync", "global::TestApp.Input2", "global::TestApp.Output2", "input"))));

        var source = ModuleEmitter.GenerateSource(model);

        // Explicit field in GetPredictors
        Assert.Contains("(\"explicit\", _explicit),", source);
        // [Predict] method in GetPredictors
        Assert.Contains("(\"PredictAsync\", __predict_PredictAsync),", source);
    }

    [Fact]
    public void GenerateSource_PredictMethod_LazyInitializesFromClient()
    {
        var model = CreateModuleModelWithPredictMethods();
        var source = ModuleEmitter.GenerateSource(model);

        // The method body should lazily initialize the backing field from Client
        Assert.Contains("Client ?? throw new global::System.InvalidOperationException", source);
    }

    [Fact]
    public void GenerateSource_PredictMethod_CloneHandlesNullableBackingField()
    {
        var model = CreateModuleModelWithPredictMethods();
        var source = ModuleEmitter.GenerateSource(model);

        // CloneCore should conditionally clone nullable backing fields
        Assert.Contains("if (__predict_ClassifyAsync is not null)", source);
        Assert.Contains("clone.__predict_ClassifyAsync", source);
    }

    [Fact]
    public void GenerateSource_PredictMethod_IncludesTaskUsing()
    {
        var model = CreateModuleModelWithPredictMethods();
        var source = ModuleEmitter.GenerateSource(model);

        Assert.Contains("using System.Threading.Tasks;", source);
    }

    [Fact]
    public void GenerateSource_NoPredictMethods_NoBackingFields()
    {
        var model = new ModuleModel(
            Namespace: "TestApp",
            TypeName: "TestModule",
            PredictorFields: new EquatableArray<PredictorFieldModel>(ImmutableArray.Create(
                new PredictorFieldModel("_classify", "global::TestApp.In", "global::TestApp.Out"))));

        var source = ModuleEmitter.GenerateSource(model);

        Assert.DoesNotContain("__predict_", source);
    }

    #endregion

    #region Pipeline integration tests — full generator

    [Fact]
    public void Pipeline_PredictMethod_EmitsCodeForModuleWithPredictAttribute()
    {
        var source = GetPredictMethodPipelineSource();
        var (diagnostics, runResult) = RunGenerator(source);

        Assert.Empty(diagnostics);

        var predictorsFile = runResult.Results[0].GeneratedSources
            .FirstOrDefault(s => s.HintName.Contains("Predictors"));
        Assert.True(predictorsFile.HintName is not null,
            "Expected a .Predictors.g.cs file to be generated");

        var generatedSource = predictorsFile.SourceText.ToString();
        Assert.Contains("partial class PredictModule", generatedSource);
        Assert.Contains("__predict_ClassifyAsync", generatedSource);
        Assert.Contains("GetPredictors()", generatedSource);
    }

    [Fact]
    public void Pipeline_PredictMethod_EmitsMethodBody()
    {
        var source = GetPredictMethodPipelineSource();
        var (_, runResult) = RunGenerator(source);

        var predictorsFile = runResult.Results[0].GeneratedSources
            .First(s => s.HintName.Contains("Predictors"));

        var generatedSource = predictorsFile.SourceText.ToString();
        Assert.Contains("public partial async Task<global::TestApp.ClassifyTicket> ClassifyAsync(", generatedSource);
    }

    [Fact]
    public void Pipeline_MultiplePredictMethods_EmitsAllMethods()
    {
        var source = GetMultiplePredictMethodPipelineSource();
        var (diagnostics, runResult) = RunGenerator(source);

        Assert.Empty(diagnostics);

        var predictorsFile = runResult.Results[0].GeneratedSources
            .First(s => s.HintName.Contains("Predictors"));

        var generatedSource = predictorsFile.SourceText.ToString();
        Assert.Contains("__predict_ClassifyAsync", generatedSource);
        Assert.Contains("__predict_DraftAsync", generatedSource);
        Assert.Contains("(\"ClassifyAsync\", __predict_ClassifyAsync),", generatedSource);
        Assert.Contains("(\"DraftAsync\", __predict_DraftAsync),", generatedSource);
    }

    [Fact]
    public void Pipeline_MixedFieldsAndPredictMethods_EmitsBoth()
    {
        var source = GetMixedPipelineSource();
        var (diagnostics, runResult) = RunGenerator(source);

        Assert.Empty(diagnostics);

        var predictorsFile = runResult.Results[0].GeneratedSources
            .First(s => s.HintName.Contains("Predictors"));

        var generatedSource = predictorsFile.SourceText.ToString();
        // Explicit field
        Assert.Contains("(\"classify\", _classify),", generatedSource);
        // [Predict] method
        Assert.Contains("(\"DraftAsync\", __predict_DraftAsync),", generatedSource);
    }

    [Fact]
    public void Pipeline_NonPartialMethod_NotDetected()
    {
        var source = """
            namespace LMP
            {
                public abstract class LmpModule
                {
                    public virtual System.Collections.Generic.IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => System.Array.Empty<(string, IPredictor)>();
                    protected virtual LmpModule CloneCore() => throw new System.NotSupportedException();
                    protected Microsoft.Extensions.AI.IChatClient? Client { get; set; }
                    public Trace? Trace { get; set; }
                }
                public interface IPredictor { IPredictor Clone(); }
                public class Predictor<TInput, TOutput> : IPredictor where TOutput : class
                {
                    public Predictor(object client) { }
                    public IPredictor Clone() => this;
                }
                public class Trace { }
                [System.AttributeUsage(System.AttributeTargets.Method)]
                public sealed class PredictAttribute : System.Attribute { }
            }

            namespace TestApp
            {
                using LMP;

                public record TicketInput(string TicketText);
                public record ClassifyTicket { public required string Category { get; init; } }

                public partial class NotPartialMethodModule : LmpModule
                {
                    // Not a partial method — should NOT be detected
                    [Predict]
                    public System.Threading.Tasks.Task<ClassifyTicket> ClassifyAsync(TicketInput input)
                        => throw new System.NotImplementedException();

                    public override System.Threading.Tasks.Task<object> ForwardAsync(
                        object input, System.Threading.CancellationToken ct = default)
                        => System.Threading.Tasks.Task.FromResult<object>(null!);
                }
            }
            """;

        var (_, runResult) = RunGenerator(source);

        // No Predictors file should be generated (no partial methods, no predictor fields)
        Assert.DoesNotContain(runResult.Results[0].GeneratedSources,
            s => s.HintName.Contains("Predictors"));
    }

    [Fact]
    public void Pipeline_PredictMethod_JsonContextIncludesTypes()
    {
        var source = GetPredictMethodPipelineSource();
        var (_, runResult) = RunGenerator(source);

        var jsonContextFile = runResult.Results[0].GeneratedSources
            .FirstOrDefault(s => s.HintName.Contains("JsonContext"));

        Assert.True(jsonContextFile.HintName is not null,
            "Expected a .JsonContext.g.cs file to be generated");

        var generatedSource = jsonContextFile.SourceText.ToString();
        Assert.Contains("global::TestApp.TicketInput", generatedSource);
        Assert.Contains("global::TestApp.ClassifyTicket", generatedSource);
    }

    #endregion

    #region ModuleExtractor integration tests

    [Fact]
    public void GenerateSource_PredictMethod_UsesCorrectParameterName()
    {
        var model = new ModuleModel(
            Namespace: "TestApp",
            TypeName: "TestModule",
            PredictorFields: new EquatableArray<PredictorFieldModel>(ImmutableArray<PredictorFieldModel>.Empty),
            PredictMethods: new EquatableArray<PredictMethodModel>(ImmutableArray.Create(
                new PredictMethodModel("ClassifyAsync", "global::TestApp.TicketInput",
                    "global::TestApp.ClassifyTicket", "ticket"))));

        var source = ModuleEmitter.GenerateSource(model);

        // Parameter name should be "ticket" not "input"
        Assert.Contains("ClassifyAsync(global::TestApp.TicketInput ticket)", source);
        Assert.Contains("PredictAsync(ticket, Trace)", source);
    }

    #endregion

    #region Syntax validity tests

    [Fact]
    public void GenerateSource_PredictMethods_ProducesSyntacticallyValidCSharp()
    {
        var model = CreateModuleModelWithMultiplePredictMethods();
        var source = ModuleEmitter.GenerateSource(model);

        var tree = CSharpSyntaxTree.ParseText(source);
        var syntaxDiagnostics = tree.GetDiagnostics().ToArray();

        Assert.Empty(syntaxDiagnostics);
    }

    [Fact]
    public void GenerateSource_MixedFieldsAndPredictMethods_ProducesSyntacticallyValidCSharp()
    {
        var model = new ModuleModel(
            Namespace: "TestApp",
            TypeName: "MixedModule",
            PredictorFields: new EquatableArray<PredictorFieldModel>(ImmutableArray.Create(
                new PredictorFieldModel("_classify", "global::TestApp.In", "global::TestApp.Out",
                    "global::LMP.Predictor<global::TestApp.In, global::TestApp.Out>",
                    CanAssignDirectly: false, UnsafeAccessorFieldName: "_classify"))),
            PredictMethods: new EquatableArray<PredictMethodModel>(ImmutableArray.Create(
                new PredictMethodModel("DraftAsync", "global::TestApp.In2", "global::TestApp.Out2", "input"))));

        var source = ModuleEmitter.GenerateSource(model);
        var tree = CSharpSyntaxTree.ParseText(source);
        var syntaxDiagnostics = tree.GetDiagnostics().ToArray();

        Assert.Empty(syntaxDiagnostics);
    }

    #endregion

    #region Edge case tests — extractor filtering

    [Fact]
    public void Pipeline_PredictMethodWithNoParameters_IsIgnored()
    {
        var source = """
            namespace Microsoft.Extensions.AI
            {
                public interface IChatClient { }
            }

            namespace LMP
            {
                public abstract class LmpModule
                {
                    public virtual System.Collections.Generic.IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => System.Array.Empty<(string, IPredictor)>();
                    protected virtual LmpModule CloneCore() => throw new System.NotSupportedException();
                    protected Microsoft.Extensions.AI.IChatClient? Client { get; set; }
                    public Trace? Trace { get; set; }
                }
                public interface IPredictor { IPredictor Clone(); }
                public class Predictor<TInput, TOutput> : IPredictor where TOutput : class
                {
                    public Predictor(object client) { }
                    public IPredictor Clone() => this;
                }
                public class Trace { }
                [System.AttributeUsage(System.AttributeTargets.Method)]
                public sealed class PredictAttribute : System.Attribute { }
            }

            namespace TestApp
            {
                using LMP;

                public record Output { public required string Value { get; init; } }

                public partial class NoParamModule : LmpModule
                {
                    [Predict]
                    public partial System.Threading.Tasks.Task<Output> GetOutputAsync();

                    public override System.Threading.Tasks.Task<object> ForwardAsync(
                        object input, System.Threading.CancellationToken ct = default)
                        => System.Threading.Tasks.Task.FromResult<object>(null!);
                }
            }
            """;

        var (_, runResult) = RunGenerator(source);

        // No partial method with 0 params should be detected as [Predict]
        Assert.DoesNotContain(runResult.Results[0].GeneratedSources,
            s => s.HintName.Contains("Predictors"));
    }

    [Fact]
    public void Pipeline_PredictMethodWithMultipleParameters_IsIgnored()
    {
        var source = """
            namespace Microsoft.Extensions.AI
            {
                public interface IChatClient { }
            }

            namespace LMP
            {
                public abstract class LmpModule
                {
                    public virtual System.Collections.Generic.IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => System.Array.Empty<(string, IPredictor)>();
                    protected virtual LmpModule CloneCore() => throw new System.NotSupportedException();
                    protected Microsoft.Extensions.AI.IChatClient? Client { get; set; }
                    public Trace? Trace { get; set; }
                }
                public interface IPredictor { IPredictor Clone(); }
                public class Predictor<TInput, TOutput> : IPredictor where TOutput : class
                {
                    public Predictor(object client) { }
                    public IPredictor Clone() => this;
                }
                public class Trace { }
                [System.AttributeUsage(System.AttributeTargets.Method)]
                public sealed class PredictAttribute : System.Attribute { }
            }

            namespace TestApp
            {
                using LMP;

                public record Input(string Text);
                public record Output { public required string Value { get; init; } }

                public partial class MultiParamModule : LmpModule
                {
                    [Predict]
                    public partial System.Threading.Tasks.Task<Output> ClassifyAsync(Input input, string extra);

                    public override System.Threading.Tasks.Task<object> ForwardAsync(
                        object input, System.Threading.CancellationToken ct = default)
                        => System.Threading.Tasks.Task.FromResult<object>(null!);
                }
            }
            """;

        var (_, runResult) = RunGenerator(source);

        // Method with 2 params should not be detected
        Assert.DoesNotContain(runResult.Results[0].GeneratedSources,
            s => s.HintName.Contains("Predictors"));
    }

    [Fact]
    public void Pipeline_PredictMethodWithNonTaskReturn_IsIgnored()
    {
        var source = """
            namespace Microsoft.Extensions.AI
            {
                public interface IChatClient { }
            }

            namespace LMP
            {
                public abstract class LmpModule
                {
                    public virtual System.Collections.Generic.IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => System.Array.Empty<(string, IPredictor)>();
                    protected virtual LmpModule CloneCore() => throw new System.NotSupportedException();
                    protected Microsoft.Extensions.AI.IChatClient? Client { get; set; }
                    public Trace? Trace { get; set; }
                }
                public interface IPredictor { IPredictor Clone(); }
                public class Predictor<TInput, TOutput> : IPredictor where TOutput : class
                {
                    public Predictor(object client) { }
                    public IPredictor Clone() => this;
                }
                public class Trace { }
                [System.AttributeUsage(System.AttributeTargets.Method)]
                public sealed class PredictAttribute : System.Attribute { }
            }

            namespace TestApp
            {
                using LMP;

                public record Input(string Text);
                public record Output { public required string Value { get; init; } }

                public partial class WrongReturnModule : LmpModule
                {
                    [Predict]
                    public partial Output Classify(Input input);

                    public override System.Threading.Tasks.Task<object> ForwardAsync(
                        object input, System.Threading.CancellationToken ct = default)
                        => System.Threading.Tasks.Task.FromResult<object>(null!);
                }
            }
            """;

        var (_, runResult) = RunGenerator(source);

        // Method not returning Task<T> should not be detected
        Assert.DoesNotContain(runResult.Results[0].GeneratedSources,
            s => s.HintName.Contains("Predictors"));
    }

    [Fact]
    public void Pipeline_PredictMethod_InGlobalNamespace_EmitsCorrectly()
    {
        var source = """
            namespace Microsoft.Extensions.AI
            {
                public interface IChatClient { }
            }

            namespace LMP
            {
                public abstract class LmpModule
                {
                    public virtual System.Collections.Generic.IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => System.Array.Empty<(string, IPredictor)>();
                    protected virtual LmpModule CloneCore() => throw new System.NotSupportedException();
                    protected Microsoft.Extensions.AI.IChatClient? Client { get; set; }
                    public Trace? Trace { get; set; }
                }
                public interface IPredictor { IPredictor Clone(); }
                public class Predictor<TInput, TOutput> : IPredictor where TOutput : class
                {
                    public Predictor(object client) { }
                    public System.Threading.Tasks.Task<TOutput> PredictAsync(TInput input, Trace? trace = null) => throw new System.NotImplementedException();
                    public IPredictor Clone() => this;
                }
                public class Trace { }
                [System.AttributeUsage(System.AttributeTargets.Method)]
                public sealed class PredictAttribute : System.Attribute { }
            }

            // Types in global namespace
            public record GlobalInput(string Text);
            public record GlobalOutput { public required string Value { get; init; } }

            public partial class GlobalModule : LMP.LmpModule
            {
                [LMP.Predict]
                public partial System.Threading.Tasks.Task<GlobalOutput> ProcessAsync(GlobalInput input);

                public override System.Threading.Tasks.Task<object> ForwardAsync(
                    object input, System.Threading.CancellationToken ct = default)
                    => System.Threading.Tasks.Task.FromResult<object>(null!);
            }
            """;

        var (diagnostics, runResult) = RunGenerator(source);

        Assert.Empty(diagnostics);

        var predictorsFile = runResult.Results[0].GeneratedSources
            .FirstOrDefault(s => s.HintName.Contains("Predictors"));
        Assert.True(predictorsFile.HintName is not null,
            "Expected a .Predictors.g.cs file for global namespace module");

        var generatedSource = predictorsFile.SourceText.ToString();
        Assert.Contains("__predict_ProcessAsync", generatedSource);
        Assert.DoesNotContain("namespace", generatedSource);
    }

    #endregion

    #region PredictAttribute tests

    [Fact]
    public void PredictAttribute_CanBeInstantiated()
    {
        var attr = new PredictAttribute();
        Assert.NotNull(attr);
    }

    [Fact]
    public void PredictAttribute_HasCorrectUsage()
    {
        var usage = (AttributeUsageAttribute?)Attribute.GetCustomAttribute(
            typeof(PredictAttribute), typeof(AttributeUsageAttribute));

        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Method, usage.ValidOn);
        Assert.False(usage.AllowMultiple);
        Assert.False(usage.Inherited);
    }

    #endregion

    #region Helper methods

    private static ModuleModel CreateModuleModelWithPredictMethods()
    {
        return new ModuleModel(
            Namespace: "TestApp",
            TypeName: "TestModule",
            PredictorFields: new EquatableArray<PredictorFieldModel>(ImmutableArray<PredictorFieldModel>.Empty),
            PredictMethods: new EquatableArray<PredictMethodModel>(ImmutableArray.Create(
                new PredictMethodModel("ClassifyAsync", "global::TestApp.TicketInput",
                    "global::TestApp.ClassifyTicket", "input"))));
    }

    private static ModuleModel CreateModuleModelWithMultiplePredictMethods()
    {
        return new ModuleModel(
            Namespace: "TestApp",
            TypeName: "TestModule",
            PredictorFields: new EquatableArray<PredictorFieldModel>(ImmutableArray<PredictorFieldModel>.Empty),
            PredictMethods: new EquatableArray<PredictMethodModel>(ImmutableArray.Create(
                new PredictMethodModel("ClassifyAsync", "global::TestApp.TicketInput",
                    "global::TestApp.ClassifyTicket", "input"),
                new PredictMethodModel("DraftAsync", "global::TestApp.ClassifyTicket",
                    "global::TestApp.DraftReply", "classification"))));
    }

    private static string GetPredictMethodPipelineSource() => """
        namespace Microsoft.Extensions.AI
        {
            public interface IChatClient { }
        }

        namespace LMP
        {
            public abstract class LmpModule
            {
                public virtual System.Collections.Generic.IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => System.Array.Empty<(string, IPredictor)>();
                protected virtual LmpModule CloneCore() => throw new System.NotSupportedException();
                protected Microsoft.Extensions.AI.IChatClient? Client { get; set; }
                public Trace? Trace { get; set; }
            }
            public interface IPredictor { IPredictor Clone(); }
            public class Predictor<TInput, TOutput> : IPredictor where TOutput : class
            {
                public Predictor(object client) { }
                public System.Threading.Tasks.Task<TOutput> PredictAsync(TInput input, Trace? trace = null) => throw new System.NotImplementedException();
                public IPredictor Clone() => this;
            }
            public class Trace { }
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class PredictAttribute : System.Attribute { }
        }

        namespace TestApp
        {
            using LMP;

            public record TicketInput(string TicketText);
            public record ClassifyTicket { public required string Category { get; init; } }

            public partial class PredictModule : LmpModule
            {
                [Predict]
                public partial System.Threading.Tasks.Task<ClassifyTicket> ClassifyAsync(TicketInput input);

                public override System.Threading.Tasks.Task<object> ForwardAsync(
                    object input, System.Threading.CancellationToken ct = default)
                    => System.Threading.Tasks.Task.FromResult<object>(null!);
            }
        }
        """;

    private static string GetMultiplePredictMethodPipelineSource() => """
        namespace Microsoft.Extensions.AI
        {
            public interface IChatClient { }
        }

        namespace LMP
        {
            public abstract class LmpModule
            {
                public virtual System.Collections.Generic.IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => System.Array.Empty<(string, IPredictor)>();
                protected virtual LmpModule CloneCore() => throw new System.NotSupportedException();
                protected Microsoft.Extensions.AI.IChatClient? Client { get; set; }
                public Trace? Trace { get; set; }
            }
            public interface IPredictor { IPredictor Clone(); }
            public class Predictor<TInput, TOutput> : IPredictor where TOutput : class
            {
                public Predictor(object client) { }
                public System.Threading.Tasks.Task<TOutput> PredictAsync(TInput input, Trace? trace = null) => throw new System.NotImplementedException();
                public IPredictor Clone() => this;
            }
            public class Trace { }
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class PredictAttribute : System.Attribute { }
        }

        namespace TestApp
        {
            using LMP;

            public record TicketInput(string TicketText);
            public record ClassifyTicket { public required string Category { get; init; } }
            public record DraftReply { public required string Text { get; init; } }

            public partial class MultiPredictModule : LmpModule
            {
                [Predict]
                public partial System.Threading.Tasks.Task<ClassifyTicket> ClassifyAsync(TicketInput input);

                [Predict]
                public partial System.Threading.Tasks.Task<DraftReply> DraftAsync(ClassifyTicket classification);

                public override System.Threading.Tasks.Task<object> ForwardAsync(
                    object input, System.Threading.CancellationToken ct = default)
                    => System.Threading.Tasks.Task.FromResult<object>(null!);
            }
        }
        """;

    private static string GetMixedPipelineSource() => """
        namespace Microsoft.Extensions.AI
        {
            public interface IChatClient { }
        }

        namespace LMP
        {
            public abstract class LmpModule
            {
                public virtual System.Collections.Generic.IReadOnlyList<(string Name, IPredictor Predictor)> GetPredictors() => System.Array.Empty<(string, IPredictor)>();
                protected virtual LmpModule CloneCore() => throw new System.NotSupportedException();
                protected Microsoft.Extensions.AI.IChatClient? Client { get; set; }
                public Trace? Trace { get; set; }
            }
            public interface IPredictor { IPredictor Clone(); }
            public class Predictor<TInput, TOutput> : IPredictor where TOutput : class
            {
                public Predictor(object client) { }
                public System.Threading.Tasks.Task<TOutput> PredictAsync(TInput input, Trace? trace = null) => throw new System.NotImplementedException();
                public IPredictor Clone() => this;
            }
            public class Trace { }
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class PredictAttribute : System.Attribute { }
        }

        namespace TestApp
        {
            using LMP;

            public record TicketInput(string TicketText);
            public record ClassifyTicket { public required string Category { get; init; } }
            public record DraftReply { public required string Text { get; init; } }

            public partial class MixedModule : LmpModule
            {
                private readonly Predictor<TicketInput, ClassifyTicket> _classify;

                public MixedModule(object client)
                {
                    _classify = new Predictor<TicketInput, ClassifyTicket>(client);
                }

                [Predict]
                public partial System.Threading.Tasks.Task<DraftReply> DraftAsync(ClassifyTicket classification);

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
