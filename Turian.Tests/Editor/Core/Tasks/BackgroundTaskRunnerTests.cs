namespace Turian.Tests;

/// <summary>Expected background failures retain their status and explanation without exception logging.</summary>
public sealed class BackgroundTaskRunnerTests
{
    /// <summary>A returned failure stays failed after the progress scope closes and releases its locks.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReturnedOutcomesKeepTheirStatus(bool succeeds)
    {
        var tasks = new BackgroundTaskManager();
        var states = new System.Collections.Concurrent.ConcurrentQueue<BackgroundTaskStatus>();
        tasks.Changed += () =>
        {
            foreach (var task in tasks.Snapshot())
            {
                states.Enqueue(task.Status);
            }
        };
        var logger = Substitute.For<ILogger>();
        var runner = new BackgroundTaskRunner(tasks, logger);
        var id = runner.RunWithResult(new BackgroundTaskSpec { Label = "Compile scripts", Locks = EditorLocks.Scripts },
            (_, _) => Task.FromResult(new BackgroundTaskResult(succeeds, "Check Bricks/manifest.json")));

        await WaitForCompletion(tasks, id);

        var task = tasks.Get(id)!;
        Assert.Equal(succeeds ? BackgroundTaskStatus.Completed : BackgroundTaskStatus.Failed, task.Status);
        if (!succeeds)
        {
            Assert.Equal("Check Bricks/manifest.json", task.Note);
            Assert.DoesNotContain(BackgroundTaskStatus.Completed, states);
        }
        Assert.Equal(EditorLocks.None, tasks.ActiveLocks);
        Assert.Empty(logger.ReceivedCalls());
    }

    /// <summary>Cancellation wins over a returned failure and does not leave work running.</summary>
    [Fact]
    public async Task CancelledOutcomeReleasesItsLocks()
    {
        var tasks = new BackgroundTaskManager();
        var runner = new BackgroundTaskRunner(tasks, NullLogger.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = runner.RunWithResult(new BackgroundTaskSpec { Label = "Compile scripts", Locks = EditorLocks.Scripts },
            async (_, _) =>
            {
                await release.Task;
                return new BackgroundTaskResult(false, "failed");
            });
        tasks.RequestCancel(id);
        release.SetResult();
        await WaitForCompletion(tasks, id);

        Assert.Equal(BackgroundTaskStatus.Cancelled, tasks.Get(id)!.Status);
        Assert.Equal(EditorLocks.None, tasks.ActiveLocks);
    }

    /// <summary>Coalescing retries the operation once without executing a duplicate submission's work.</summary>
    [Fact]
    public async Task CoalescedFailureRunsOneRetry()
    {
        var tasks = new BackgroundTaskManager();
        var runner = new BackgroundTaskRunner(tasks, NullLogger.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var spec = new BackgroundTaskSpec { Label = "Compile scripts", Policy = DuplicatePolicy.Coalesce };
        var id = runner.RunWithResult(spec, async (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            await release.Task;
            return new BackgroundTaskResult(false, "missing brick");
        });
        Assert.Equal(id, runner.RunWithResult(spec, (_, _) =>
            throw new InvalidOperationException("duplicate work must not run")));
        release.SetResult();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (Volatile.Read(ref attempts) < 2 || tasks.Get(id) is { IsActive: true })
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.Equal(2, attempts);
        Assert.Equal(BackgroundTaskStatus.Failed, tasks.Get(id)!.Status);
        Assert.Equal("missing brick", tasks.Get(id)!.Note);
    }

    /// <summary>The existing task API keeps normal completion, cancellation and unexpected exception behavior.</summary>
    [Theory]
    [InlineData("success", BackgroundTaskStatus.Completed)]
    [InlineData("cancel", BackgroundTaskStatus.Cancelled)]
    [InlineData("error", BackgroundTaskStatus.Failed)]
    public async Task ExistingTaskApiPreservesOutcomes(string outcome, BackgroundTaskStatus expected)
    {
        var tasks = new BackgroundTaskManager();
        var logger = Substitute.For<ILogger>();
        var runner = new BackgroundTaskRunner(tasks, logger);
        var status = await runner.RunAsync(new BackgroundTaskSpec { Label = "Import" }, (_, _) => outcome switch
        {
            "cancel" => Task.FromCanceled(new CancellationToken(true)),
            "error" => Task.FromException(new IOException("disk unavailable")),
            _ => Task.CompletedTask,
        });

        Assert.Equal(expected, status);
        Assert.Equal(expected, Assert.Single(tasks.Snapshot()).Status);
        Assert.Equal(outcome == "error", logger.ReceivedCalls().Any(static call => call.GetMethodInfo().Name == "Log"
            && call.GetArguments()[3] is IOException));
    }

    /// <summary>Background operations wait for required work and do not execute when that dependency failed.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DependenciesGateResultWork(bool succeeds)
    {
        var tasks = new BackgroundTaskManager();
        var runner = new BackgroundTaskRunner(tasks, NullLogger.Instance);
        var dependency = tasks.Begin(BackgroundTaskKind.Scan, "Resolve bricks");
        var ran = false;
        var id = runner.RunWithResult(new BackgroundTaskSpec { Label = "Compile scripts", DependsOn = [dependency] },
            (_, _) =>
            {
                ran = true;
                return Task.FromResult(new BackgroundTaskResult(true, "compiled"));
            });
        Assert.Equal(BackgroundTaskStatus.Blocked, tasks.Get(id)!.Status);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        if (succeeds)
        {
            tasks.Complete(dependency);
        }
        else
        {
            tasks.Fail(dependency, "missing brick");
        }
        await WaitForCompletion(tasks, id);

        Assert.Equal(succeeds, ran);
        Assert.Equal(succeeds ? BackgroundTaskStatus.Completed : BackgroundTaskStatus.Cancelled, tasks.Get(id)!.Status);
    }

    /// <summary>Existing synchronous and asynchronous submissions keep progress and finish successfully.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingSubmissionOverloadsKeepProgress(bool synchronous)
    {
        var tasks = new BackgroundTaskManager();
        var runner = new BackgroundTaskRunner(tasks, NullLogger.Instance);
        var calls = 0;
        void Work(IProgressSink progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            calls++;
            progress.Report(0.5f, "source.cs");
        }
        var spec = new BackgroundTaskSpec { Label = "Compile scripts" };
        var id = synchronous ? runner.Run(spec, Work) : runner.Run(spec, async (progress, ct) =>
        {
            await Task.Yield();
            Work(progress, ct);
        });
        await WaitForCompletion(tasks, id);

        Assert.Equal(1, calls);
        Assert.Equal(BackgroundTaskStatus.Completed, tasks.Get(id)!.Status);
        Assert.Equal("source.cs", tasks.Get(id)!.Note);
    }

    internal static async Task WaitForCompletion(BackgroundTaskManager tasks, long id)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (tasks.Get(id) is { IsActive: true })
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
