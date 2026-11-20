namespace Turian.Editor.Core;

/// <summary>Whether the editor a choice names can be started.</summary>
public enum ExternalEditorStatus
{
    /// <summary>Nothing is chosen, so files open with the operating system's default program.</summary>
    SystemDefault,

    /// <summary>The editor is registered and its executable exists.</summary>
    Ready,

    /// <summary>The chosen executable no longer exists, for example after the editor was uninstalled.</summary>
    ExecutableMissing,

    /// <summary>No registered editor has the chosen id, for example because its brick was removed.</summary>
    EditorMissing,
}

/// <summary>
/// The editor the user chose and everything that goes with it, kept as one value so it always resolves from a single
/// settings scope and never mixes an editor from one scope with an executable from another.
/// </summary>
/// <param name="EditorId">The id of the <see cref="IExternalEditor"/>; empty to use the operating system's default program.</param>
/// <param name="Executable">The full path of the program to start.</param>
/// <param name="Arguments">The argument template of a custom editor; empty for other editors.</param>
/// <param name="Options">The values of the editor's <see cref="ExternalEditorOption"/>s, by key; <c>null</c> for none.</param>
public sealed record ExternalEditorChoice(
    string EditorId = "",
    string Executable = "",
    string Arguments = "",
    IReadOnlyDictionary<string, string>? Options = null)
{
    /// <summary>Whether nothing is chosen and the operating system's default program is used.</summary>
    public bool UsesSystemDefault => string.IsNullOrEmpty(EditorId);

    /// <summary>The selection the launcher starts, or <c>null</c> to use the operating system's default program.</summary>
    /// <returns>The selection.</returns>
    public ExternalEditorSelection? ToSelection() =>
        UsesSystemDefault
            ? null
            : new ExternalEditorSelection(EditorId, Executable,
                string.IsNullOrWhiteSpace(Arguments) ? null : Arguments, Options);

    /// <summary>Tells whether the chosen editor can be started now.</summary>
    /// <param name="registry">The registered editors.</param>
    /// <param name="fileExists">Tells whether a file exists; the file system when <c>null</c>.</param>
    /// <returns>The status.</returns>
    public ExternalEditorStatus Check(IExternalEditorRegistry registry, Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (UsesSystemDefault)
        {
            return ExternalEditorStatus.SystemDefault;
        }

        if (registry.Find(EditorId) is null)
        {
            return ExternalEditorStatus.EditorMissing;
        }

        return (fileExists ?? File.Exists)(Executable) ? ExternalEditorStatus.Ready : ExternalEditorStatus.ExecutableMissing;
    }

    /// <summary>Whether another choice names the same editor, program, template and option values.</summary>
    /// <param name="other">The choice to compare with.</param>
    public bool Equals(ExternalEditorChoice? other) =>
        other is not null && EditorId == other.EditorId && Executable == other.Executable
        && Arguments == other.Arguments && SameOptions(Options, other.Options);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(EditorId, Executable, Arguments, Options?.Count ?? 0);

    static bool SameOptions(IReadOnlyDictionary<string, string>? left, IReadOnlyDictionary<string, string>? right)
    {
        left ??= new Dictionary<string, string>();
        right ??= new Dictionary<string, string>();
        return left.Count == right.Count
               && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }
}
