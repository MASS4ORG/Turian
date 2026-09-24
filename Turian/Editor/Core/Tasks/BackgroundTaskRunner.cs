namespace Turian.Editor.Core;

/// <summary>
/// Runs submitted work on the thread pool and keeps its <see cref="BackgroundTaskManager"/> entry in
/// step: dependencies are awaited, cancel requests surface as a <see cref="CancellationToken"/>, and
/// the task lands as completed, failed or cancelled whatever the operation does.
/// </summary>
/// <param name="tasks">The registry the work reports into.</param>
/// <param name="logger">Logger for failures the UI only summarises.</param>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class BackgroundTaskRunner(BackgroundTaskManager tasks, ILogger logger)
{
    static readonly TimeSpan pollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How often a running task is polled for a cancel request.</summary>
    public TimeSpan CancelPollInterval { get; init; } = pollInterval;

    /// <summary>
    /// Submits <paramref name="spec"/> and runs <paramref name="work"/> for it in the background.
    /// A submission that coalesced onto an in-flight task does not start a second run.
    /// </summary>
    /// <param name="spec">What to track.</param>
    /// <param name="work">The operation, given a progress sink and a cancellation token.</param>
    /// <returns>The task id to watch.</returns>
    public long Run(BackgroundTaskSpec spec, Func<IProgressSink, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(work);

        var id = tasks.Submit(spec, out var created);
        if (created) _ = Task.Run(() => ExecuteAsync(id, spec, work));
        return id;
    }

    /// <summary>Submits and runs a synchronous operation in the background.</summary>
    /// <param name="spec">What to track.</param>
    /// <param name="work">The operation, given a progress sink and a cancellation token.</param>
    /// <returns>The task id to watch.</returns>
    public long Run(BackgroundTaskSpec spec, Action<IProgressSink, CancellationToken> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Run(spec, (sink, ct) =>
        {
            work(sink, ct);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Submits and runs <paramref name="work"/>, completing when the task reaches a terminal state.
    /// For callers that need the outcome rather than fire-and-forget.
    /// </summary>
    /// <param name="spec">What to track.</param>
    /// <param name="work">The operation, given a progress sink and a cancellation token.</param>
    /// <returns>The status the task landed in.</returns>
    public async Task<BackgroundTaskStatus> RunAsync(
        BackgroundTaskSpec spec, Func<IProgressSink, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(work);

        var id = tasks.Submit(spec, out var created);
        if (!created) return tasks.Get(id)?.Status ?? BackgroundTaskStatus.Completed;

        await ExecuteAsync(id, spec, work).ConfigureAwait(false);
        return tasks.Get(id)?.Status ?? BackgroundTaskStatus.Completed;
    }

    async Task ExecuteAsync(long id, BackgroundTaskSpec spec, Func<IProgressSink, CancellationToken, Task> work)
    {
        do
        {
            if (!await WaitForDependenciesAsync(id).ConfigureAwait(false)) return;

            tasks.Start(id);
            using var cts = new CancellationTokenSource();
            using var poller = PollForCancellation(id, cts);

            try
            {
                using var progress = tasks.ProgressFor(id);
                await work(progress, cts.Token).ConfigureAwait(false);
                if (tasks.IsCancelRequested(id)) tasks.Cancel(id);
                else tasks.Complete(id);
            }
            catch (OperationCanceledException)
            {
                tasks.Cancel(id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background task {Label} failed", spec.Label);
                tasks.Fail(id, ex.Message);
            }
        }
        while (tasks.TakeRerunRequest(id) && Restart(id));
    }

    bool Restart(long id)
    {
        // A coalesced duplicate arrived mid-flight: reuse the same entry for the extra run rather
        // than queueing one job per click.
        tasks.SetProgress(id, 0);
        tasks.Start(id);
        return tasks.Get(id) is not null;
    }

    async Task<bool> WaitForDependenciesAsync(long id)
    {
        while (tasks.Get(id) is { Status: BackgroundTaskStatus.Blocked })
        {
            tasks.ResolveDependencies();
            if (tasks.Get(id) is { Status: BackgroundTaskStatus.Blocked }) await Task.Delay(pollInterval).ConfigureAwait(false);
        }

        return tasks.Get(id) is { IsActive: true };
    }

    IDisposable PollForCancellation(long id, CancellationTokenSource cts) => new Timer(
        _ =>
        {
            if (tasks.IsCancelRequested(id)) CancelQuietly(cts);
        },
        null, CancelPollInterval, CancelPollInterval);

    static void CancelQuietly(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
