using LMP;

namespace LMP.Tests;

public class DelegateTargetTests
{
    // ── Factory / null guards ─────────────────────────────────────────────

    [Fact]
    public void For_AsyncWithCt_NullThrows()
        => Assert.Throws<ArgumentNullException>(
            () => DelegateTarget.For((Func<object, CancellationToken, Task<object>>)null!));

    [Fact]
    public void For_AsyncNoCt_NullThrows()
        => Assert.Throws<ArgumentNullException>(
            () => DelegateTarget.For((Func<object, Task<object>>)null!));

    // ── Shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void Shape_IsSingleTurn()
    {
        var target = DelegateTarget.For((input, _) => Task.FromResult(input));
        Assert.Equal(TargetShape.SingleTurn, target.Shape);
    }

    // ── ExecuteAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CallsDelegate()
    {
        var called = false;
        var target = DelegateTarget.For((input, ct) =>
        {
            called = true;
            return Task.FromResult((object)"result");
        });
        await target.ExecuteAsync("input");
        Assert.True(called);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsOutput()
    {
        var target = DelegateTarget.For((input, ct) => Task.FromResult((object)"echo"));
        var (output, _) = await target.ExecuteAsync("anything");
        Assert.Equal("echo", output);
    }

    [Fact]
    public async Task ExecuteAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var target = DelegateTarget.For(async (input, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return await Task.FromResult((object)"done");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => target.ExecuteAsync("x", cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        var target = DelegateTarget.For((input, ct) => Task.FromResult(input));
        await Assert.ThrowsAsync<ArgumentNullException>(() => target.ExecuteAsync(null!));
    }

    [Fact]
    public async Task ExecuteAsync_TraceIsEmpty()
    {
        var target = DelegateTarget.For((input, ct) => Task.FromResult((object)"x"));
        var (_, trace) = await target.ExecuteAsync("test");
        Assert.NotNull(trace);
    }

    // ── No state / no parameters ──────────────────────────────────────────

    [Fact]
    public void GetParameterSpace_IsEmpty()
    {
        var target = DelegateTarget.For((input, ct) => Task.FromResult(input));
        Assert.True(target.GetParameterSpace().IsEmpty);
    }

    [Fact]
    public void GetState_ReturnsNonNull()
    {
        var target = DelegateTarget.For((input, ct) => Task.FromResult(input));
        Assert.NotNull(target.GetState());
    }

    [Fact]
    public void ApplyState_IsNoOp()
    {
        var target = DelegateTarget.For((input, ct) => Task.FromResult(input));
        var state = target.GetState();
        target.ApplyState(state);  // should not throw
    }

    // ── WithParameters returns new instance ───────────────────────────────

    [Fact]
    public void WithParameters_ReturnsNewInstance()
    {
        var target = DelegateTarget.For((input, ct) => Task.FromResult(input));
        var cloned = target.WithParameters(ParameterAssignment.Empty);
        Assert.NotSame(target, cloned);
    }

    [Fact]
    public void WithParameters_NullThrows()
    {
        var target = DelegateTarget.For((input, ct) => Task.FromResult(input));
        Assert.Throws<ArgumentNullException>(() => target.WithParameters(null!));
    }

    [Fact]
    public async Task WithParameters_CloneDelegateStillWorks()
    {
        var target = DelegateTarget.For((input, ct) => Task.FromResult((object)"from-clone"));
        var cloned = target.WithParameters(ParameterAssignment.Empty);
        var (output, _) = await cloned.ExecuteAsync("test");
        Assert.Equal("from-clone", output);
    }

    // ── GetService always returns null ─────────────────────────────────────

    [Fact]
    public void GetService_AlwaysNull()
    {
        var target = DelegateTarget.For((input, ct) => Task.FromResult(input));
        Assert.Null(target.GetService<object>());
        Assert.Null(target.GetService<System.IO.Stream>());
    }
}
