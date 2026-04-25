using LMP;
using Microsoft.Extensions.AI;

namespace LMP.Tests;

/// <summary>
/// Tests for OptimizedChatClient tool filtering via UseOptimized.
/// </summary>
public class OptimizedChatClientToolFilterTests
{
    private static AIFunction MakeTool(string name)
        => AIFunctionFactory.Create(() => name, name: name);

    // ── UseOptimized(builder, state, toolPool) ────────────────────────────

    [Fact]
    public async Task UseOptimized_WithToolPool_FiltersToSelectedNames()
    {
        var search = MakeTool("search");
        var calc = MakeTool("calculator");
        var weather = MakeTool("weather");
        var toolPool = new List<AITool> { search, calc, weather };

        var state = new ChatClientState { SelectedToolNames = ["search", "calculator"] };
        var spy = new SpyChatClient("reply");

        var client = new ChatClientBuilder(spy)
            .UseOptimized(state, toolPool)
            .Build();

        await client.GetResponseAsync("hi");

        Assert.NotNull(spy.LastOptions?.Tools);
        var toolNames = spy.LastOptions!.Tools!.Select(t => t.Name).ToList();
        Assert.Equal(2, toolNames.Count);
        Assert.Contains("search", toolNames);
        Assert.Contains("calculator", toolNames);
        Assert.DoesNotContain("weather", toolNames);
    }

    [Fact]
    public async Task UseOptimized_NullToolPool_DoesNotTouchCallSiteTools()
    {
        var state = new ChatClientState { SelectedToolNames = ["search"] };
        var spy = new SpyChatClient("reply");

        var client = new ChatClientBuilder(spy)
            .UseOptimized(state, toolPool: null)
            .Build();

        var callOpts = new ChatOptions { Tools = [MakeTool("search"), MakeTool("other")] };
        await client.GetResponseAsync("hi", callOpts);

        // Without a pool the middleware must not touch tools — caller's 2 tools pass through
        Assert.NotNull(spy.LastOptions?.Tools);
        Assert.Equal(2, spy.LastOptions!.Tools!.Count);
    }

    [Fact]
    public async Task UseOptimized_EmptySelectedNames_DoesNotSetTools()
    {
        var search = MakeTool("search");
        var toolPool = new List<AITool> { search };

        // Empty (not null) — no tools were selected
        var state = new ChatClientState { SelectedToolNames = [] };
        var spy = new SpyChatClient("reply");

        var client = new ChatClientBuilder(spy)
            .UseOptimized(state, toolPool)
            .Build();

        await client.GetResponseAsync("hi");

        // FilterTools returns early when selected count is 0, so spy.LastOptions is null (no temp/prompt either)
        Assert.Null(spy.LastOptions?.Tools);
    }

    [Fact]
    public async Task UseOptimized_NullSelectedNames_DoesNotFilter()
    {
        var search = MakeTool("search");
        var toolPool = new List<AITool> { search };

        // Null means tool selection wasn't part of optimisation → leave options untouched
        var state = new ChatClientState { SelectedToolNames = null };
        var spy = new SpyChatClient("reply");

        var client = new ChatClientBuilder(spy)
            .UseOptimized(state, toolPool)
            .Build();

        await client.GetResponseAsync("hi");

        Assert.Null(spy.LastOptions?.Tools);
    }

    // ── UseOptimized(builder, result) extracts AllTools ──────────────────

    [Fact]
    public async Task UseOptimized_FromResult_ExtractsAllToolsFromChatClientTarget()
    {
        var search = MakeTool("search");
        var calc = MakeTool("calculator");

        var spy = new SpyChatClient("reply");
        var target = spy.AsOptimizationTarget(b => b.WithTools([search, calc]));

        // Simulate optimizer selecting only "search"
        var assignment = ParameterAssignment.Empty
            .With("tools", (IReadOnlyList<object>)[search]);
        var optimizedTarget = target.WithParameters(assignment);

        var result = new OptimizationResult
        {
            Target = optimizedTarget,
            BaselineScore = 0.5f,
            OptimizedScore = 0.7f,
            Trials = []
        };

        var client = new ChatClientBuilder(spy)
            .UseOptimized(result)
            .Build();

        await client.GetResponseAsync("hi");

        Assert.NotNull(spy.LastOptions?.Tools);
        var names = spy.LastOptions!.Tools!.Select(t => t.Name).ToList();
        Assert.Single(names);
        Assert.Equal("search", names[0]);
    }
}
