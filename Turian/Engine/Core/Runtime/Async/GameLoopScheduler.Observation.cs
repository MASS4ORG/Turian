namespace Turian.Engine.Core;

public sealed partial class GameLoopScheduler
{
    /// <summary>Consumes an operation once and reports failures with its owner instead of abandoning its result.</summary>
    public void Forget(ValueTask operation, Component? owner = null)
        => _ = Observe(operation, owner, default, null);

    internal void RunOwned(Func<CancellationToken, ValueTask> routine, Component owner)
    {
        RequireMainThread();
        if (Cancellation(default, owner).IsCancellationRequested) return;
        using var contextScope = EnterContext();
        var lifetimeScope = CancellationTokenSource.CreateLinkedTokenSource(owner.DestroyCancellationToken,
            SessionCancellationToken);
        try { _ = Observe(routine(lifetimeScope.Token), owner, lifetimeScope.Token, lifetimeScope); }
        catch (OperationCanceledException) when (lifetimeScope.IsCancellationRequested) { lifetimeScope.Dispose(); }
        catch (Exception ex)
        {
            lifetimeScope.Dispose();
            Report(ex, owner);
        }
    }

    async Task Observe(ValueTask operation, Component? owner, CancellationToken cancellationToken,
        CancellationTokenSource? lifetimeScope)
    {
        var nodeName = owner?.Node?.Name;
        try { await operation.ConfigureAwait(false); }
        catch (Exception ex) { ObserveFailure(ex, owner, cancellationToken, nodeName); }
        finally { lifetimeScope?.Dispose(); }
    }

    void ObserveFailure(Exception exception, Component? owner, CancellationToken token, string? nodeName)
    {
        if (exception is OperationCanceledException && IsExpectedCancellation(token, owner)) return;
        if (IsMainThread || IsStopped) Report(exception, owner, nodeName);
        else Post(static state =>
        {
            var failure = ((Exception Exception, Component? Owner, string? Node))state!;
            Report(failure.Exception, failure.Owner, failure.Node);
        }, (exception, owner, nodeName));
    }

    bool IsExpectedCancellation(CancellationToken cancellationToken, Component? owner)
        => cancellationToken.IsCancellationRequested || IsStopped || IsDestroyed(owner) ||
           owner?.DestroyCancellationToken.IsCancellationRequested == true;
}
