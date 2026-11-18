namespace Turian.Tests;

/// <summary>The stored external editor choice: one value, compared by content, that survives any path shape.</summary>
public sealed class ExternalEditorChoiceTests
{
    static readonly JsonSerializerOptions Json = SettingsFile.JsonOptions;

    sealed class StubRegistry(params string[] ids) : IExternalEditorRegistry
    {
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<IExternalEditor> Editors => [];

        public void Register(IExternalEditor editor) => throw new NotSupportedException();

        public bool Unregister(string id) => throw new NotSupportedException();

        public IExternalEditor? Find(string id) =>
            ids.Contains(id, StringComparer.OrdinalIgnoreCase) ? new CustomEditor() : null;

        public IReadOnlyList<EditorInstallation> DiscoverAll() => [];
    }

    /// <summary>Choices with the same content are equal whatever the option dictionaries' order or identity.</summary>
    [Fact]
    public void ChoicesCompareByContent()
    {
        var first = new ExternalEditorChoice("code", "/usr/bin/code", "", new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        var second = new ExternalEditorChoice("code", "/usr/bin/code", "", new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, first with { Options = new Dictionary<string, string> { ["a"] = "2", ["b"] = "2" } });
        Assert.Equal(new ExternalEditorChoice(), new ExternalEditorChoice("", "", "", new Dictionary<string, string>()));
        Assert.NotEqual(first, first with { Executable = "/usr/bin/other" });
        Assert.False(first.Equals(null));
    }

    /// <summary>Paths with spaces, Unicode and Windows separators survive the stored JSON unchanged.</summary>
    [Theory]
    [InlineData("/home/usuário/Meu Editor/ed é")]
    [InlineData(@"C:\Program Files\Éditeur 日本語\ed.exe")]
    [InlineData("/Applications/Mi Editor é.app/Contents/MacOS/ed")]
    public void PathsSurviveSerialization(string executable)
    {
        var choice = new ExternalEditorChoice("custom", executable, "--open \"{file}\" --line {line}",
            new Dictionary<string, string> { ["reuse"] = "true" });

        var restored = JsonSerializer.SerializeToNode(choice, Json).Deserialize<ExternalEditorChoice>(Json);

        Assert.Equal(choice, restored);
    }

    /// <summary>Only a named editor produces a selection; the default means the operating system's program.</summary>
    [Fact]
    public void OnlyANamedEditorProducesASelection()
    {
        Assert.Null(new ExternalEditorChoice().ToSelection());

        var selection = new ExternalEditorChoice("custom", "/x/ed", "  ", new Dictionary<string, string> { ["k"] = "v" })
            .ToSelection()!;

        Assert.Equal("custom", selection.EditorId);
        Assert.Null(selection.Arguments);
        Assert.Equal("v", selection.Options!["k"]);
        Assert.Equal("v", selection.ToInstallation("Custom").Options!["k"]);
        Assert.Equal("{file}", new ExternalEditorChoice("custom", "/x/ed", "{file}").ToSelection()!.Arguments);
    }

    /// <summary>The status tells apart nothing chosen, a ready editor, a gone executable and a gone editor.</summary>
    [Fact]
    public void StatusTellsWhyAnEditorCannotStart()
    {
        var registry = new StubRegistry("custom");

        Assert.Equal(ExternalEditorStatus.SystemDefault, new ExternalEditorChoice().Check(registry));
        Assert.Equal(ExternalEditorStatus.Ready, new ExternalEditorChoice("custom", "/x/ed").Check(registry, static _ => true));
        Assert.Equal(ExternalEditorStatus.ExecutableMissing,
            new ExternalEditorChoice("custom", "/x/ed").Check(registry, static _ => false));
        Assert.Equal(ExternalEditorStatus.EditorMissing, new ExternalEditorChoice("gone", "/x/ed").Check(registry));
        Assert.Throws<ArgumentNullException>(static () => new ExternalEditorChoice().Check(null!));
    }

    /// <summary>An option's stored value wins, and its default applies until one is stored.</summary>
    [Fact]
    public void OptionValuesFallBackToTheirDefault()
    {
        var option = new ExternalEditorOption("reuse", "Reuse", ExternalEditorOptionKind.Toggle, "false");

        Assert.Equal("false", option.ValueIn(null));
        Assert.Equal("false", option.ValueIn(new Dictionary<string, string>()));
        Assert.Equal("true", option.ValueIn(new Dictionary<string, string> { ["reuse"] = "true" }));
    }

    /// <summary>Editors have no options and recognise nothing unless a brick says otherwise.</summary>
    [Fact]
    public void EditorsOfferNoOptionsAndRecogniseNothingByDefault()
    {
        IExternalEditor editor = new CustomEditor();

        Assert.Empty(editor.Options);
        Assert.False(editor.Recognizes("/usr/bin/code"));
    }
}
