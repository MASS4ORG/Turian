namespace Turian.Tests;

/// <summary>Checks rendering-independent client startup and feature teardown without a window.</summary>
[Collection(SerialTests.Name)]
public sealed class ClientPlayerTests
{
    sealed class Feature(List<string> calls, bool failStart) : IApplicationParticipant
    {
        public void Start()
        {
            calls.Add("feature:start");
            if (failStart) { throw new InvalidOperationException("Feature startup failed."); }
        }
        public void Stop() => calls.Add("feature:stop");
    }

    sealed class Host(List<string> calls, bool fail, bool failPrepare) : IClientRuntimeHost
    {
        public IInputSource Input { get; } = new BufferedInputSource();
        public IRenderDevice RenderDevice { get; } = Substitute.For<IRenderDevice>();
        public void Prepare(ClientHostContext context)
        {
            calls.Add("host:prepare");
            if (failPrepare) { throw new InvalidOperationException("Host preparation failed."); }
        }
        public void Initialize(GameRuntime runtime, GamePlayerContext context)
        {
            calls.Add("host:initialize");
            Assert.True(runtime.Services.GetRequiredService<ApplicationContext>().Interactive);
            if (fail) { throw new InvalidOperationException("Host initialization failed."); }
        }
        public int Run(GameRuntime runtime)
        {
            calls.Add("host:run");
            return 42;
        }
        public void Dispose() => calls.Add("host:dispose");
    }

    /// <summary>Core composes features before opening a host and stops them before host resources are disposed.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void CoreOwnsClientSessionLifetime(bool fail, bool failPrepare, bool failStart)
    {
        var directory = Directory.CreateTempSubdirectory("turian-client-composition-");
        Directory.CreateDirectory(Path.Combine(directory.FullName, "Assets"));
        ServiceProvider? provider = null;
        var calls = new List<string>();
        var context = new GamePlayerContext(directory.FullName, NullLogger.Instance, services =>
        {
            services.AddSingleton<IApplicationParticipant>(new Feature(calls, failStart));
            return provider = services.BuildServiceProvider();
        });
        try
        {
            if (failPrepare)
            {
                Assert.Throws<InvalidOperationException>(() => new ClientPlayer(() => new Host(calls, fail, failPrepare)).Run(context));
                Assert.Equal(["host:prepare", "host:dispose"], calls);
            }
            else if (failStart)
            {
                Assert.Throws<InvalidOperationException>(() => new ClientPlayer(() => new Host(calls, fail, failPrepare)).Run(context));
                Assert.Equal(["host:prepare", "feature:start", "feature:stop", "host:dispose"], calls);
            }
            else if (fail)
            {
                Assert.Throws<InvalidOperationException>(() => new ClientPlayer(() => new Host(calls, fail, failPrepare)).Run(context));
                Assert.Equal(["host:prepare", "feature:start", "host:initialize", "feature:stop", "host:dispose"], calls);
            }
            else
            {
                Assert.Equal(42, new ClientPlayer(() => new Host(calls, fail, failPrepare)).Run(context));
                Assert.Equal(["host:prepare", "feature:start", "host:initialize", "host:run", "feature:stop", "host:dispose"], calls);
            }
        }
        finally
        {
            provider?.Dispose();
            directory.Delete(true);
        }
    }
}
