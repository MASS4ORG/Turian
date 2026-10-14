#pragma warning disable xUnit1031 // The clock and scheduler stay on one thread while worker cancellation is joined.
#pragma warning disable xUnit1051 // Clock and owner tests deliberately exercise their own cancellation lifetimes.

namespace Turian.Tests;

/// <summary>Checks presentation and fixed clocks, pause cancellation and component-owned waits.</summary>
public sealed class GameLoopClockTests
{
    sealed class ManualTime : TimeProvider
    {
        long timestamp;
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => timestamp;
        internal void Advance(double seconds) => timestamp += (long)(seconds * TimestampFrequency);
    }

    /// <summary>Scaled, unscaled and realtime deadlines have distinct behavior across pause and time scale zero.</summary>
    [Fact]
    public void Clocks_DistinguishScalePauseAndMonotonicTime()
    {
        var time = new ManualTime();
        using var scheduler = new GameLoopScheduler(time);
        var scaled = scheduler.Seconds(2).AsTask();
        var unscaled = scheduler.UnscaledSeconds(2).AsTask();
        var realtime = scheduler.RealtimeSeconds(2).AsTask();
        GameLoopSchedulerTests.Pump(scheduler, 0, 1);
        Assert.False(scaled.IsCompleted);
        Assert.False(unscaled.IsCompleted);
        Assert.False(realtime.IsCompleted);
        time.Advance(1800);
        scheduler.PumpCancellation();
        Assert.False(realtime.IsCompleted);
        GameLoopSchedulerTests.Pump(scheduler, 0, 1);
        Assert.True(unscaled.IsCompletedSuccessfully);
        Assert.True(realtime.IsCompletedSuccessfully);
        Assert.False(scaled.IsCompleted);
        GameLoopSchedulerTests.Pump(scheduler, 2, 1);
        Assert.True(scaled.IsCompletedSuccessfully);
        Assert.Equal(2, scheduler.ElapsedSeconds);
        Assert.Equal(3, scheduler.UnscaledElapsedSeconds);
    }

    /// <summary>Fixed waits can resume between catch-up steps while presentation waits wait until updates finish.</summary>
    [Fact]
    public void CatchUp_FixedWaitsResumeAtFirstEligibleTick()
    {
        using var scheduler = new GameLoopScheduler();
        var order = new List<string>();
        var routine = FixedRoutine();
        var seconds = scheduler.Seconds(0.03).AsTask();
        scheduler.BeginFrame(0.1, 0.1);
        scheduler.CompleteFixedStep(0.02);
        Assert.Equal(["fixed:1"], order);
        Assert.False(seconds.IsCompleted);
        scheduler.CompleteFixedStep(0.02);
        Assert.Equal(["fixed:1", "fixed:2"], order);
        scheduler.CompleteFixedStep(0.02);
        Assert.Equal(["fixed:1", "fixed:2", "deadline:3"], order);
        Assert.True(routine.IsCompletedSuccessfully);
        scheduler.DrainUpdate();
        Assert.True(seconds.IsCompletedSuccessfully);

        async Task FixedRoutine()
        {
            await scheduler.FixedUpdate();
            order.Add($"fixed:{scheduler.FixedIndex}");
            await scheduler.FixedUpdate();
            order.Add($"fixed:{scheduler.FixedIndex}");
            await scheduler.FixedSeconds(0.01);
            order.Add($"deadline:{scheduler.FixedIndex}");
        }
    }

    /// <summary>Zero delays and predicates run only in their phase, and polling does not happen at creation.</summary>
    [Fact]
    public void Phases_DoNotInvokePredicatesDuringRegistration()
    {
        using var scheduler = new GameLoopScheduler();
        var polls = 0;
        var condition = true;
        var until = scheduler.Until(() => ++polls == 2).AsTask();
        var whileWait = scheduler.While(() => condition).AsTask();
        var zero = scheduler.Seconds(0).AsTask();
        var late = scheduler.LateUpdate().AsTask();
        Assert.Equal(0, polls);
        scheduler.BeginFrame(0, 0);
        scheduler.DrainUpdate();
        Assert.Equal(1, polls);
        Assert.False(until.IsCompleted);
        Assert.False(whileWait.IsCompleted);
        Assert.False(late.IsCompleted);
        Assert.True(zero.IsCompletedSuccessfully);
        scheduler.DrainUpdate();
        Assert.Equal(1, polls);
        scheduler.DrainLateUpdate();
        Assert.True(late.IsCompletedSuccessfully);
        condition = false;
        GameLoopSchedulerTests.Pump(scheduler);
        Assert.True(until.IsCompletedSuccessfully);
        Assert.True(whileWait.IsCompletedSuccessfully);
    }

    /// <summary>A paused cancellation pump unwinds waits without advancing clocks or executing gameplay predicates.</summary>
    [Fact]
    public void Cancellation_PumpsWhilePausedWithoutGameplay()
    {
        using var scheduler = new GameLoopScheduler();
        using var cancellation = new CancellationTokenSource();
        var polls = 0;
        var wait = scheduler.Until(() => ++polls > 10, cancellation.Token).AsTask();
        GameLoopSchedulerTests.OnWorker(cancellation.Cancel);
        Assert.False(wait.IsCompleted);
        scheduler.PumpCancellation();
        Assert.True(wait.IsCanceled);
        Assert.Equal(0, scheduler.FrameIndex);
        Assert.Equal(0, polls);
        Assert.True(scheduler.FixedUpdate(cancellation.Token).AsTask().IsCanceled);
        Assert.Equal(0, scheduler.OutstandingCount);
    }

    /// <summary>Owner helpers observe routines and destruction cancels every supported wait kind.</summary>
    [Fact]
    public void OwnerHelpers_CancelWaitsAndObserveRoutines()
    {
        using var scheduler = new GameLoopScheduler();
        using var services = new ServiceCollection().AddSingleton(scheduler).BuildServiceProvider();
        var owner = new GameLoopSchedulerTests.TestOwner();
        var node = new Node();
        node.AddComponent(owner);
        node.Awake(null, services);
        var waits = new[]
        {
            owner.Async.NextFrame().AsTask(), owner.Async.FixedUpdate().AsTask(), owner.Async.LateUpdate().AsTask(),
            owner.Async.Seconds(10).AsTask(), owner.Async.UnscaledSeconds(10).AsTask(),
            owner.Async.RealtimeSeconds(10).AsTask(), owner.Async.FixedSeconds(10).AsTask(),
            owner.Async.Until(() => false).AsTask(), owner.Async.While(() => true).AsTask(),
            owner.Async.MainThread().AsTask()
        };
        var cleaned = false;
        owner.Async.Run(async token =>
        {
            try { await owner.Async.NextFrame(token); }
            finally { cleaned = true; }
        });
        owner.Async.Run(_ => throw new InvalidOperationException("observed synchronous failure"));
        owner.Async.Run(async _ =>
        {
            await owner.Async.NextFrame();
            throw new InvalidOperationException("observed asynchronous failure");
        });
        owner.Detach();
        scheduler.PumpCancellation();
        Assert.True(cleaned);
        Assert.All(waits, wait => Assert.True(wait.IsCanceled));
        Assert.Equal(0, scheduler.OutstandingCount);
        Assert.Throws<InvalidOperationException>(() => owner.Async);
    }

    /// <summary>The ticker orders waits after their corresponding callbacks and advances fixed ticks individually.</summary>
    [Fact]
    public void SceneTicker_SharesSchedulerAndPhaseOrder()
    {
        using var scheduler = new GameLoopScheduler();
        var order = new List<string>();
        var component = new PhaseComponent(order);
        var root = new Node();
        root.AddComponent(component);
        root.Awake(null);
        var manager = new SceneManager(new AssetDatabase());
        manager.AdoptScene(Guid.NewGuid(), root);
        var ticker = new SceneTicker(manager) { Async = scheduler };
        scheduler.Forget(Waits());
        ticker.Tick(SceneTicker.FixedTimestep);
        Assert.Equal(["fixed", "fixed-wait", "update", "update-wait", "late", "late-wait"], order);
        ticker.Clock.IsPaused = true;
        var nextFixed = scheduler.FixedUpdate().AsTask();
        ticker.StepFrame();
        Assert.True(nextFixed.IsCompletedSuccessfully);

        async ValueTask Waits()
        {
            await scheduler.FixedUpdate();
            order.Add("fixed-wait");
            await scheduler.Seconds(0);
            order.Add("update-wait");
            await scheduler.LateUpdate();
            order.Add("late-wait");
        }
    }

    sealed class PhaseComponent(List<string> order) : Component
    {
        public override void OnFixedUpdate(float fixedDeltaTime) => order.Add("fixed");
        public override void OnUpdate(float deltaTime) => order.Add("update");
        public override void OnLateUpdate(float deltaTime) => order.Add("late");
    }
}
