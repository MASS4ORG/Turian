#pragma warning disable CA2012 // Exercises explicit registration, diagnostics and invalid consumption contracts.
#pragma warning disable xUnit1031 // The scheduler's loop thread must remain fixed while worker work is joined.
#pragma warning disable xUnit1051 // Exercises default and owner lifetimes independently of the test-run lifetime.

namespace Turian.Tests;

/// <summary>Verifies loop-thread dispatch, ordered drains and pooled operation consumption.</summary>
public sealed class GameLoopSchedulerTests
{
    /// <summary>A worker can complete before registration without moving the consumer off the loop thread.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MainThread_RegistrationRaceAlwaysDispatchesOnLoop(bool completeBeforeRegistration)
    {
        using var scheduler = new GameLoopScheduler();
        var loopThread = Environment.CurrentManagedThreadId;
        var resumedThread = 0;
        var wait = default(ValueTask);
        OnWorker(() => wait = scheduler.MainThread());
        if (completeBeforeRegistration) scheduler.DrainUpdate();

        OnWorker(() => wait.GetAwaiter().UnsafeOnCompleted(() =>
        {
            wait.GetAwaiter().GetResult();
            resumedThread = Environment.CurrentManagedThreadId;
        }));

        Assert.Equal(0, resumedThread);
        scheduler.DrainUpdate();
        Assert.Equal(loopThread, resumedThread);
        Assert.Equal(0, scheduler.OutstandingCount);
    }

    /// <summary>Posts preserve FIFO, isolate faults and defer newly posted work to another snapshot.</summary>
    [Fact]
    public void Post_SnapshotKeepsDrainingAfterFailure()
    {
        using var scheduler = new GameLoopScheduler();
        var order = new List<int>();
        scheduler.Post(() =>
        {
            order.Add(1);
            scheduler.Post(() => order.Add(4));
            throw new InvalidOperationException("script failure");
        });
        scheduler.Post(() => order.Add(2));
        scheduler.Post(() => order.Add(3));

        scheduler.DrainUpdate();
        Assert.Equal([1, 2, 3], order);
        scheduler.DrainUpdate();
        Assert.Equal([1, 2, 3, 4], order);
    }

    /// <summary>Ordinary Task awaits capture the gameplay context while the host's context is restored.</summary>
    [Fact]
    public void Context_ExternalTaskReturnsToGameThread()
    {
        using var scheduler = new GameLoopScheduler();
        var previous = SynchronizationContext.Current;
        var completion = new TaskCompletionSource();
        var resumedThread = 0;
        Task routine;
        using (scheduler.EnterContext()) routine = Routine();
        Assert.Same(previous, SynchronizationContext.Current);
        OnWorker(completion.SetResult);
        Assert.Equal(0, resumedThread);
        scheduler.DrainUpdate();
        Assert.True(routine.IsCompletedSuccessfully);
        Assert.Equal(Environment.CurrentManagedThreadId, resumedThread);

        async Task Routine()
        {
            await completion.Task;
            resumedThread = Environment.CurrentManagedThreadId;
        }
    }

    /// <summary>A frame wait cannot resume twice in one frame and retains registration order.</summary>
    [Fact]
    public void NextFrame_IsStrictAndFifoAcrossReuse()
    {
        using var scheduler = new GameLoopScheduler();
        var order = new List<int>();
        var first = Routine(1);
        var second = Routine(2);
        scheduler.DrainUpdate();
        Assert.Empty(order);
        Pump(scheduler);
        Assert.Equal([1, 2], order);
        scheduler.DrainUpdate();
        Assert.Equal([1, 2], order);
        Pump(scheduler);
        Assert.Equal([1, 2, 1, 2], order);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(second.IsCompletedSuccessfully);

        async Task Routine(int id)
        {
            await scheduler.NextFrame();
            order.Add(id);
            await scheduler.NextFrame();
            order.Add(id);
        }
    }

    /// <summary>A faulty predicate does not strand the following waiter or leak its result.</summary>
    [Fact]
    public void PredicateFailure_IsolatedFromOtherWaiters()
    {
        using var scheduler = new GameLoopScheduler();
        var failure = scheduler.Until(() => throw new InvalidOperationException("predicate")).AsTask();
        var following = scheduler.Until(() => true).AsTask();
        Pump(scheduler);
        Assert.True(following.IsCompletedSuccessfully);
        Assert.Equal("predicate", Assert.IsType<InvalidOperationException>(failure.Exception!.InnerException).Message);
        Assert.Equal(0, scheduler.OutstandingCount);
    }

    /// <summary>Dead owners are canceled immediately before their turn in the same snapshot.</summary>
    [Fact]
    public void OwnerDestroyedDuringDrain_UnwindsFollowingWaitAndSkipsPostedAction()
    {
        using var scheduler = new GameLoopScheduler();
        var owner = CreateOwner();
        var ran = false;
        var cleaned = false;
        scheduler.Post(owner.Detach);
        scheduler.Post(() => ran = true, owner);
        var routine = Routine();
        Pump(scheduler);
        Assert.False(ran);
        Assert.True(cleaned);
        Assert.True(routine.IsCanceled);
        Assert.Equal(0, scheduler.OutstandingCount);

        async Task Routine()
        {
            try
            {
                await scheduler.NextFrame(owner: owner);
                ran = true;
            }
            finally { cleaned = true; }
        }
    }

    /// <summary>Shutdown requested by a callback stops later actions and unwinds pending waits.</summary>
    [Fact]
    public void StopInsideDrain_CancelsAndRunsFinally()
    {
        using var scheduler = new GameLoopScheduler();
        var cleaned = false;
        var ran = false;
        var routine = Routine();
        scheduler.Post(scheduler.Dispose);
        scheduler.Post(() => ran = true);
        scheduler.DrainUpdate();
        Assert.True(scheduler.IsStopped);
        Assert.True(cleaned);
        Assert.False(ran);
        Assert.True(routine.IsCanceled);
        Assert.Equal(0, scheduler.OutstandingCount);

        async Task Routine()
        {
            try { await scheduler.Seconds(100); }
            finally { cleaned = true; }
        }
    }

    /// <summary>Old contexts cannot deliver work into a successor session.</summary>
    [Fact]
    public void StoppedContext_DoesNotFollowNewSession()
    {
        using var original = new GameLoopScheduler();
        SynchronizationContext context;
        using (original.EnterContext()) context = SynchronizationContext.Current!;
        original.Dispose();
        using var successor = new GameLoopScheduler();
        var ran = false;
        OnWorker(() => context.Post(_ => ran = true, null));
        successor.DrainUpdate();
        Assert.False(ran);
    }

    /// <summary>Inspection retains creation and owner details until a completed result is consumed.</summary>
    [Fact]
    public void Diagnostics_KeepCompletedUnconsumedOperations()
    {
        using var scheduler = new GameLoopScheduler { CaptureCreationStacks = true };
        var owner = CreateOwner();
        var wait = scheduler.NextFrame(owner: owner);
        var pending = Assert.Single(scheduler.InspectOutstanding());
        Assert.Equal("NextFrame", pending.Kind);
        Assert.Equal("Owner", pending.Node);
        Assert.False(pending.IsCompleted);
        Assert.Contains(nameof(Diagnostics_KeepCompletedUnconsumedOperations), pending.CreationStack);
        Pump(scheduler);
        Assert.True(Assert.Single(scheduler.InspectOutstanding()).IsCompleted);
        Assert.True(wait.IsCompletedSuccessfully);
        wait.GetAwaiter().GetResult();
        Assert.Empty(scheduler.InspectOutstanding());
    }

    /// <summary>Wait factories reject invalid times and phase pumps reject worker threads and recursive drains.</summary>
    [Fact]
    public void InvalidUsage_FailsWithoutCorruptingQueue()
    {
        using var scheduler = new GameLoopScheduler();
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Seconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Seconds(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.BeginFrame(double.PositiveInfinity, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.CompleteFixedStep(-1));
        Assert.Throws<ArgumentNullException>(() => scheduler.Until(null!));
        Assert.Throws<ArgumentNullException>(() => scheduler.Post(null!));
        OnWorker(() => Assert.Throws<InvalidOperationException>(scheduler.DrainUpdate));
        scheduler.Post(() => Assert.Throws<InvalidOperationException>(() => scheduler.BeginFrame(0, 0)));
        scheduler.DrainUpdate();
        scheduler.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scheduler.BeginFrame(0, 0));
        Assert.True(scheduler.NextFrame().IsCanceled);
    }

    internal static void Pump(GameLoopScheduler scheduler, double scaled = 1, double unscaled = 1)
    {
        scheduler.BeginFrame(scaled, unscaled);
        scheduler.DrainUpdate();
        scheduler.DrainLateUpdate();
    }

    internal static void OnWorker(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Worker did not return to the test.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
    }

    internal static TestOwner CreateOwner()
    {
        var owner = new TestOwner();
        var node = new Node { Name = "Owner" };
        node.AddComponent(owner);
        node.Awake(null);
        return owner;
    }

    internal sealed class TestOwner : Component;
}
