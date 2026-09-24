namespace Turian.Tests.Editor;

/// <summary>
/// Covers the background task registry: duplicate policy, dependency gating, the capability locks
/// that gate individual editor actions, and the weighted rollup a compact display renders.
/// </summary>
public class BackgroundTaskTests
{
    readonly TestClock clock = new(DateTimeOffset.UnixEpoch);
    readonly BackgroundTaskManager tasks;

    /// <summary>Creates a manager on a controllable clock.</summary>
    public BackgroundTaskTests() => tasks = new BackgroundTaskManager(clock);

    /// <summary>A queued task flips to running as soon as it reports, so nothing sits at "Queued" while working.</summary>
    [Fact]
    public void QueuedTaskStartsOnItsFirstProgressReport()
    {
        var id = tasks.Enqueue(BackgroundTaskKind.Import, "Import textures");
        Assert.Equal(BackgroundTaskStatus.Queued, tasks.Get(id)!.Status);

        tasks.SetProgress(id, 0.25f, "atlas.png");

        var task = tasks.Get(id)!;
        Assert.Equal(BackgroundTaskStatus.Running, task.Status);
        Assert.Equal(0.25f, task.Progress);
        Assert.Equal("atlas.png", task.Note);
    }

    /// <summary>Item counters win over a reported fraction once set.</summary>
    [Fact]
    public void UnitCountersDriveTheFraction()
    {
        var id = tasks.Begin(BackgroundTaskKind.Import, "Import");
        tasks.SetProgress(id, 0.9f);
        tasks.SetUnits(id, 142, 1035);

        Assert.Equal(142f / 1035f, tasks.Get(id)!.Fraction, 4);
    }

    /// <summary>A burst of identical submissions costs one extra run, not one run per submission.</summary>
    [Fact]
    public void CoalescingReusesTheInFlightTaskAndFlagsOneRerun()
    {
        var spec = new BackgroundTaskSpec
        {
            Label = "Compile scripts",
            Kind = BackgroundTaskKind.Compile,
            Policy = DuplicatePolicy.Coalesce,
        };

        var first = tasks.Submit(spec, out var created);
        Assert.True(created);

        var second = tasks.Submit(spec, out var createdAgain);
        var third = tasks.Submit(spec, out _);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.False(createdAgain);
        Assert.Equal(1, tasks.TotalCount);

        // A burst of clicks costs exactly one extra run, not one per click.
        Assert.True(tasks.TakeRerunRequest(first));
        Assert.False(tasks.TakeRerunRequest(first));
    }

    /// <summary>Drop returns the in-flight id untouched; restart cancels it and queues a fresh task.</summary>
    [Fact]
    public void DropIgnoresTheDuplicateAndRestartCancelsTheOriginal()
    {
        var dropped = new BackgroundTaskSpec { Label = "Scan", Policy = DuplicatePolicy.Drop };
        var first = tasks.Submit(dropped);
        Assert.Equal(first, tasks.Submit(dropped));
        Assert.False(tasks.IsCancelRequested(first));

        var restart = new BackgroundTaskSpec { Label = "Build", Policy = DuplicatePolicy.Restart };
        var original = tasks.Submit(restart);
        var replacement = tasks.Submit(restart);

        Assert.NotEqual(original, replacement);
        Assert.True(tasks.IsCancelRequested(original));
    }

    /// <summary>Dedupe only applies to active tasks.</summary>
    [Fact]
    public void ADuplicateOfAFinishedTaskIsANewTask()
    {
        var spec = new BackgroundTaskSpec { Label = "Compile", Policy = DuplicatePolicy.Coalesce };
        var first = tasks.Submit(spec);
        tasks.Complete(first);

        Assert.NotEqual(first, tasks.Submit(spec));
    }

    /// <summary>A dependent stays blocked until its dependencies land, then becomes ready.</summary>
    [Fact]
    public void DependenciesGateAndCascade()
    {
        var import = tasks.Enqueue(BackgroundTaskKind.Import, "Import");
        var build = tasks.Submit(new BackgroundTaskSpec
        {
            Label = "Build",
            Kind = BackgroundTaskKind.Build,
            DependsOn = [import],
        });

        Assert.Equal(BackgroundTaskStatus.Blocked, tasks.Get(build)!.Status);
        Assert.Equal(import, tasks.NextReady());

        tasks.Complete(import);
        tasks.ResolveDependencies();

        Assert.Equal(BackgroundTaskStatus.Queued, tasks.Get(build)!.Status);
        // The import is finished, so the build is now the only thing ready to run.
        Assert.Equal(build, tasks.NextReady());
    }

    /// <summary>A build must not run on a half-imported project.</summary>
    [Fact]
    public void ABrokenDependencyCancelsTheDependent()
    {
        var import = tasks.Enqueue(BackgroundTaskKind.Import, "Import");
        var build = tasks.Submit(new BackgroundTaskSpec { Label = "Build", DependsOn = [import] });

        tasks.Fail(import, "disk full");
        tasks.ResolveDependencies();

        var task = tasks.Get(build)!;
        Assert.Equal(BackgroundTaskStatus.Cancelled, task.Status);
        Assert.Equal("Dependency did not finish", task.Note);
    }

    /// <summary>Capabilities are held only while their task is active, and unrelated ones stay free.</summary>
    [Fact]
    public void LocksAreUnionedAcrossActiveTasksOnly()
    {
        var compile = tasks.Submit(new BackgroundTaskSpec
        {
            Label = "Compile",
            Locks = EditorLocks.Scripts,
            StartImmediately = true,
        });
        var import = tasks.Submit(new BackgroundTaskSpec
        {
            Label = "Import",
            Locks = EditorLocks.Assets,
            StartImmediately = true,
        });

        Assert.Equal(EditorLocks.Scripts | EditorLocks.Assets, tasks.ActiveLocks);
        Assert.Equal("Compile", tasks.LockOwner(EditorLocks.Scripts)!.Label);

        // Scene editing stays available throughout: neither task holds it.
        Assert.Null(tasks.LockOwner(EditorLocks.Scene));

        tasks.Complete(compile);
        Assert.Equal(EditorLocks.Assets, tasks.ActiveLocks);
        tasks.Complete(import);
        Assert.Equal(EditorLocks.None, tasks.ActiveLocks);
    }

    /// <summary>Cancelling a build cancels the phases under it, however deep.</summary>
    [Fact]
    public void CancellingARootCascadesToEveryDescendant()
    {
        var root = tasks.Begin(BackgroundTaskKind.Build, "Build");
        var child = tasks.BeginChild(root, BackgroundTaskKind.Compile, "Compile");
        var grandchild = tasks.BeginChild(child, BackgroundTaskKind.Generic, "Emit");

        tasks.RequestCancel(root);

        Assert.True(tasks.IsCancelRequested(root));
        Assert.True(tasks.IsCancelRequested(child));
        Assert.True(tasks.IsCancelRequested(grandchild));
    }

    /// <summary>Successes fade from the list; failures stay until cleared, since nobody has read them.</summary>
    [Fact]
    public void CompletedRootsAreReclaimedAfterRetentionButFailuresAreSticky()
    {
        tasks.Retention = TimeSpan.FromSeconds(4);
        var ok = tasks.Begin(BackgroundTaskKind.Scan, "Scan");
        var bad = tasks.Begin(BackgroundTaskKind.Import, "Import");
        tasks.Complete(ok);
        tasks.Fail(bad, "unreadable");

        clock.Advance(TimeSpan.FromSeconds(5));
        tasks.Tick();

        Assert.Null(tasks.Get(ok));
        Assert.NotNull(tasks.Get(bad));

        tasks.ClearFinished();
        Assert.Equal(0, tasks.TotalCount);
    }

    /// <summary>Children do not outlive the root they roll up into.</summary>
    [Fact]
    public void ReclaimingARootAlsoDropsItsOrphanedChildren()
    {
        tasks.Retention = TimeSpan.FromSeconds(1);
        var root = tasks.Begin(BackgroundTaskKind.Build, "Build");
        var child = tasks.BeginChild(root, BackgroundTaskKind.Compile, "Compile");
        tasks.Complete(child);
        tasks.Complete(root);

        clock.Advance(TimeSpan.FromSeconds(2));
        tasks.Tick();

        Assert.Equal(0, tasks.TotalCount);
    }

    /// <summary>A phase contributes its declared share of the parent, not an equal one.</summary>
    [Fact]
    public void ProgressSinkChildrenRollUpByWeight()
    {
        var root = tasks.Begin(BackgroundTaskKind.Build, "Build");
        var sink = tasks.ProgressFor(root);
        sink.PlanChildren(10);

        var heavy = sink.BeginChild(BackgroundTaskKind.Compile, "Compile", 9);
        heavy.Report(0.5f);

        var rollup = BackgroundTaskTree.Rollup(tasks.Snapshot(), tasks.Get(root)!);

        // 9 * 0.5 over the *planned* 10, not over the 9 opened so far.
        Assert.Equal(0.45f, rollup.Progress, 4);
        Assert.Equal(BackgroundTaskStatus.Running, rollup.Status);
        Assert.Equal("Compile", rollup.ActiveChild!.Label);
    }

    /// <summary>Declaring the total up front keeps the aggregate monotonic as phases open.</summary>
    [Fact]
    public void PlannedWeightStopsTheBarWalkingBackwards()
    {
        var root = tasks.Begin(BackgroundTaskKind.Build, "Build");
        tasks.SetPlannedWeight(root, 4);

        var first = tasks.BeginChild(root, BackgroundTaskKind.Scan, "Scan");
        tasks.Complete(first);
        var afterFirst = BackgroundTaskTree.Rollup(tasks.Snapshot(), tasks.Get(root)!).Progress;

        tasks.BeginChild(root, BackgroundTaskKind.Compile, "Compile", 3);
        var afterSecond = BackgroundTaskTree.Rollup(tasks.Snapshot(), tasks.Get(root)!).Progress;

        Assert.Equal(0.25f, afterFirst, 4);
        Assert.True(afterSecond >= afterFirst, $"progress went backwards: {afterFirst} -> {afterSecond}");
    }

    /// <summary>A finished root knows outcomes its children cannot see.</summary>
    [Fact]
    public void AFinishedRootOverridesItsChildrensAggregate()
    {
        var root = tasks.Begin(BackgroundTaskKind.Build, "Build");
        var child = tasks.BeginChild(root, BackgroundTaskKind.Compile, "Compile");
        tasks.SetProgress(child, 0.3f);
        tasks.Complete(root);

        var rollup = BackgroundTaskTree.Rollup(tasks.Snapshot(), tasks.Get(root)!);

        Assert.Equal(BackgroundTaskStatus.Completed, rollup.Status);
        Assert.Equal(1, rollup.Progress);
    }

    /// <summary>The one row a compact bar affords goes to the most disruptive work.</summary>
    [Fact]
    public void PrimaryPicksTheMostDisruptiveActiveRoot()
    {
        tasks.Begin(BackgroundTaskKind.Scan, "Scan");
        var build = tasks.Begin(BackgroundTaskKind.Build, "Build");
        tasks.Begin(BackgroundTaskKind.Import, "Import");

        var snapshot = tasks.Snapshot();
        Assert.Equal(build, BackgroundTaskTree.Primary(snapshot)!.Id);
        Assert.Equal(3, BackgroundTaskTree.ActiveRoots(snapshot));
    }

    /// <summary>The compact line shows the most specific detail available.</summary>
    [Fact]
    public void DescribePrefersTheRunningChildThenUnitsThenTheNote()
    {
        var root = tasks.Begin(BackgroundTaskKind.Build, "Build");

        tasks.SetProgress(root, 0.1f, "preparing");
        Assert.Equal("Build: preparing", Describe(root));

        tasks.SetUnits(root, 3, 9);
        Assert.Equal("Build (3/9)", Describe(root));

        var child = tasks.BeginChild(root, BackgroundTaskKind.Compile, "Compile");
        tasks.SetUnits(child, 2, 5);
        Assert.Equal("Build — Compile (2/5)", Describe(root));
    }

    sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }

    string Describe(long id)
    {
        var snapshot = tasks.Snapshot();
        var task = tasks.Get(id)!;
        return BackgroundTaskTree.Describe(task, BackgroundTaskTree.Rollup(snapshot, task));
    }
}
