namespace Turian.Tests;

/// <summary>When the Turian bridge activates and releases the live plugins of editor assemblies.</summary>
public sealed class LivePluginBridgeTests : IDisposable
{
    readonly LivePluginHarness harness = new();

    /// <inheritdoc />
    public void Dispose() => harness.Dispose();

    /// <summary>A plugin activates once when its assembly joins and is released when it leaves.</summary>
    [Fact]
    public void APluginFollowsItsAssemblyInAndOutOfTheSet()
    {
        var assembly = harness.Load("Ui");

        harness.Apply(assembly);
        harness.Bridge.Sync();
        harness.Apply(assembly, typeof(string).Assembly);

        Assert.Equal(["on Ui"], harness.Trace);
        harness.Apply(typeof(string).Assembly);
        Assert.Equal(["on Ui", "off Ui"], harness.Trace);
    }

    /// <summary>A recompile releases the old assembly's plugin before the new one activates.</summary>
    [Fact]
    public void ARecompileSwapReleasesTheOldPluginAndActivatesTheNewOne()
    {
        var kept = harness.Load("Kept");
        harness.Apply(kept, harness.Load("Before"));

        harness.Apply(kept, harness.Load("After"));

        Assert.Equal(["on Kept", "on Before", "off Before", "on After"], harness.Trace);
    }

    /// <summary>Another project releases every lifetime, newest first, and activates what stays in the set.</summary>
    [Fact]
    public void AProjectSwitchRenewsEveryLifetime()
    {
        harness.Apply(harness.Load("One"), harness.Load("Two"));
        harness.Trace.Clear();

        harness.Project = "second";
        harness.Bridge.Sync();

        Assert.Equal(["off Two", "off One", "on One", "on Two"], harness.Trace);
    }

    /// <summary>Disposing releases everything once and ends the following.</summary>
    [Fact]
    public void DisposingReleasesEveryLifetimeOnce()
    {
        harness.Apply(harness.Load("Ui"));

        harness.Bridge.Dispose();
        harness.Bridge.Dispose();
        harness.Apply(harness.Load("Late"));

        Assert.Equal(["on Ui", "off Ui"], harness.Trace);
    }

    /// <summary>A plugin that fails to start or stop is reported without costing the others theirs.</summary>
    [Fact]
    public void AFailingPluginDoesNotStopTheOthers()
    {
        const string source = """
            public sealed class A_NoConstructor : Gaya.Sdk.ILivePlugin
            {
                public A_NoConstructor(int value) { }
                public System.IDisposable Activate(Gaya.Sdk.LivePluginContext context) => null;
            }

            public sealed class B_Throws : Gaya.Sdk.ILivePlugin
            {
                public System.IDisposable Activate(Gaya.Sdk.LivePluginContext context) =>
                    throw new System.InvalidOperationException("no");
            }

            public sealed class C_ReturnsNothing : Gaya.Sdk.ILivePlugin
            {
                public System.IDisposable Activate(Gaya.Sdk.LivePluginContext context) => null;
            }

            public sealed class D_ThrowsOnRelease : Gaya.Sdk.ILivePlugin, System.IDisposable
            {
                public System.IDisposable Activate(Gaya.Sdk.LivePluginContext context) => this;
                public void Dispose() => throw new System.InvalidOperationException("stuck");
            }

            public abstract class E_Abstract : Gaya.Sdk.ILivePlugin
            {
                public abstract System.IDisposable Activate(Gaya.Sdk.LivePluginContext context);
            }
            """;
        harness.Apply(harness.LoadSource(source), harness.Load("Good"));

        Assert.Equal(["on Good"], harness.Trace);
        var reported = new List<string>();
        while (harness.Notifications.TryTake(out var notification))
        {
            Assert.True(notification.IsError);
            reported.Add(notification.Message);
        }

        Assert.Equal(3, reported.Count);
        Assert.Contains(reported, static message => message.Contains("A_NoConstructor"));
        Assert.Contains(reported, static message => message.Contains("B_Throws"));
        Assert.Contains(reported, static message => message.Contains("C_ReturnsNothing"));

        harness.Apply();
        Assert.Equal(["on Good", "off Good"], harness.Trace);
    }

    /// <summary>The plugins of one assembly activate in name order and leave newest first.</summary>
    [Fact]
    public void AnAssemblysPluginsLeaveNewestFirst()
    {
        var both = harness.LoadSource(PluginAssemblies.Delegating("Alpha") + PluginAssemblies.Delegating("Beta"));

        harness.Apply(both);
        Assert.Equal(["on Alpha", "on Beta"], harness.Trace);

        harness.Apply();
        Assert.Equal(["on Alpha", "on Beta", "off Beta", "off Alpha"], harness.Trace);
    }

    /// <summary>An assembly whose types cannot all be read contributes the plugins that can.</summary>
    [Fact]
    public void AnUnreadableAssemblyIsContained()
    {
        var partial = Substitute.For<Assembly>();
        partial.GetTypes().Returns(static _ => throw new ReflectionTypeLoadException(
            [null], [new TypeLoadException("missing")]));
        var broken = Substitute.For<Assembly>();
        broken.GetName().Returns(new AssemblyName("Broken"));
        broken.GetTypes().Returns(static _ => throw new NotSupportedException("unreadable"));

        harness.Apply(partial, broken, harness.Load("Good"));

        Assert.Equal(["on Good"], harness.Trace);
    }
}
