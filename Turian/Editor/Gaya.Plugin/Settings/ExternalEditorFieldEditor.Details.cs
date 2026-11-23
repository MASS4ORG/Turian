namespace Gaya.Plugin.Turian;

/// <summary>The parts below the editor dropdown: the custom command, the warning, the editor's options and Test.</summary>
sealed partial class ExternalEditorFieldEditor
{
    readonly Dictionary<string, int> optionChoices = [];

    /// <summary>The executable and argument template of a custom editor.</summary>
    void CustomFields(Gui gui, SettingsFieldContext field, ExternalEditorChoice choice)
    {
        using (gui.Node(-1, InputHeight, $"{field.Id}/program").Direction(Axis.Horizontal).Gap(4f)
                   .AlignContentY(0.5f).Enter())
        {
            var program = choice.Executable;
            gui.TextInput(ref program, placeholder: T("Path to the program"),
                id: $"{field.Id}/program/text").Width(Theme.Scale(controlWidth)).Height(InputHeight).Padding(inputPadding);
            if (program != choice.Executable)
            {
                field.SetValue(choice with { Executable = program });
            }

            if (gui.Button(T("Browse…"), id: $"{field.Id}/program/browse").Width(Theme.Scale(70f)).Height(Theme.Scale(Theme.RowHeight)).Clicked)
            {
                Browse(field, keepEditor: true);
            }
        }

        using (gui.Node(-1, InputHeight, $"{field.Id}/arguments").Direction(Axis.Horizontal)
                   .AlignContentY(0.5f).Enter())
        {
            var template = choice.Arguments;
            gui.TextInput(ref template, placeholder: CustomEditor.DefaultTemplate,
                id: $"{field.Id}/arguments/text").Width(Theme.Scale(controlWidth)).Height(InputHeight).Padding(inputPadding);
            if (template != choice.Arguments)
            {
                field.SetValue(choice with { Arguments = template });
            }
        }

        Line(gui, $"{T("Arguments")}: {string.Join(' ', EditorArgumentTemplate.Tokens)}", Theme.InkDim);
    }

    /// <summary>A badge and a quick fix when the chosen editor cannot be started.</summary>
    void Warning(Gui gui, SettingsFieldContext field, ExternalEditorChoice choice)
    {
        var status = choice.Check(registry, Exists);
        if (status is not (ExternalEditorStatus.ExecutableMissing or ExternalEditorStatus.EditorMissing))
        {
            return;
        }

        var missing = status == ExternalEditorStatus.ExecutableMissing;
        using (gui.Node(-1, Theme.Scale(Theme.RowHeight), $"{field.Id}/warning").Direction(Axis.Horizontal).Gap(6f)
                   .AlignContentY(0.5f).Enter())
        {
            if (gui.Button(missing ? T("Choose again…") : T("Use system default"), id: $"{field.Id}/fix").Width(Theme.Scale(120f)).Height(Theme.Scale(Theme.RowHeight)).Clicked)
            {
                if (missing)
                {
                    Browse(field, keepEditor: true);
                }
                else
                {
                    field.SetValue(new ExternalEditorChoice());
                }
            }

            gui.DrawText(WarningText(choice, missing), Theme.Text(11f), Theme.Warning, centerInRect: false);
        }
    }

    string WarningText(ExternalEditorChoice choice, bool missing)
    {
        if (!missing)
        {
            return $"⚠ {T("This editor is not available; its brick may have been removed.")}";
        }

        return choice.Executable.Length == 0
            ? $"⚠ {T("No program chosen yet.")}"
            : $"⚠ {T("Not found")}: {choice.Executable}";
    }

    /// <summary>The options the chosen editor's brick contributes, such as reusing an existing window.</summary>
    void Options(Gui gui, SettingsFieldContext field, ExternalEditorChoice choice)
    {
        if (registry.Find(choice.EditorId) is not { } editor)
        {
            return;
        }

        foreach (var option in editor.Options)
        {
            var stored = option.ValueIn(choice.Options);
            var next = option.Kind switch
            {
                ExternalEditorOptionKind.Toggle => ToggleOption(gui, field, option, stored),
                ExternalEditorOptionKind.Choice => ChoiceOption(gui, field, option, stored),
                _ => TextOption(gui, field, option, stored),
            };
            if (next != stored)
            {
                field.SetValue(choice with { Options = WithOption(choice.Options, option.Key, next) });
            }

            Line(gui, option.Description, Theme.InkDim);
        }
    }

    string ToggleOption(Gui gui, SettingsFieldContext field, ExternalEditorOption option, string stored)
    {
        var enabled = string.Equals(stored, bool.TrueString, StringComparison.OrdinalIgnoreCase);
        gui.Checkbox(ref enabled, T(option.Label), id: $"{field.Id}/option/{option.Key}");
        return enabled ? "true" : "false";
    }

    /// <summary>A dropdown of the option's values; like the editor dropdown, a pick is made in the build pass and applied in the render pass.</summary>
    string ChoiceOption(Gui gui, SettingsFieldContext field, ExternalEditorOption option, string stored)
    {
        var values = option.Choices ?? [];
        var id = $"{field.Id}/option/{option.Key}";
        var current = values.ToList().FindIndex(value => string.Equals(value.Value, stored, StringComparison.OrdinalIgnoreCase));
        gui.DrawText(T(option.Label), Theme.Text(12f), Theme.Ink, centerInRect: false);

        int next;
        using (gui.Node(-1, Theme.Scale(Theme.RowHeight), id).Direction(Axis.Horizontal).AlignContentY(0.5f).Enter())
        {
            next = current;
            gui.Dropdown([.. values.Select(value => T(value.Label))], ref next,
                maxVisibleItems: 12, id: id).Width(Theme.Scale(controlWidth)).Height(Theme.Scale(Theme.RowHeight));
        }

        if (gui.Pass == Pass.Pass1Build)
        {
            optionChoices.Remove(id);
            if (next >= 0 && next != current)
            {
                optionChoices[id] = next;
            }

            return stored;
        }

        return optionChoices.Remove(id, out var chosen) && chosen < values.Count ? values[chosen].Value : stored;
    }

    string TextOption(Gui gui, SettingsFieldContext field, ExternalEditorOption option, string stored)
    {
        gui.DrawText(T(option.Label), Theme.Text(12f), Theme.Ink, centerInRect: false);
        gui.TextInput(ref stored, id: $"{field.Id}/option/{option.Key}").Width(Theme.Scale(controlWidth)).Height(InputHeight).Padding(inputPadding);
        return stored;
    }

    static Dictionary<string, string> WithOption(IReadOnlyDictionary<string, string>? options, string key, string value)
    {
        var values = options is null ? [] : new Dictionary<string, string>(options, StringComparer.Ordinal);
        values[key] = value;
        return values;
    }

    /// <summary>Opens the project with the editor that applies now, so a wrong choice shows up here and not later.</summary>
    void TestRow(Gui gui, SettingsFieldContext field)
    {
        if (field.Value is not ExternalEditorChoice { UsesSystemDefault: false })
        {
            return;
        }

        var settings = project.Settings;
        if (gui.Button(T("Test"), enabled: settings is not null,
            id: $"{field.Id}/test").Width(Theme.Scale(60f)).Height(Theme.Scale(Theme.RowHeight)).Clicked && settings is not null)
        {
            _ = Task.Run(() => launcher.OpenAsync(CodeOpener.ContextFor(settings)));
        }
    }
}
