namespace Gaya.Host;

/// <summary>
/// The overrides the user's settings file stores: command id to key sequence, where an empty
/// sequence means the user cleared the shortcut rather than never having touched it.
/// </summary>
public sealed class ShortcutOverrides
{
    /// <summary>Command id to the sequence bound to it, written the way a menu displays one.</summary>
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Every contributed binding, the user's overrides on top, and the chord state machine the workbench
/// feeds key presses into. Overrides ride along in the user's settings file as a hidden page, so they
/// are persisted and restored by the same code every other editor setting uses.
/// </summary>
public sealed class ShortcutService : IShortcutService
{
    /// <summary>The settings page id the overrides are stored under.</summary>
    public const string PageId = "gaya.shortcuts";

    readonly ShortcutOverrides overrides = new();
    readonly List<KeyBinding> defaults = [];
    readonly IEditorSettings? settings;
    readonly ILogger log;

    KeyStroke pending = KeyStroke.None;

    /// <summary>Creates the service and registers its hidden settings page, which restores the overrides.</summary>
    /// <param name="log">Where an unreadable stored sequence is reported.</param>
    /// <param name="settings">The settings the overrides are persisted in; null keeps them in memory.</param>
    public ShortcutService(ILogger log, IEditorSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        this.log = log;
        this.settings = settings;
        settings?.Register(new SettingsPageDescriptor(PageId, "Editor/Shortcuts", overrides)
        {
            Hidden = true,
        });
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>The armed first stroke of a chord, shown in the status bar while it waits.</summary>
    public KeyStroke Pending => pending;

    /// <inheritdoc />
    public bool IsCapturing { get; set; }

    /// <inheritdoc />
    public void Add(KeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var existing = defaults.FindIndex(b => b.CommandId == binding.CommandId && b.Context == binding.Context);
        if (existing >= 0) defaults[existing] = binding;
        else defaults.Add(binding);

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Remove(string commandId)
    {
        if (defaults.RemoveAll(binding => binding.CommandId == commandId) > 0) Changed?.Invoke();
    }

    /// <inheritdoc />
    public IReadOnlyList<ShortcutEntry> Entries =>
        [.. defaults.Select(binding => new ShortcutEntry(binding.CommandId, binding, Effective(binding)))
            .OrderBy(entry => entry.Context, StringComparer.Ordinal)
            .ThenBy(entry => entry.CommandId, StringComparer.Ordinal)];

    /// <inheritdoc />
    public string DisplayFor(string commandId) =>
        defaults.Where(binding => binding.CommandId == commandId)
            .Select(Effective)
            .FirstOrDefault(binding => binding is not null)?.Display ?? string.Empty;

    /// <inheritdoc />
    public void Rebind(string commandId, KeyStroke stroke, KeyStroke? second = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        overrides.Bindings[commandId] = stroke.IsNone
            ? string.Empty
            : new KeyBinding(commandId, stroke, second).Display;

        Persist();
    }

    /// <inheritdoc />
    public void Reset(string commandId)
    {
        if (!overrides.Bindings.Remove(commandId)) return;
        Persist();
    }

    /// <inheritdoc />
    public void ResetAll()
    {
        if (overrides.Bindings.Count == 0) return;

        overrides.Bindings.Clear();
        Persist();
    }

    /// <inheritdoc />
    public IReadOnlyList<ShortcutConflict> Conflicts =>
        [.. Entries.Where(entry => entry.Effective is not null)
            .GroupBy(entry => (entry.Display, entry.Context))
            .Select(group => new ShortcutConflict(group.Key.Display, group.Key.Context,
                [.. group.Select(entry => entry.CommandId)]))
            .Concat(GlobalShadowConflicts())
            .Where(conflict => conflict.CommandIds.Count > 1)];

    /// <inheritdoc />
    public IReadOnlyList<string> ConflictsFor(string commandId, KeyStroke stroke, KeyStroke? second = null)
    {
        if (stroke.IsNone) return [];

        var display = new KeyBinding(commandId, stroke, second).Display;
        var context = defaults.Find(binding => binding.CommandId == commandId)?.Context
                      ?? ShortcutContexts.Global;

        return [.. Entries
            .Where(entry => entry.CommandId != commandId && entry.Display == display)
            .Where(entry => Overlaps(entry.Context, context))
            .Select(entry => entry.CommandId)];
    }

    /// <summary>
    /// Feeds one frame's key state in and returns the command a completed sequence invokes. A chord's
    /// first stroke arms the sequence and returns null; the next stroke either completes it or, if it
    /// matches nothing, cancels it.
    /// </summary>
    /// <param name="input">This frame's input.</param>
    /// <param name="activePanelId">The focused panel, which panel-scoped bindings are matched against.</param>
    /// <returns>The command to run, or null when nothing fired.</returns>
    public string? Dispatch(IInputHandler input, string activePanelId)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (KeyStroke.Read(input) is not { } stroke) return null;

        if (!pending.IsNone)
        {
            var armed = pending;
            pending = KeyStroke.None;

            if (Best(entry => entry.Effective is { Second: { } second }
                              && entry.Effective.Stroke == armed && second == stroke, activePanelId) is { } chord)
                return chord.CommandId;
        }

        if (Best(entry => entry.Effective is { Second: not null } && entry.Effective.Stroke == stroke,
                activePanelId) is not null)
        {
            pending = stroke;
            return null;
        }

        return Best(entry => entry.Effective is { Second: null } && entry.Effective.Stroke == stroke,
            activePanelId)?.CommandId;
    }

    /// <summary>Drops an armed chord, which is what Escape and a focus change do.</summary>
    public void CancelPending() => pending = KeyStroke.None;

    /// <summary>
    /// The matching binding, preferring one scoped to the focused panel over a global one, so a panel
    /// may take a sequence the studio also uses.
    /// </summary>
    ShortcutEntry? Best(Func<ShortcutEntry, bool> matches, string activePanelId)
    {
        var candidates = Entries.Where(matches)
            .Where(entry => ShortcutContexts.IsGlobal(entry.Context) || entry.Context == activePanelId)
            .ToList();

        return candidates.Find(entry => !ShortcutContexts.IsGlobal(entry.Context)) ?? candidates.FirstOrDefault();
    }

    /// <summary>The binding in force for a declared one, or null when the user cleared it.</summary>
    KeyBinding? Effective(KeyBinding declared)
    {
        if (!overrides.Bindings.TryGetValue(declared.CommandId, out var stored)) return declared;
        if (stored.Length == 0) return null;

        if (!KeyBinding.TryParseSequence(stored, out var strokes))
        {
            log.LogWarning("Shortcuts: {Sequence} bound to {CommandId} is not a key sequence", stored,
                declared.CommandId);
            return declared;
        }

        return declared with { Stroke = strokes.First, Second = strokes.Second };
    }

    /// <summary>A global binding also clashes with a panel-scoped one, because it fires inside that panel.</summary>
    IEnumerable<ShortcutConflict> GlobalShadowConflicts()
    {
        var bound = Entries.Where(entry => entry.Effective is not null).ToList();

        return bound.Where(entry => !ShortcutContexts.IsGlobal(entry.Context))
            .Select(scoped => new ShortcutConflict(scoped.Display, scoped.Context,
                [scoped.CommandId, .. bound
                    .Where(other => ShortcutContexts.IsGlobal(other.Context) && other.Display == scoped.Display)
                    .Select(other => other.CommandId)]));
    }

    static bool Overlaps(string left, string right) =>
        left == right || ShortcutContexts.IsGlobal(left) || ShortcutContexts.IsGlobal(right);

    void Persist()
    {
        settings?.NotifyChanged(PageId);
        Changed?.Invoke();
    }
}
