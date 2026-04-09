using System.Text.Json;
using System.Text.Json.Serialization;

namespace LMP.Tests;

public class ExampleLoadFromJsonlTests : IDisposable
{
    private readonly string _tempDir;

    public ExampleLoadFromJsonlTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lmp-jsonl-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteJsonl(string fileName, params string[] lines)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllLines(path, lines);
        return path;
    }

    // === Simple types ===

    [Fact]
    public void LoadFromJsonl_SimpleTypes_ReturnsCorrectExamples()
    {
        var path = WriteJsonl("simple.jsonl",
            """{"input": "hello", "label": 42}""",
            """{"input": "world", "label": 99}""");

        var examples = Example.LoadFromJsonl<string, int>(path);

        Assert.Equal(2, examples.Count);
        Assert.Equal("hello", examples[0].Input);
        Assert.Equal(42, examples[0].Label);
        Assert.Equal("world", examples[1].Input);
        Assert.Equal(99, examples[1].Label);
    }

    // === Complex (record-like) types ===

    public record TicketInput(string Subject, string Plan);
    public record DraftReply(string Category, int Urgency);

    [Fact]
    public void LoadFromJsonl_ComplexTypes_DeserializesCorrectly()
    {
        var path = WriteJsonl("complex.jsonl",
            """{"input": {"subject": "I was charged twice", "plan": "Pro"}, "label": {"category": "billing", "urgency": 4}}""",
            """{"input": {"subject": "App crashed", "plan": "Free"}, "label": {"category": "bug", "urgency": 5}}""");

        var examples = Example.LoadFromJsonl<TicketInput, DraftReply>(path);

        Assert.Equal(2, examples.Count);
        Assert.Equal("I was charged twice", examples[0].Input.Subject);
        Assert.Equal("Pro", examples[0].Input.Plan);
        Assert.Equal("billing", examples[0].Label.Category);
        Assert.Equal(4, examples[0].Label.Urgency);
        Assert.Equal("App crashed", examples[1].Input.Subject);
        Assert.Equal("bug", examples[1].Label.Category);
    }

    // === Base type compatibility ===

    [Fact]
    public void LoadFromJsonl_ResultsAreExampleBase()
    {
        var path = WriteJsonl("base.jsonl",
            """{"input": "x", "label": 1}""");

        IReadOnlyList<Example<string, int>> examples = Example.LoadFromJsonl<string, int>(path);

        Example untyped = examples[0];
        Assert.Equal("x", untyped.WithInputs());
        Assert.Equal(1, untyped.GetLabel());
    }

    // === Empty file ===

    [Fact]
    public void LoadFromJsonl_EmptyFile_ReturnsEmptyList()
    {
        var path = WriteJsonl("empty.jsonl");

        var examples = Example.LoadFromJsonl<string, int>(path);

        Assert.Empty(examples);
    }

    // === Blank lines are skipped ===

    [Fact]
    public void LoadFromJsonl_SkipsBlankLines()
    {
        var path = WriteJsonl("blanks.jsonl",
            """{"input": "a", "label": 1}""",
            "",
            "   ",
            """{"input": "b", "label": 2}""");

        var examples = Example.LoadFromJsonl<string, int>(path);

        Assert.Equal(2, examples.Count);
        Assert.Equal("a", examples[0].Input);
        Assert.Equal("b", examples[1].Input);
    }

    // === PascalCase property names ===

    [Fact]
    public void LoadFromJsonl_PascalCaseKeys_DeserializesCorrectly()
    {
        var path = WriteJsonl("pascal.jsonl",
            """{"Input": "hello", "Label": 42}""");

        var examples = Example.LoadFromJsonl<string, int>(path);

        Assert.Single(examples);
        Assert.Equal("hello", examples[0].Input);
        Assert.Equal(42, examples[0].Label);
    }

    // === Custom JsonSerializerOptions ===

    [Fact]
    public void LoadFromJsonl_WithCustomOptions_UsesProvidedOptions()
    {
        var path = WriteJsonl("custom.jsonl",
            """{"input": {"Subject": "test", "Plan": "free"}, "label": {"Category": "bug", "Urgency": 3}}""");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var examples = Example.LoadFromJsonl<TicketInput, DraftReply>(path, options);

        Assert.Single(examples);
        Assert.Equal("test", examples[0].Input.Subject);
        Assert.Equal("bug", examples[0].Label.Category);
    }

    // === Error cases ===

    [Fact]
    public void LoadFromJsonl_NullPath_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            Example.LoadFromJsonl<string, int>(null!));
    }

    [Fact]
    public void LoadFromJsonl_FileNotFound_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() =>
            Example.LoadFromJsonl<string, int>(Path.Combine(_tempDir, "missing.jsonl")));
    }

    [Fact]
    public void LoadFromJsonl_MissingInputProperty_ThrowsFormatException()
    {
        var path = WriteJsonl("no-input.jsonl",
            """{"label": 42}""");

        var ex = Assert.Throws<FormatException>(() =>
            Example.LoadFromJsonl<string, int>(path));

        Assert.Contains("input", ex.Message);
        Assert.Contains("Line 1", ex.Message);
    }

    [Fact]
    public void LoadFromJsonl_MissingLabelProperty_ThrowsFormatException()
    {
        var path = WriteJsonl("no-label.jsonl",
            """{"input": "hello"}""");

        var ex = Assert.Throws<FormatException>(() =>
            Example.LoadFromJsonl<string, int>(path));

        Assert.Contains("label", ex.Message);
        Assert.Contains("Line 1", ex.Message);
    }

    [Fact]
    public void LoadFromJsonl_InvalidJson_ThrowsJsonException()
    {
        var path = WriteJsonl("invalid.jsonl",
            "not valid json");

        Assert.ThrowsAny<JsonException>(() =>
            Example.LoadFromJsonl<string, int>(path));
    }

    [Fact]
    public void LoadFromJsonl_NonObjectLine_ThrowsFormatException()
    {
        var path = WriteJsonl("array.jsonl",
            """[1, 2, 3]""");

        var ex = Assert.Throws<FormatException>(() =>
            Example.LoadFromJsonl<string, int>(path));

        Assert.Contains("JSON object", ex.Message);
    }

    [Fact]
    public void LoadFromJsonl_ErrorOnSecondLine_ReportsCorrectLineNumber()
    {
        var path = WriteJsonl("second-line.jsonl",
            """{"input": "ok", "label": 1}""",
            """{"input": "missing label"}""");

        var ex = Assert.Throws<FormatException>(() =>
            Example.LoadFromJsonl<string, int>(path));

        Assert.Contains("Line 2", ex.Message);
    }

    // === Numeric types ===

    [Fact]
    public void LoadFromJsonl_NumericInputAndLabel()
    {
        var path = WriteJsonl("numeric.jsonl",
            """{"input": 3.14, "label": true}""",
            """{"input": 2.71, "label": false}""");

        var examples = Example.LoadFromJsonl<double, bool>(path);

        Assert.Equal(2, examples.Count);
        Assert.Equal(3.14, examples[0].Input);
        Assert.True(examples[0].Label);
        Assert.Equal(2.71, examples[1].Input);
        Assert.False(examples[1].Label);
    }

    // === Large file ===

    [Fact]
    public void LoadFromJsonl_ManyLines_LoadsAll()
    {
        var lines = Enumerable.Range(0, 100)
            .Select(i => $$"""{"input": "item-{{i}}", "label": {{i}}}""")
            .ToArray();
        var path = WriteJsonl("large.jsonl", lines);

        var examples = Example.LoadFromJsonl<string, int>(path);

        Assert.Equal(100, examples.Count);
        Assert.Equal("item-0", examples[0].Input);
        Assert.Equal(0, examples[0].Label);
        Assert.Equal("item-99", examples[99].Input);
        Assert.Equal(99, examples[99].Label);
    }

    // === Case insensitive inner properties by default ===

    [Fact]
    public void LoadFromJsonl_CaseInsensitiveInnerProperties_ByDefault()
    {
        var path = WriteJsonl("case.jsonl",
            """{"input": {"SUBJECT": "test", "PLAN": "pro"}, "label": {"CATEGORY": "billing", "URGENCY": 1}}""");

        var examples = Example.LoadFromJsonl<TicketInput, DraftReply>(path);

        Assert.Single(examples);
        Assert.Equal("test", examples[0].Input.Subject);
        Assert.Equal("billing", examples[0].Label.Category);
    }

    // === Single line file ===

    [Fact]
    public void LoadFromJsonl_SingleLine_ReturnsSingleExample()
    {
        var path = WriteJsonl("single.jsonl",
            """{"input": "only one", "label": 7}""");

        var examples = Example.LoadFromJsonl<string, int>(path);

        Assert.Single(examples);
        Assert.Equal("only one", examples[0].Input);
        Assert.Equal(7, examples[0].Label);
    }
}
