using System.Text.Json;

namespace LMP.Tests;

// --- Test types for ProgramOfThought ---

public record MathInput(string Question);

public record MathResult
{
    public required int Answer { get; init; }
    public required string Explanation { get; init; }
}

public record StringInput(string Text);

public record StringResult
{
    public required string Output { get; init; }
}

// --- Tests ---

/// <summary>
/// Tests for Phase 8 — ProgramOfThought reasoning module.
/// Verifies that ProgramOfThought generates C# code via LM,
/// executes it via Roslyn scripting, and returns typed results.
/// </summary>
public class ProgramOfThoughtTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_ThrowsOnNullClient()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ProgramOfThought<MathInput, MathResult>(null!));
    }

    [Fact]
    public void Constructor_DefaultTimeout_Is30Seconds()
    {
        var client = new FakeChatClient();
        var pot = new ProgramOfThought<MathInput, MathResult>(client);
        Assert.Equal(TimeSpan.FromSeconds(30), pot.ExecutionTimeout);
    }

    [Fact]
    public void Constructor_CustomTimeout_IsPreserved()
    {
        var client = new FakeChatClient();
        var pot = new ProgramOfThought<MathInput, MathResult>(client,
            executionTimeout: TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), pot.ExecutionTimeout);
    }

    [Fact]
    public void Constructor_SetsCodeGeneratorInstructions()
    {
        var client = new FakeChatClient();
        var pot = new ProgramOfThought<MathInput, MathResult>(client);
        Assert.Contains("C# code generator", pot.CodeGenerator.Instructions);
        Assert.Contains("MathInput", pot.CodeGenerator.Instructions);
        Assert.Contains("MathResult", pot.CodeGenerator.Instructions);
    }

    #endregion

    #region ExecuteCodeAsync Tests

    [Fact]
    public async Task ExecuteCodeAsync_SimpleArithmetic_ReturnsResult()
    {
        var client = new FakeChatClient();
        var pot = new ProgramOfThought<MathInput, MathResult>(client);

        var code = """
            new LMP.Tests.MathResult { Answer = 42, Explanation = "The answer to everything" }
            """;

        var result = await pot.ExecuteCodeAsync(
            code, new MathInput("What is 6 * 7?"), CancellationToken.None);

        Assert.Equal(42, result.Answer);
        Assert.Equal("The answer to everything", result.Explanation);
    }

    [Fact]
    public async Task ExecuteCodeAsync_UsesInputGlobal()
    {
        var client = new FakeChatClient();
        var pot = new ProgramOfThought<StringInput, StringResult>(client);

        var code = """
            new LMP.Tests.StringResult { Output = Input.Text.ToUpper() }
            """;

        var result = await pot.ExecuteCodeAsync(
            code, new StringInput("hello world"), CancellationToken.None);

        Assert.Equal("HELLO WORLD", result.Output);
    }

    [Fact]
    public async Task ExecuteCodeAsync_CanUseLinq()
    {
        var client = new FakeChatClient();
        var pot = new ProgramOfThought<MathInput, MathResult>(client);

        var code = """
            var nums = Enumerable.Range(1, 10).ToList();
            var sum = nums.Sum();
            new LMP.Tests.MathResult { Answer = sum, Explanation = "Sum of 1 to 10" }
            """;

        var result = await pot.ExecuteCodeAsync(
            code, new MathInput("Sum of 1 to 10"), CancellationToken.None);

        Assert.Equal(55, result.Answer);
    }

    [Fact]
    public async Task ExecuteCodeAsync_FibonacciComputation()
    {
        var client = new FakeChatClient();
        var pot = new ProgramOfThought<MathInput, MathResult>(client);

        var code = """
            int Fib(int n) => n <= 1 ? n : Fib(n - 1) + Fib(n - 2);
            var answer = Fib(10);
            new LMP.Tests.MathResult { Answer = answer, Explanation = "10th Fibonacci number" }
            """;

        var result = await pot.ExecuteCodeAsync(
            code, new MathInput("10th Fibonacci?"), CancellationToken.None);

        Assert.Equal(55, result.Answer);
    }

    [Fact]
    public async Task ExecuteCodeAsync_CompilationError_Throws()
    {
        var client = new FakeChatClient();
        var pot = new ProgramOfThought<MathInput, MathResult>(client);

        var code = "this is not valid C# code!!!";

        await Assert.ThrowsAsync<Microsoft.CodeAnalysis.Scripting.CompilationErrorException>(
            () => pot.ExecuteCodeAsync(code, new MathInput("bad"), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteCodeAsync_RuntimeError_Throws()
    {
        var client = new FakeChatClient();
        var pot = new ProgramOfThought<MathInput, MathResult>(client);

        var code = """
            int[] arr = new int[0];
            var x = arr[5]; // IndexOutOfRangeException
            new LMP.Tests.MathResult { Answer = x, Explanation = "oops" }
            """;

        await Assert.ThrowsAsync<IndexOutOfRangeException>(
            () => pot.ExecuteCodeAsync(code, new MathInput("bad"), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteCodeAsync_NullReturn_ThrowsInvalidOperation()
    {
        var client = new FakeChatClient();
        var pot = new ProgramOfThought<MathInput, MathResult>(client);

        var code = """
            (LMP.Tests.MathResult)null
            """;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pot.ExecuteCodeAsync(code, new MathInput("null"), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteCodeAsync_Timeout_ThrowsTimeoutException()
    {
        var client = new FakeChatClient();
        var pot = new ProgramOfThought<MathInput, MathResult>(client,
            executionTimeout: TimeSpan.FromMilliseconds(100));

        // Infinite loop code
        var code = """
            while (true) { }
            new LMP.Tests.MathResult { Answer = 0, Explanation = "never" }
            """;

        await Assert.ThrowsAsync<TimeoutException>(
            () => pot.ExecuteCodeAsync(code, new MathInput("timeout"), CancellationToken.None));
    }

    #endregion

    #region PredictAsync End-to-End Tests

    [Fact]
    public async Task PredictAsync_SuccessfulCodeGen_ReturnsResult()
    {
        var client = new FakeChatClient();

        // The code gen predictor will get a CodeGenerationOutput
        client.EnqueueResponse(new CodeGenerationOutput
        {
            Reasoning = "I need to compute 6 * 7",
            Code = """
                new LMP.Tests.MathResult { Answer = 42, Explanation = "6 times 7" }
                """
        });

        var pot = new ProgramOfThought<MathInput, MathResult>(client);
        var result = await pot.PredictAsync(new MathInput("What is 6 * 7?"));

        Assert.Equal(42, result.Answer);
        Assert.Equal("6 times 7", result.Explanation);
    }

    [Fact]
    public async Task PredictAsync_RecordsTrace()
    {
        var client = new FakeChatClient();

        // Code gen response
        client.EnqueueResponse(new CodeGenerationOutput
        {
            Reasoning = "Simple math",
            Code = """
                new LMP.Tests.MathResult { Answer = 10, Explanation = "5+5" }
                """
        });

        var pot = new ProgramOfThought<MathInput, MathResult>(client);
        var trace = new Trace();
        var result = await pot.PredictAsync(new MathInput("5+5"), trace);

        Assert.Equal(10, result.Answer);
        // Trace should have the code gen step + the final result step
        Assert.True(trace.Entries.Count >= 1);
    }

    [Fact]
    public async Task PredictAsync_WithInputAccess_ReturnsCorrectResult()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new CodeGenerationOutput
        {
            Reasoning = "Reverse the input text",
            Code = """
                var reversed = new string(Input.Text.Reverse().ToArray());
                new LMP.Tests.StringResult { Output = reversed }
                """
        });

        var pot = new ProgramOfThought<StringInput, StringResult>(client);
        var result = await pot.PredictAsync(new StringInput("hello"));

        Assert.Equal("olleh", result.Output);
    }

    [Fact]
    public async Task PredictAsync_RetryOnCompilationError_SucceedsOnSecondAttempt()
    {
        var client = new FakeChatClient();

        // First attempt: bad code (will cause compilation error → LM retry)
        client.EnqueueResponse(new CodeGenerationOutput
        {
            Reasoning = "Let me try",
            Code = "this is not valid code!!!"
        });

        // Second attempt: good code
        client.EnqueueResponse(new CodeGenerationOutput
        {
            Reasoning = "Fixed it",
            Code = """
                new LMP.Tests.MathResult { Answer = 42, Explanation = "fixed" }
                """
        });

        var pot = new ProgramOfThought<MathInput, MathResult>(client);
        var result = await pot.PredictAsync(new MathInput("retry test"));

        Assert.Equal(42, result.Answer);
        Assert.Equal("fixed", result.Explanation);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task PredictAsync_AllRetriesFail_ThrowsMaxRetriesExceeded()
    {
        var client = new FakeChatClient();

        // All attempts return bad code
        for (int i = 0; i < 5; i++)
        {
            client.EnqueueResponse(new CodeGenerationOutput
            {
                Reasoning = "Trying again",
                Code = "invalid code!!!"
            });
        }

        var pot = new ProgramOfThought<MathInput, MathResult>(client);

        await Assert.ThrowsAsync<LmpMaxRetriesExceededException>(
            () => pot.PredictAsync(new MathInput("always fail")));
    }

    [Fact]
    public async Task PredictAsync_WithValidation_ReturnsValidResult()
    {
        var client = new FakeChatClient();

        client.EnqueueResponse(new CodeGenerationOutput
        {
            Reasoning = "Computing",
            Code = """
                new LMP.Tests.MathResult { Answer = 42, Explanation = "correct" }
                """
        });

        var pot = new ProgramOfThought<MathInput, MathResult>(client);
        var result = await pot.PredictAsync(
            new MathInput("test"),
            validate: r => LmpAssert.That(r, x => x.Answer > 0, "Answer must be positive"));

        Assert.Equal(42, result.Answer);
    }

    [Fact]
    public async Task PredictAsync_ValidationFails_Retries()
    {
        var client = new FakeChatClient();

        // First attempt: returns negative answer (fails validation)
        client.EnqueueResponse(new CodeGenerationOutput
        {
            Reasoning = "First try",
            Code = """
                new LMP.Tests.MathResult { Answer = -1, Explanation = "negative" }
                """
        });

        // Second attempt: returns positive answer (passes validation)
        client.EnqueueResponse(new CodeGenerationOutput
        {
            Reasoning = "Second try",
            Code = """
                new LMP.Tests.MathResult { Answer = 42, Explanation = "positive" }
                """
        });

        var pot = new ProgramOfThought<MathInput, MathResult>(client);
        var result = await pot.PredictAsync(
            new MathInput("test"),
            validate: r => LmpAssert.That(r, x => x.Answer > 0, "Answer must be positive"));

        Assert.Equal(42, result.Answer);
        Assert.Equal(2, client.CallCount);
    }

    #endregion

    #region Clone Tests

    [Fact]
    public void Clone_CreatesIndependentCopy()
    {
        var client = new FakeChatClient();
        var pot = new ProgramOfThought<MathInput, MathResult>(client);
        pot.Instructions = "Custom instructions";
        pot.Demos.Add((new MathInput("1+1"), new MathResult { Answer = 2, Explanation = "sum" }));

        var clone = (Predictor<MathInput, MathResult>)pot.Clone();

        Assert.NotSame(pot, clone);
        Assert.Equal("Custom instructions", clone.Instructions);
        Assert.Single(clone.Demos);

        // Modifying clone should not affect original
        clone.Demos.Clear();
        Assert.Single(pot.Demos);
    }

    #endregion

    #region CodeGenerationOutput Tests

    [Fact]
    public void CodeGenerationOutput_SerializesCorrectly()
    {
        var output = new CodeGenerationOutput
        {
            Reasoning = "Think step by step",
            Code = "1 + 1"
        };

        var json = JsonSerializer.Serialize(output);
        var deserialized = JsonSerializer.Deserialize<CodeGenerationOutput>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Think step by step", deserialized.Reasoning);
        Assert.Equal("1 + 1", deserialized.Code);
    }

    #endregion

    #region ScriptGlobals Tests

    [Fact]
    public void ScriptGlobals_ExposesInput()
    {
        var globals = new ScriptGlobals<MathInput> { Input = new MathInput("test") };
        Assert.Equal("test", globals.Input.Question);
    }

    #endregion
}
