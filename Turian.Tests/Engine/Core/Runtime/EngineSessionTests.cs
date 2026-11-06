namespace Turian.Tests;

/// <summary>Qualifies feature composition and lifetime independently of rendering and optional features.</summary>
public sealed class EngineSessionTests
{
    sealed class Participant(string name, List<string> calls, bool failStart = false, bool failStop = false)
        : IEngineSessionParticipant
    {
        public void Start()
        {
            calls.Add(name + ":start");
            if (failStart) { throw new InvalidOperationException("Start failed."); }
        }
        public void Stop()
        {
            calls.Add(name + ":stop");
            if (failStop) { throw new InvalidOperationException("Stop failed."); }
        }
    }

    /// <summary>Features start once in registration order and stop once in reverse order.</summary>
    [Fact]
    public void FeatureLifetimesAreOrderedAndIdempotent()
    {
        var calls = new List<string>();
        var lifetime = new EngineSessionLifetime([new Participant("a", calls), new Participant("b", calls)]);
        lifetime.Start();
        lifetime.Start();
        lifetime.Dispose();
        lifetime.Dispose();
        Assert.Equal(["a:start", "b:start", "b:stop", "a:stop"], calls);
    }

    /// <summary>A failed startup unwinds both the failed feature and those already started.</summary>
    [Fact]
    public void FailedFeatureStartupUnwindsTheSession()
    {
        var calls = new List<string>();
        using var lifetime = new EngineSessionLifetime(
            [new Participant("a", calls), new Participant("b", calls, failStart: true)]);
        Assert.Throws<InvalidOperationException>(() => lifetime.Start());
        Assert.Equal(["a:start", "b:start", "b:stop", "a:stop"], calls);
    }

    /// <summary>A stop failure does not prevent the remaining features from releasing their resources.</summary>
    [Fact]
    public void FailedFeatureStopStillStopsOtherFeatures()
    {
        var calls = new List<string>();
        var lifetime = new EngineSessionLifetime(
            [new Participant("a", calls), new Participant("b", calls, failStop: true)]);
        lifetime.Start();
        Assert.Single(Assert.Throws<AggregateException>(() => lifetime.Dispose()).InnerExceptions);
        Assert.Equal(["a:start", "b:start", "b:stop", "a:stop"], calls);
        lifetime.Dispose();
    }

    /// <summary>Module registration can inspect session capabilities without constructing a provider.</summary>
    [Fact]
    public void ModulesReadTheRegisteredSessionContext()
    {
        var services = new ServiceCollection();
        Assert.Equal(EngineSessionKind.Headless, services.GetEngineSession().Kind);
        var context = new EngineSessionContext(EngineSessionKind.Editor, Interactive: true);
        services.AddSingleton(context);
        Assert.Same(context, services.GetEngineSession());
    }

    /// <summary>A renderer supplies host infrastructure without depending on optional audio or physics assemblies.</summary>
    [Fact]
    public void RenderHostHasNoFeatureAssemblyDependencies()
    {
        Assert.DoesNotContain(typeof(VulkanRuntimeHost).Assembly.GetReferencedAssemblies(), static reference =>
            reference.Name?.StartsWith("Turian.Audio", StringComparison.Ordinal) == true ||
            reference.Name?.StartsWith("Turian.Engine.Physics", StringComparison.Ordinal) == true);
        Assert.Null(GamePlayers.Find([typeof(Node).Assembly]));
        Assert.IsType<ClientPlayer>(GamePlayers.Find([typeof(VulkanRuntimeHost).Assembly]));
    }

    /// <summary>Failed host initialization can release input services before a native input context exists.</summary>
    [Fact]
    public void InputCanBeDisposedBeforeHostInitialization()
    {
        using var input = new InputManager(Substitute.For<ISurfaceHost>(), NullLogger.Instance);
        input.Dispose();
    }
}
