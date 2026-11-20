namespace Turian.Editor.Core;

/// <summary>
/// Holds the registered editors. Bricks register through <see cref="ExternalEditorAttribute"/> on their assembly, which
/// is read whenever the list is asked for, so an editor appears and disappears with its brick's assembly.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton, typeof(IExternalEditorRegistry))]
public sealed class ExternalEditorRegistry(ILogger log) : IExternalEditorRegistry
{
    readonly Lock gate = new();
    readonly List<IExternalEditor> registered = [new CustomEditor()];
    readonly Dictionary<Type, IExternalEditor> fromAssemblies = [];

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public IReadOnlyList<IExternalEditor> Editors
    {
        get
        {
            lock (gate)
            {
                var attributed = LoadedEditors().Where(editor => registered.All(other => !SameId(other, editor.Id)));
                return [.. registered, .. attributed];
            }
        }
    }

    /// <inheritdoc />
    public void Register(IExternalEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        lock (gate)
        {
            registered.RemoveAll(other => SameId(other, editor.Id));
            registered.Add(editor);
        }

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public bool Unregister(string id)
    {
        bool removed;
        lock (gate)
        {
            removed = registered.RemoveAll(other => SameId(other, id)) > 0;
        }

        if (removed)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    /// <inheritdoc />
    public IExternalEditor? Find(string id) => Editors.FirstOrDefault(editor => SameId(editor, id));

    /// <inheritdoc />
    public IReadOnlyList<EditorInstallation> DiscoverAll()
    {
        var found = new List<EditorInstallation>();
        foreach (var editor in Editors)
        {
            try
            {
                found.AddRange(editor.Discover());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or InvalidOperationException or PlatformNotSupportedException)
            {
                log.LogWarning(exception, "Discovering {Editor} failed", editor.DisplayName);
            }
        }

        return found;
    }

    static bool SameId(IExternalEditor editor, string id) =>
        string.Equals(editor.Id, id, StringComparison.OrdinalIgnoreCase);

    // The editors of the assemblies loaded right now; those of an unloaded assembly are forgotten.
    List<IExternalEditor> LoadedEditors()
    {
        var types = AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly => !assembly.IsDynamic)
            .SelectMany(static assembly => assembly.GetCustomAttributes<ExternalEditorAttribute>())
            .Select(static attribute => attribute.EditorType)
            .Distinct()
            .ToList();
        foreach (var gone in fromAssemblies.Keys.Except(types).ToList())
        {
            fromAssemblies.Remove(gone);
        }

        foreach (var type in types.Where(type => !fromAssemblies.ContainsKey(type)))
        {
            try
            {
                fromAssemblies[type] = (IExternalEditor)Activator.CreateInstance(type)!;
            }
            catch (Exception exception) when (exception is MissingMethodException or InvalidCastException
                                                  or TargetInvocationException)
            {
                log.LogWarning(exception, "External editor {Type} could not be created", type.FullName);
            }
        }

        return [.. fromAssemblies.Values];
    }
}
