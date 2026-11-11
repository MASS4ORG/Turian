namespace Turian.Tests;

/// <summary>Qualifies the application, session and scene lifetimes and the subsystems they own.</summary>
public sealed class SubsystemLifetimeTests
{
    class Probe(string name, List<string> calls, bool failStart = false) : ISubsystem, IDisposable
    {
        public void Start()
        {
            calls.Add(name + ":start");
            if (failStart) { throw new InvalidOperationException("Start failed."); }
        }
        public void Stop() => calls.Add(name + ":stop");
        public void Dispose() => calls.Add(name + ":dispose");
    }

    sealed class A(List<string> calls) : Probe("a", calls);
    sealed class B(List<string> calls, bool fail = false) : Probe("b", calls, fail);
    sealed class C(List<string> calls) : Probe("c", calls);

    sealed class Progress : ISubsystem
    {
        public int Score { get; set; }
        public void Start() { }
        public void Stop() { }
    }

    sealed class Level(Progress progress) : ISubsystem
    {
        public Progress Progress { get; } = progress;
        public void Start() { }
        public void Stop() { }
    }

    sealed class Mover : ITickingSubsystem
    {
        public SubsystemTickPhase TickPhase => SubsystemTickPhase.Fixed;
        public int Ticks { get; private set; }
        public void Start() { }
        public void Stop() { }
        public void Tick(float deltaTime) => Ticks++;
    }

    sealed class Painter : ITickingSubsystem
    {
        public SubsystemTickPhase TickPhase => SubsystemTickPhase.Frame;
        public int Ticks { get; private set; }
        public void Start() { }
        public void Stop() { }
        public void Tick(float deltaTime) => Ticks++;
    }

    static SubsystemRegistry Registry(params SubsystemDescriptor[] descriptors) => new(descriptors);

    static SubsystemDescriptor ProgressAtSession() =>
        SubsystemDescriptor.Of(LifetimeScope.Session, static _ => new Progress());

    static SubsystemDescriptor LevelAtScene() =>
        SubsystemDescriptor.Of(LifetimeScope.Scene, static scope => new Level(scope.Get<Progress>()), typeof(Progress));

    /// <summary>Subsystems start after the ones they need, in any registration order, and stop in reverse.</summary>
    [Fact]
    public void DependenciesStartFirstAndStopLast()
    {
        var calls = new List<string>();
        var host = new LifetimeHost(Registry(
            SubsystemDescriptor.Of(LifetimeScope.Application, _ => new A(calls), typeof(B)),
            SubsystemDescriptor.Of(LifetimeScope.Application, _ => new B(calls), typeof(C)),
            SubsystemDescriptor.Of(LifetimeScope.Application, _ => new C(calls))));
        host.Start();
        host.Start();
        host.Dispose();
        host.Dispose();
        Assert.Equal(
            ["c:start", "b:start", "a:start", "a:stop", "a:dispose", "b:stop", "b:dispose", "c:stop", "c:dispose"],
            calls);
    }

    /// <summary>Reloading a scene replaces its subsystems while the session's state is kept.</summary>
    [Fact]
    public void SceneReloadKeepsSessionState()
    {
        using var host = new LifetimeHost(Registry(ProgressAtSession(), LevelAtScene()));
        host.Start();
        var session = host.BeginSession();
        var first = host.BeginScene();
        first.Get<Progress>().Score = 7;
        var firstLevel = first.Get<Level>();
        host.EndScene(first);

        var second = host.BeginScene();
        Assert.Same(session.Get<Progress>(), second.Get<Progress>());
        Assert.Equal(7, second.Get<Level>().Progress.Score);
        Assert.NotSame(firstLevel, second.Get<Level>());
    }

    /// <summary>Ending the session discards its state, and the next session starts clean.</summary>
    [Fact]
    public void EndingTheSessionDiscardsItsState()
    {
        using var host = new LifetimeHost(Registry(ProgressAtSession(), LevelAtScene()));
        host.Start();
        host.BeginSession().Get<Progress>().Score = 3;
        var scene = host.BeginScene();
        host.EndSession();

        Assert.Null(host.Session);
        Assert.Throws<InvalidOperationException>(() => scene.Get<Progress>());
        Assert.Equal(0, host.BeginSession().Get<Progress>().Score);
        Assert.Throws<InvalidOperationException>(() => host.BeginSession());
    }

    /// <summary>A subsystem a scope cannot see is reported by name, and probing for it returns false.</summary>
    [Fact]
    public void MissingSubsystemsAreReportedByName()
    {
        using var host = new LifetimeHost(Registry(ProgressAtSession()));
        host.Start();
        var error = Assert.Throws<InvalidOperationException>(() => host.Application.Get<Progress>());
        Assert.Contains(nameof(Progress), error.Message);
        Assert.False(host.Application.TryGet<Progress>(out _));
        Assert.True(host.BeginSession().TryGet<Progress>(out var progress));
        Assert.NotNull(progress);
    }

    /// <summary>Registration rejects repeats, unknown dependencies, cycles and dependencies on shorter lifetimes.</summary>
    [Fact]
    public void RegistryRejectsInvalidGraphs()
    {
        var calls = new List<string>();
        Assert.Throws<InvalidOperationException>(() => Registry(ProgressAtSession(), ProgressAtSession()));
        Assert.Contains("unregistered", Assert.Throws<InvalidOperationException>(() => Registry(LevelAtScene())).Message);
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => Registry(
            SubsystemDescriptor.Of(LifetimeScope.Scene, _ => new A(calls), typeof(B)),
            SubsystemDescriptor.Of(LifetimeScope.Scene, _ => new B(calls), typeof(A)))).Message);
        Assert.Contains("shorter-lived", Assert.Throws<InvalidOperationException>(() => Registry(
            SubsystemDescriptor.Of(LifetimeScope.Application, _ => new A(calls), typeof(B)),
            SubsystemDescriptor.Of(LifetimeScope.Scene, _ => new B(calls)))).Message);
    }

    /// <summary>A failed start disposes the failing subsystem and stops those already started.</summary>
    [Fact]
    public void FailedStartUnwinds()
    {
        var calls = new List<string>();
        var host = new LifetimeHost(Registry(
            SubsystemDescriptor.Of(LifetimeScope.Application, _ => new A(calls)),
            SubsystemDescriptor.Of(LifetimeScope.Application, _ => new B(calls, fail: true), typeof(A))));
        Assert.Throws<InvalidOperationException>(() => host.Start());
        Assert.Equal(["a:start", "b:start", "b:dispose", "a:stop", "a:dispose"], calls);
    }

    /// <summary>Ticking subsystems run only in their own phase.</summary>
    [Fact]
    public void TicksRunInTheirOwnPhase()
    {
        var mover = new Mover();
        var painter = new Painter();
        using var host = new LifetimeHost(Registry(
            SubsystemDescriptor.Of(LifetimeScope.Application, _ => mover),
            SubsystemDescriptor.Of(LifetimeScope.Application, _ => painter)));
        host.Start();
        host.Tick(SubsystemTickPhase.Fixed, 0.02f);
        host.Tick(SubsystemTickPhase.Fixed, 0.02f);
        host.Tick(SubsystemTickPhase.Frame, 0.016f);
        Assert.Equal((2, 1), (mover.Ticks, painter.Ticks));
    }

    /// <summary>Starting from another thread is refused, because subsystems activate on the main thread.</summary>
    [Fact]
    public void SubsystemsActivateOnTheMainThread()
    {
        using var host = new LifetimeHost(Registry(ProgressAtSession()));
        Exception? error = null;
        var other = new Thread(() =>
        {
            try { host.Start(); }
            catch (Exception ex) { error = ex; }
        });
        other.Start();
        other.Join();
        Assert.IsType<InvalidOperationException>(error);
    }

    /// <summary>Resolving a subsystem allocates nothing, including through parent scopes.</summary>
    [Fact]
    public void ResolutionAllocatesNothing()
    {
        using var host = new LifetimeHost(Registry(ProgressAtSession(), LevelAtScene()));
        host.Start();
        host.BeginSession();
        var scene = host.BeginScene();
        for (var i = 0; i < 100; i++) { scene.Get<Progress>(); }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            scene.Get<Progress>();
            scene.Get<Level>();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Modules register subsystems with the service collection and the runtime reads them back.</summary>
    [Fact]
    public void ModulesRegisterSubsystemsThroughServices()
    {
        var services = new ServiceCollection()
            .AddSubsystem(LifetimeScope.Session, static _ => new Progress())
            .AddSubsystem(LifetimeScope.Scene, static scope => new Level(scope.Get<Progress>()), typeof(Progress));
        using var provider = services.BuildServiceProvider();
        var registry = new SubsystemRegistry(provider.GetServices<SubsystemDescriptor>());
        Assert.Equal([typeof(Progress)], registry.For(LifetimeScope.Session).Select(static d => d.Type));
        Assert.Equal([typeof(Level)], registry.For(LifetimeScope.Scene).Select(static d => d.Type));
    }

    /// <summary>Disposing the host ends scenes, then the session, then the application.</summary>
    [Fact]
    public void DisposingTheHostEndsTheShortestLivedFirst()
    {
        var calls = new List<string>();
        var host = new LifetimeHost(Registry(
            SubsystemDescriptor.Of(LifetimeScope.Application, _ => new A(calls)),
            SubsystemDescriptor.Of(LifetimeScope.Session, _ => new B(calls)),
            SubsystemDescriptor.Of(LifetimeScope.Scene, _ => new C(calls))));
        host.Start();
        host.BeginSession();
        host.BeginScene();
        calls.Clear();
        host.Dispose();
        Assert.Equal(["c:stop", "c:dispose", "b:stop", "b:dispose", "a:stop", "a:dispose"], calls);
    }
}
