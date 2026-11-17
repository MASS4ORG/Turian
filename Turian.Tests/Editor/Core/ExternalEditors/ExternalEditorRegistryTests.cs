[assembly: Turian.Editor.Core.ExternalEditor(typeof(Turian.Tests.AttributedDummyEditor))]

namespace Turian.Tests;

/// <summary>An editor with scripted discovery, standing in for an editor brick.</summary>
public class DummyEditor(string id = "dummy", params string[] installed) : IExternalEditor
{
    /// <inheritdoc />
    public string Id => id;

    /// <inheritdoc />
    public string DisplayName => $"Dummy {id}";

    /// <inheritdoc />
    public ExternalEditorCapabilities Capabilities => ExternalEditorCapabilities.OpenProject;

    /// <summary>The contexts this editor was asked to open.</summary>
    public List<EditorContext> Opened { get; } = [];

    /// <summary>Raised instead of opening, when set.</summary>
    public Exception? Failure { get; init; }

    /// <inheritdoc />
    public virtual IReadOnlyList<EditorInstallation> Discover() =>
        [.. installed.Select(path => new EditorInstallation(Id, DisplayName, path))];

    /// <inheritdoc />
    public Task OpenAsync(EditorInstallation installation, EditorContext context, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Opened.Add(context);
        return Task.CompletedTask;
    }
}

/// <summary>An editor registered the way a brick does, by an assembly attribute.</summary>
public sealed class AttributedDummyEditor : DummyEditor
{
    /// <summary>The id the editor registers under.</summary>
    public const string EditorId = "attributed-dummy";

    /// <summary>Creates the editor with one installation.</summary>
    public AttributedDummyEditor() : base(EditorId, "/opt/attributed/editor")
    {
    }
}

/// <summary>The registry resolves editors by stable id and feeds the discovered list.</summary>
public sealed class ExternalEditorRegistryTests
{
    static ExternalEditorRegistry Create() => new(NullLogger.Instance);

    /// <summary>A brick's editor appears in the list without any registration call, and the custom editor is built in.</summary>
    [Fact]
    public void AttributedEditorsAppearInTheDiscoveredList()
    {
        var registry = Create();

        Assert.NotNull(registry.Find(CustomEditor.EditorId));
        Assert.Contains(registry.DiscoverAll(), static installation => installation is
            { EditorId: AttributedDummyEditor.EditorId, Executable: "/opt/attributed/editor" });
    }

    /// <summary>Editors resolve by id ignoring case, and registering the same id replaces the earlier editor.</summary>
    [Fact]
    public void RegistersFindsAndReplacesById()
    {
        var registry = Create();
        var raised = 0;
        registry.Changed += () => raised++;
        var first = new DummyEditor("zed");
        var second = new DummyEditor("ZED");

        registry.Register(first);
        registry.Register(second);

        Assert.Same(second, registry.Find("zed"));
        Assert.Single(registry.Editors, static editor => string.Equals(editor.Id, "zed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, raised);
        Assert.Null(registry.Find("missing"));
    }

    /// <summary>Removing an editor removes its installations and tells listeners; removing it twice does nothing.</summary>
    [Fact]
    public void UnregisterRemovesTheEditor()
    {
        var registry = Create();
        registry.Register(new DummyEditor("ide", "/opt/ide"));
        var raised = 0;
        registry.Changed += () => raised++;

        Assert.True(registry.Unregister("IDE"));
        Assert.False(registry.Unregister("ide"));

        Assert.DoesNotContain(registry.DiscoverAll(), static installation => installation.EditorId == "ide");
        Assert.Equal(1, raised);
    }

    /// <summary>An editor that fails to discover, or finds nothing, contributes no entries and breaks nothing.</summary>
    [Fact]
    public void DiscoveryFailuresAndUnsupportedEditorsYieldNothing()
    {
        var registry = Create();
        registry.Register(new ThrowingEditor());
        registry.Register(new DummyEditor("absent"));
        registry.Register(new DummyEditor("present", "/usr/bin/present"));

        var found = registry.DiscoverAll();

        Assert.Contains(found, static installation => installation.EditorId == "present");
        Assert.DoesNotContain(found, static installation => installation.EditorId is "absent" or "throwing");
    }

    sealed class ThrowingEditor : DummyEditor
    {
        public ThrowingEditor() : base("throwing")
        {
        }

        public override IReadOnlyList<EditorInstallation> Discover() => throw new IOException("unreadable");
    }
}
