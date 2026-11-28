namespace Turian.Tests;

/// <summary>The log explains which editors bricks and scripts contributed, and why a file left the studio.</summary>
public sealed class BrickEditorDiagnosticsTests : IDisposable
{
    sealed class Messages : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }

    readonly LivePluginHarness harness = new();
    readonly Messages log = new();

    /// <inheritdoc />
    public void Dispose() => harness.Dispose();

    /// <summary>Each activated plugin is named with the document kinds it added, or none.</summary>
    [Fact]
    public void ActivationNamesTheAddedDocumentKinds()
    {
        var kinds = new WorkspaceDocumentKindRegistry();
        harness.Behaviour = (_, name) => name == "Editor"
            ? kinds.Add(new FakeDocumentKind { Id = "fake.editor" })
            : new Withdrawal(static () => { });
        using var bridge = new LivePluginBridge(() => harness.Assemblies, () => "project", harness.Context, log,
            workspaceKinds: kinds);
        harness.Assemblies = [harness.Load("Editor"), harness.Load("Menus")];

        bridge.Sync();

        Assert.Collection(log.Lines,
            line => Assert.Equal("Editor plugin EditorPlugin activated; document kinds added: fake.editor", line.Text),
            line => Assert.Equal("Editor plugin MenusPlugin activated; document kinds added: none", line.Text));
    }

    /// <summary>Types that cannot load are reported, since a plugin among them silently contributes nothing.</summary>
    [Fact]
    public void UnloadableTypesAreReported()
    {
        var partial = Substitute.For<Assembly>();
        partial.FullName.Returns("Partial");
        partial.GetTypes().Returns(static _ => throw new ReflectionTypeLoadException(
            [null], [new TypeLoadException("Gaya.Sdk is missing")]));
        using var bridge = new LivePluginBridge(() => harness.Assemblies, () => "project", harness.Context, log);
        harness.Assemblies = [partial];

        bridge.Sync();

        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("Partial", line.Text, StringComparison.Ordinal);
        Assert.Contains("Gaya.Sdk is missing", line.Text, StringComparison.Ordinal);
    }

    /// <summary>A file handed to the system program says that no studio editor claims it.</summary>
    [Fact]
    public void AFileLeavingTheStudioSaysWhy()
    {
        using var browser = new AssetBrowserHarness();
        var types = new AssetTypeCatalog([], browser.Kinds);
        var opener = new AssetOpenService(types, browser.Workspace, null!, browser.Inspector, null!, log)
        {
            Launch = static _ => { },
        };
        var source = Path.Combine(browser.Root, "menu.ui");

        Assert.Equal(AssetActivation.ExternalProgram, opener.Open(source, null));

        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.StartsWith($"No studio editor claims {source}", line.Text, StringComparison.Ordinal);
    }
}
