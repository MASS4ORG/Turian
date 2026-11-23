namespace Gaya.Plugin.Turian;

/// <summary>
/// Draws the External Editor preference: a dropdown of the discovered installations grouped by editor with Browse and
/// Custom entries, the custom executable and argument template, the options the chosen editor offers, a warning when its
/// executable is gone, and a Test button. Every change is stored at once, so it applies without a restart.
/// </summary>
/// <param name="registry">The editors and their installations.</param>
/// <param name="launcher">Starts the editor for the Test button.</param>
/// <param name="picker">Asks the user for a program, for Browse.</param>
/// <param name="project">The open project, which Test opens.</param>
/// <param name="localization">Translates the strings; null leaves them in English.</param>
sealed partial class ExternalEditorFieldEditor(IExternalEditorRegistry registry, ExternalEditorLauncher launcher,
    IProgramPicker picker, SettingsService project, IShellLocalization? localization) : ISettingsFieldEditor
{
    const float controlWidth = 280f;
    const float inputPadding = 5f;

    static readonly TimeSpan discoveryAge = TimeSpan.FromSeconds(10);
    static readonly TimeSpan existenceAge = TimeSpan.FromSeconds(1);

    readonly Dictionary<string, int> frameChoices = [];
    readonly Dictionary<string, (bool Exists, long Checked)> existence = new(StringComparer.Ordinal);
    IReadOnlyList<EditorInstallation>? installations;
    long discovered;
    bool stale = true;

    static ThemeTokens Theme => ThemeTokens.Current;

    static float InputHeight => Theme.Scale(Theme.RowHeight + 4f);

    string T(string source) => localization?.T(source) ?? source;

    /// <inheritdoc />
    public void Draw(Gui gui, SettingsFieldContext field)
    {
        var choice = field.Value as ExternalEditorChoice ?? new ExternalEditorChoice();
        EditorRow(gui, field, choice);
        if (choice.EditorId == CustomEditor.EditorId)
        {
            CustomFields(gui, field, choice);
        }
        else if (!choice.UsesSystemDefault)
        {
            Line(gui, choice.Executable, Theme.InkDim);
        }

        if (!choice.UsesSystemDefault)
        {
            Warning(gui, field, choice);
            Options(gui, field, choice);
        }

        TestRow(gui, field);
    }

    /// <summary>Marks the discovered installations out of date, so they are found again on the next draw.</summary>
    public void Invalidate() => stale = true;

    IReadOnlyList<EditorInstallation> Installations
    {
        get
        {
            if (installations is null || stale || Stopwatch.GetElapsedTime(discovered) > discoveryAge)
            {
                installations = registry.DiscoverAll();
                discovered = Stopwatch.GetTimestamp();
                stale = false;
            }

            return installations;
        }
    }

    void EditorRow(Gui gui, SettingsFieldContext field, ExternalEditorChoice choice)
    {
        var entries = Entries(choice);
        var current = entries.FindIndex(entry => Selected(entry, choice));
        var labels = entries.Select(static entry => entry.Label).ToArray();

        using (gui.Node(-1, Theme.Scale(Theme.RowHeight), $"{field.Id}/choice").Direction(Axis.Horizontal).Gap(4f)
                   .AlignContentY(0.5f).Enter())
        {
            var next = current;
            gui.Dropdown([.. labels], ref next,
                maxVisibleItems: 12, id: $"{field.Id}/choice").Width(Theme.Scale(controlWidth)).Height(Theme.Scale(Theme.RowHeight));
            Choose(gui, field, choice, entries, next, current);

            if (gui.Button(T("Rescan"), id: $"{field.Id}/rescan").Width(Theme.Scale(60f)).Height(Theme.Scale(Theme.RowHeight)).Clicked)
            {
                Invalidate();
            }
        }
    }

    /// <summary>A choice is made in the build pass and applied in the render pass, so settings never change mid-layout.</summary>
    void Choose(Gui gui, SettingsFieldContext field, ExternalEditorChoice choice, List<Entry> entries, int next, int current)
    {
        if (gui.Pass == Pass.Pass1Build)
        {
            frameChoices.Remove(field.Id);
            if (next >= 0 && next != current)
            {
                frameChoices[field.Id] = next;
            }
        }
        else if (frameChoices.Remove(field.Id, out var chosen) && chosen < entries.Count)
        {
            Apply(entries[chosen], field, choice);
        }
    }

    List<Entry> Entries(ExternalEditorChoice choice)
    {
        List<Entry> entries = [new(EntryKind.SystemDefault, T("System default program")), .. InstallationEntries()];
        if (CurrentEntry(choice) is { } current)
        {
            entries.Add(current);
        }

        entries.Add(new Entry(EntryKind.Browse, T("Browse…")));
        entries.Add(new Entry(EntryKind.Custom, T("Custom…")));
        return entries;
    }

    /// <summary>Each editor's heading followed by its installations.</summary>
    IEnumerable<Entry> InstallationEntries() =>
        Installations.GroupBy(static installation => installation.EditorId).SelectMany(group => group
            .Select(static installation => new Entry(EntryKind.Installation, $"    {Describe(installation)}", installation))
            .Prepend(new Entry(EntryKind.Header, registry.Find(group.Key)?.DisplayName ?? group.Key)));

    /// <summary>The chosen program when it is not among the discovered installations, so it can still be shown.</summary>
    Entry? CurrentEntry(ExternalEditorChoice choice) =>
        choice.UsesSystemDefault || choice.EditorId == CustomEditor.EditorId
        || Installations.Any(installation => SameProgram(installation, choice))
            ? null
            : new Entry(EntryKind.Current, $"{registry.Find(choice.EditorId)?.DisplayName ?? choice.EditorId}: {choice.Executable}");

    static string Describe(EditorInstallation installation) =>
        installation.Version is { Length: > 0 } version ? $"{installation.DisplayName} {version}" : installation.DisplayName;

    static bool SameProgram(EditorInstallation installation, ExternalEditorChoice choice) =>
        string.Equals(installation.EditorId, choice.EditorId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(installation.Executable, choice.Executable, PathComparison);

    static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static bool Selected(Entry entry, ExternalEditorChoice choice) => entry.Kind switch
    {
        EntryKind.SystemDefault => choice.UsesSystemDefault,
        EntryKind.Custom => choice.EditorId == CustomEditor.EditorId,
        EntryKind.Installation => SameProgram(entry.Installation!, choice) && choice.EditorId != CustomEditor.EditorId,
        EntryKind.Current => !choice.UsesSystemDefault && choice.EditorId != CustomEditor.EditorId,
        _ => false,
    };

    void Apply(Entry entry, SettingsFieldContext field, ExternalEditorChoice choice)
    {
        switch (entry.Kind)
        {
            case EntryKind.SystemDefault:
                field.SetValue(new ExternalEditorChoice());
                break;
            case EntryKind.Installation:
                field.SetValue(Installed(entry.Installation!, choice));
                break;
            case EntryKind.Custom:
                field.SetValue(choice.EditorId == CustomEditor.EditorId
                    ? choice
                    : new ExternalEditorChoice(CustomEditor.EditorId, string.Empty, CustomEditor.DefaultTemplate));
                break;
            case EntryKind.Browse:
                Browse(field, keepEditor: false);
                break;
        }
    }

    /// <summary>The choice for an installation; the option values carry over when the editor stays the same.</summary>
    static ExternalEditorChoice Installed(EditorInstallation installation, ExternalEditorChoice current) =>
        new(installation.EditorId, installation.Executable, string.Empty,
            installation.EditorId == current.EditorId ? current.Options : null);

    /// <summary>Asks for a program. A program an editor recognises becomes that editor, otherwise a custom one.</summary>
    void Browse(SettingsFieldContext field, bool keepEditor)
    {
        picker.Pick(T("Choose the editor's program"), path =>
        {
            if (path is not null)
            {
                field.SetValue(Browsed(field.Value as ExternalEditorChoice ?? new ExternalEditorChoice(), path, keepEditor));
            }
        });
    }

    internal ExternalEditorChoice Browsed(ExternalEditorChoice current, string path, bool keepEditor)
    {
        if (keepEditor && !current.UsesSystemDefault)
        {
            return current with { Executable = path };
        }

        var editor = registry.Editors.FirstOrDefault(candidate => candidate is not CustomEditor
                                                                  && candidate.Recognizes(path));
        return editor is null
            ? new ExternalEditorChoice(CustomEditor.EditorId, path, CustomEditor.DefaultTemplate)
            : new ExternalEditorChoice(editor.Id, path);
    }

    bool Exists(string path)
    {
        var now = Stopwatch.GetTimestamp();
        if (existence.TryGetValue(path, out var known) && Stopwatch.GetElapsedTime(known.Checked, now) < existenceAge)
        {
            return known.Exists;
        }

        var exists = File.Exists(path);
        existence[path] = (exists, now);
        return exists;
    }

    void Line(Gui gui, string text, System.Drawing.Color color)
    {
        if (text.Length > 0)
        {
            gui.DrawText(text, Theme.Text(11f), color, centerInRect: false);
        }
    }

    enum EntryKind { SystemDefault, Header, Installation, Current, Browse, Custom }

    sealed record Entry(EntryKind Kind, string Label, EditorInstallation? Installation = null);
}
