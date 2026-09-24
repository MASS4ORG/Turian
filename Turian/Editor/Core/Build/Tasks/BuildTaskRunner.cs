namespace Turian.Editor.Core;

/// <summary>
/// Executes <see cref="IBuildTask"/> instances sequentially on a background queue and
/// raises events that the UI (or console host) can subscribe to.
///
/// Tasks are serialized: a new task waits until the running one finishes.
/// UI-locking tasks additionally raise <see cref="UiLockChanged"/> so the host can
/// disable interactive controls for the duration.
/// </summary>
[PublicAPI]
public sealed class BuildTaskRunner : IDisposable
{
    readonly ILogger logger;
    readonly SemaphoreSlim gate = new(1, 1);
    CancellationTokenSource? currentCts;
    bool disposed;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fired when a task starts. Payload is the task name.</summary>
    public event Action<string>? TaskStarted;

    /// <summary>Fired when a task completes (success, failure, or cancellation).</summary>
    public event Action<BuildTaskStatus>? TaskCompleted;

    /// <summary>
    /// Fired when the UI-lock state changes.
    /// <c>true</c> = lock the UI; <c>false</c> = unlock.
    /// </summary>
    public event Action<bool>? UiLockChanged;

    /// <summary>
    /// When <c>true</c>, a UI-locking task is currently executing.
    /// </summary>
    public bool IsUiLocked { get; private set; }

    /// <summary>
    /// Creates a new <see cref="BuildTaskRunner"/>.
    /// </summary>
    /// <param name="logger"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public BuildTaskRunner(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    /// <summary>
    /// Enqueues <paramref name="task"/> for execution and returns immediately.
    /// The task runs asynchronously; subscribe to <see cref="TaskCompleted"/> for the result.
    /// </summary>
    public void Enqueue(IBuildTask task, CancellationToken externalToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = RunInternalAsync(task, externalToken);
    }

    /// <summary>
    /// Enqueues <paramref name="task"/> and returns a <see cref="Task{TResult}"/> that
    /// completes when the task finishes, carrying the <see cref="BuildTaskStatus"/>.
    /// </summary>
    public Task<BuildTaskStatus> EnqueueAsync(IBuildTask task, CancellationToken externalToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return RunInternalAsync(task, externalToken);
    }

    /// <summary>Requests cancellation of the currently running task, if any.</summary>
    public void CancelCurrent() => currentCts?.Cancel();

    // ── Internal ──────────────────────────────────────────────────────────────

    async Task<BuildTaskStatus> RunInternalAsync(IBuildTask task, CancellationToken externalToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(externalToken).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        currentCts = cts;

        if (task.LocksUi)
        {
            IsUiLocked = true;
            UiLockChanged?.Invoke(true);
        }

        TaskStarted?.Invoke(task.Name);
        logger.LogInformation("[BuildTask] Starting: {TaskName}", task.Name);

        BuildTaskStatus status;
        try
        {
            var result = await task.RunAsync(cts.Token).ConfigureAwait(false);
            status = new BuildTaskStatus(task.Name, BuildTaskState.Succeeded, result, task.LocksUi);
            logger.LogInformation("[BuildTask] Succeeded: {TaskName} – {Message}", task.Name, result);
        }
        catch (OperationCanceledException)
        {
            status = new BuildTaskStatus(task.Name, BuildTaskState.Cancelled, "Cancelled.", task.LocksUi);
            logger.LogWarning("[BuildTask] Cancelled: {TaskName}", task.Name);
        }
        catch (Exception ex)
        {
            status = new BuildTaskStatus(task.Name, BuildTaskState.Failed, ex.Message, task.LocksUi);
            logger.LogError(ex, "[BuildTask] Failed: {TaskName}", task.Name);
        }
        finally
        {
            currentCts = null;

            if (task.LocksUi)
            {
                IsUiLocked = false;
                UiLockChanged?.Invoke(false);
            }

            gate.Release();
        }

        TaskCompleted?.Invoke(status);
        return status;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        gate.Dispose();
    }
}
