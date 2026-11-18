namespace Gaya.Host;

/// <summary>The scope controls of a page that cascades: where each value comes from, and the menu that relocates it.</summary>
sealed partial class SettingsPanel
{
    readonly Dictionary<string, int> frameScopeActions = [];

    /// <summary>
    /// For a page that cascades: the scope the shown value comes from, and a menu to move, copy, store
    /// or reset it. The menu lists only what the member's current state allows.
    /// </summary>
    void ScopeControls(Gui gui, SettingsPageDescriptor page, FormField field, string id)
    {
        var effective = settings.EffectiveScope(page.Id, field.Name);
        gui.DrawText(T(effective?.ToString() ?? "Default"), Theme.Text(10f),
            effective is null ? Theme.InkDim : Theme.Accent, centerInRect: false);

        var actions = ScopeActions(page, field.Name, effective);
        if (actions.Count > 0)
        {
            ScopeMenu(gui, page, field.Name, id, actions);
        }
    }

    void ScopeMenu(Gui gui, SettingsPageDescriptor page, string member, string id,
        List<(string Label, Func<SettingsChange> Run)> actions)
    {
        // ReSharper disable once ExplicitCallerInfoArgument
        var next = gui.Dropdown([.. actions.Select(static action => action.Label)], -1, width: Theme.Scale(104f),
            height: Theme.Scale(Theme.RowHeight), placeholder: T("Scope…"), fontSize: Theme.Text(11),
            maxVisibleItems: 8, filePath: $"{id}/scope");

        // A choice is made in the build pass and applied in the render pass, so the settings never change mid-layout.
        if (gui.Pass == Pass.Pass1Build)
        {
            frameScopeActions.Remove(id);
            if (next >= 0)
            {
                frameScopeActions[id] = next;
            }
        }
        else if (frameScopeActions.Remove(id, out var chosen) && chosen < actions.Count)
        {
            Run(actions[chosen].Run, page, member);
        }
    }

    void Run(Func<SettingsChange> action, SettingsPageDescriptor page, string member)
    {
        var change = action();
        if (!change.Succeeded)
        {
            log.LogWarning("Settings: {Page}.{Member}: {Error}", page.Id, member, change.Error);
        }
    }

    /// <summary>What a member can do from where it stands: the scopes it could go to, and the ones it could leave.</summary>
    List<(string Label, Func<SettingsChange> Run)> ScopeActions(SettingsPageDescriptor page, string member,
        SettingsScope? effective)
    {
        var actions = new List<(string, Func<SettingsChange>)>();
        var available = page.Layers.Where(scope => !string.IsNullOrEmpty(settings.PathFor(scope))).ToList();

        foreach (var to in available.Where(scope => scope != effective))
        {
            if (effective is { } from)
            {
                actions.Add((T($"Move to {to}"), () => settings.Move(page.Id, member, from, to)));
                actions.Add((T($"Copy to {to}"), () => settings.Copy(page.Id, member, from, to)));
            }
            else
            {
                actions.Add((T($"Store in {to}"),
                    () => settings.Write(page.Id, member, to, CurrentValue(page, member))));
            }
        }

        foreach (var held in settings.ScopesHolding(page.Id, member))
        {
            actions.Add((T($"Reset in {held}"), () => settings.Reset(page.Id, member, held)));
        }

        return actions;
    }

    static object? CurrentValue(SettingsPageDescriptor page, string member) =>
        page.Target.GetType().GetProperty(member)?.GetValue(page.Target)
        ?? page.Target.GetType().GetField(member)?.GetValue(page.Target);

    /// <summary>Lower scopes' values that a higher scope hides, greyed so they read as inactive.</summary>
    void Overridden(Gui gui, SettingsPageDescriptor page, string member)
    {
        var held = settings.ScopesHolding(page.Id, member);
        for (var i = 0; i < held.Count - 1; i++)
        {
            if (settings.TryRead(page.Id, member, held[i], out var value))
            {
                gui.DrawText($"{T(held[i].ToString())}: {value}", Theme.Text(10f), Theme.InkDim, centerInRect: false);
            }
        }
    }
}
