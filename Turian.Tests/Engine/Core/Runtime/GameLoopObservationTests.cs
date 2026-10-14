namespace Turian.Tests;

/// <summary>Checks fault observation, shutdown diagnostics and synchronization-context boundaries.</summary>
public sealed class GameLoopObservationTests
{
    /// <summary>A worker's fault is queued for reporting on the loop and is consumed exactly once.</summary>
    [Fact]
    public void Forget_ObservesWorkerFailureAndUnexpectedCancellation()
    {
        using var scheduler = new GameLoopScheduler();
        var completion = new TaskCompletionSource();
        scheduler.Forget(new ValueTask(completion.Task));
        GameLoopSchedulerTests.OnWorker(() => completion.SetException(new InvalidOperationException("worker")));
        scheduler.DrainUpdate();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        scheduler.Forget(ValueTask.FromCanceled(cancellation.Token));
        scheduler.Dispose();
        var late = new TaskCompletionSource();
        scheduler.Forget(new ValueTask(late.Task));
        GameLoopSchedulerTests.OnWorker(() => late.SetException(new InvalidOperationException("late worker")));
    }

    /// <summary>Shutdown reports unconsumed waits and continues despite throwing cancellation registrations.</summary>
    [Fact]
    public void Shutdown_RetainsUnconsumedDiagnosticUntilConsumerUnwinds()
    {
        using var scheduler = new GameLoopScheduler();
        using var registration = scheduler.SessionCancellationToken.Register(
            static () => throw new InvalidOperationException("cancellation callback"));
        var wait = scheduler.NextFrame(TestContext.Current.CancellationToken);
        scheduler.Dispose();
        Assert.True(Assert.Single(scheduler.InspectOutstanding()).IsCompleted);
        Assert.True(wait.AsTask().IsCanceled);
        Assert.Equal(0, scheduler.OutstandingCount);
    }

    /// <summary>Send stays synchronous on the loop, rejects worker blocking and rejects an ended session.</summary>
    [Fact]
    public void Context_SendAndCopyStayBoundToSession()
    {
        using var scheduler = new GameLoopScheduler();
        SynchronizationContext context;
        using (scheduler.EnterContext())
        {
            context = SynchronizationContext.Current!;
        }

        Assert.Same(context, context.CreateCopy());
        var invoked = false;
        context.Send(_ => invoked = true, null);
        Assert.True(invoked);
        GameLoopSchedulerTests.OnWorker(() =>
            Assert.Throws<InvalidOperationException>(() => context.Send(_ => { }, null)));
        scheduler.Dispose();
        Assert.Throws<ObjectDisposedException>(() => context.Send(_ => { }, null));
    }

    /// <summary>Owned routines receive a root token canceled by the session as well as by component destruction.</summary>
    [Fact]
    public void OwnedRoutine_RootLifetimeIncludesSessionAndRejectsStoppedStarts()
    {
        using var scheduler = new GameLoopScheduler();
        var owner = GameLoopSchedulerTests.CreateOwner();
        var captured = default(CancellationToken);
        scheduler.RunOwned(token =>
        {
            captured = token;
            return scheduler.NextFrame(token, owner);
        }, owner);
        scheduler.Dispose();
        Assert.True(captured.IsCancellationRequested);
        Assert.False(owner.IsDestroyed);
        var started = false;
        scheduler.RunOwned(_ =>
        {
            started = true;
            return ValueTask.CompletedTask;
        }, owner);
        Assert.False(started);
    }

    /// <summary>Synchronous cancellation during routine startup is observed without leaking its linked lifetime.</summary>
    [Fact]
    public void OwnedRoutine_ObservesSynchronousLifetimeCancellation()
    {
        using var scheduler = new GameLoopScheduler();
        var owner = GameLoopSchedulerTests.CreateOwner();
        scheduler.RunOwned(token =>
        {
            owner.Detach();
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }, owner);
        Assert.True(owner.DestroyCancellationToken.IsCancellationRequested);
        Assert.Equal(0, scheduler.OutstandingCount);
    }
}
