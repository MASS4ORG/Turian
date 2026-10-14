#pragma warning disable CA2012 // These tests exercise invalid consumption of manually pumped values.
#pragma warning disable xUnit1031 // Results are inspected after explicitly pumping the same loop thread.
#pragma warning disable xUnit1051 // Exercises default tokens and source consumption independently of cancellation.

namespace Turian.Tests;

/// <summary>Verifies pooled leases reject duplicate consumers and stale handles.</summary>
public sealed class GameLoopConsumptionTests
{
    /// <summary>Results cannot be consumed early or twice, including after their source has been rented again.</summary>
    [Fact]
    public void Result_RejectsEarlyDuplicateAndStaleConsumption()
    {
        using var scheduler = new GameLoopScheduler();
        var first = scheduler.NextFrame();
        Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult());
        GameLoopSchedulerTests.Pump(scheduler);
        first.GetAwaiter().GetResult();
        Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult());
        var reused = scheduler.NextFrame();
        Assert.Throws<InvalidOperationException>(() => first.GetAwaiter().GetResult());
        GameLoopSchedulerTests.Pump(scheduler);
        reused.GetAwaiter().GetResult();
        Assert.Equal(0, scheduler.OutstandingCount);
    }

    /// <summary>A second pending registration is rejected without replacing the first callback.</summary>
    [Fact]
    public void Registration_RejectsSecondConsumer()
    {
        using var scheduler = new GameLoopScheduler();
        var wait = scheduler.NextFrame();
        var ran = false;
        wait.GetAwaiter().UnsafeOnCompleted(() =>
        {
            wait.GetAwaiter().GetResult();
            ran = true;
        });
        Assert.Throws<InvalidOperationException>(() => wait.GetAwaiter().UnsafeOnCompleted(() => { }));
        GameLoopSchedulerTests.Pump(scheduler);
        Assert.True(ran);
        Assert.Equal(0, scheduler.OutstandingCount);
    }

    /// <summary>A throwing manually registered continuation cannot abort other waits in the phase.</summary>
    [Fact]
    public void ThrowingContinuation_DoesNotAbortPhase()
    {
        using var scheduler = new GameLoopScheduler();
        var wait = scheduler.NextFrame();
        wait.GetAwaiter().UnsafeOnCompleted(() =>
        {
            wait.GetAwaiter().GetResult();
            throw new InvalidOperationException("continuation");
        });
        var following = scheduler.NextFrame().AsTask();
        GameLoopSchedulerTests.Pump(scheduler);
        Assert.True(following.IsCompletedSuccessfully);
        Assert.Equal(0, scheduler.OutstandingCount);
    }

    /// <summary>Late safe registration flows execution context and still invokes on the game loop.</summary>
    [Fact]
    public void SafeRegistration_FlowsExecutionContext()
    {
        using var scheduler = new GameLoopScheduler();
        var local = new AsyncLocal<string?> { Value = "registration" };
        var observed = "";
        var wait = scheduler.NextFrame();
        GameLoopSchedulerTests.Pump(scheduler);
        wait.GetAwaiter().OnCompleted(() =>
        {
            observed = local.Value;
            wait.GetAwaiter().GetResult();
        });
        local.Value = "drain";
        scheduler.DrainUpdate();
        Assert.Equal("registration", observed);
        Assert.Equal("drain", local.Value);
    }

    /// <summary>A dead owner overrides an already-ready success before the late consumer resumes.</summary>
    [Fact]
    public void ReadyResult_RechecksOwnerBeforeConsumption()
    {
        using var scheduler = new GameLoopScheduler();
        var owner = GameLoopSchedulerTests.CreateOwner();
        var wait = scheduler.NextFrame(owner: owner);
        GameLoopSchedulerTests.Pump(scheduler);
        owner.Detach();
        Assert.Throws<OperationCanceledException>(() => wait.GetAwaiter().GetResult());
        Assert.Equal(0, scheduler.OutstandingCount);
    }

    /// <summary>A completed wait registered during pause can unwind without resuming ordinary gameplay.</summary>
    [Fact]
    public void LateRegistration_CancellationPumpUnwindsReadyResult()
    {
        using var scheduler = new GameLoopScheduler();
        var owner = GameLoopSchedulerTests.CreateOwner();
        var wait = scheduler.NextFrame(owner: owner);
        GameLoopSchedulerTests.Pump(scheduler);
        var cleaned = false;
        wait.GetAwaiter().UnsafeOnCompleted(() =>
        {
            Assert.Throws<OperationCanceledException>(() => wait.GetAwaiter().GetResult());
            cleaned = true;
        });
        owner.Detach();
        scheduler.PumpCancellation();
        Assert.True(cleaned);
        Assert.Equal(0, scheduler.OutstandingCount);
    }
}
