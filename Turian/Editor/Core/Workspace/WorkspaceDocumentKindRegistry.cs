namespace Turian.Editor.Core;

/// <summary>
/// The document kinds the workspace opens sources with: those registered through dependency injection, then those
/// editor bricks add while they are active. It is used on the UI thread.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class WorkspaceDocumentKindRegistry
{
    readonly List<IWorkspaceDocumentKind> kinds;

    /// <summary>Creates the registry with the kinds that stay for its whole lifetime.</summary>
    /// <param name="kinds">The kinds registered through dependency injection.</param>
    public WorkspaceDocumentKindRegistry(IEnumerable<IWorkspaceDocumentKind>? kinds = null) =>
        this.kinds = [.. kinds ?? []];

    /// <summary>The kinds in force, in registration order, which is the order they are offered a source.</summary>
    public IReadOnlyList<IWorkspaceDocumentKind> Kinds => kinds;

    /// <summary>Raised after a kind was added.</summary>
    public event Action<IWorkspaceDocumentKind>? Added;

    /// <summary>Raised after a kind was removed.</summary>
    public event Action<IWorkspaceDocumentKind>? Removed;

    /// <summary>Adds a kind, such as one a brick contributes while it is active.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The registration; disposing it removes the kind.</returns>
    public IDisposable Add(IWorkspaceDocumentKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        kinds.Add(kind);
        Added?.Invoke(kind);
        return new ContributionLifetime(() =>
        {
            if (kinds.Remove(kind))
            {
                Removed?.Invoke(kind);
            }
        });
    }
}
