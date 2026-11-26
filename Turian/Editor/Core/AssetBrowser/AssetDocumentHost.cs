namespace Turian.Editor.Core;

/// <summary>
/// Routes Save, Save All, close prompts and Undo/Redo to the document editors that implement
/// <see cref="IAssetDocumentSession"/>, so a document written by an editor panel behaves like a scene tab.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class AssetDocumentHost : IDisposable
{
    readonly UserPanelCatalog panels;
    readonly SettingsService settings;
    readonly AssetManager assets;
    readonly EditorNotifications? notifications;
    readonly ILogger log;

    /// <summary>Creates the host and makes it the writer of assets whose source a document editor owns.</summary>
    /// <param name="panels">Supplies the open document editors.</param>
    /// <param name="settings">Resolves an asset's path inside the open project.</param>
    /// <param name="assets">Asks this host to write sources a document editor owns.</param>
    /// <param name="log">Where a failed write is recorded.</param>
    /// <param name="notifications">Where a failed write is reported to the user.</param>
    public AssetDocumentHost(UserPanelCatalog panels, SettingsService settings, AssetManager assets, ILogger log,
        EditorNotifications? notifications = null)
    {
        this.panels = panels;
        this.settings = settings;
        this.assets = assets;
        this.log = log;
        this.notifications = notifications;
        assets.SourceWriter = WriteOwnedSource;
    }

    /// <summary>
    /// Asks the user what to do with a document's unsaved edits, then calls the first callback with the choice or the
    /// second when the user cancels; set by the shell. Without it, <see cref="RequestClose"/> closes at once.
    /// </summary>
    public Action<IAssetDocumentSession, Action<UnsavedChanges>, Action>? Prompt { get; set; }

    /// <summary>
    /// The id of the panel that has focus, as Gaya tracks it; set by the shell. A document editor is active when it
    /// is the target of that panel. Without it, no document editor is ever active.
    /// </summary>
    public Func<string?>? FocusedPanel { get; set; }

    /// <summary>Every document editor with a document open.</summary>
    public IReadOnlyList<IAssetDocumentSession> Sessions =>
        [.. panels.Pages.Select(static page => page.Target).OfType<IAssetDocumentSession>()
            .Where(static session => session.DocumentPath is not null)];

    /// <summary>The document editor with focus, or null when the scene tabs have it.</summary>
    public IAssetDocumentSession? Active => FocusedPanel?.Invoke() is { } panelId
        ? panels.Pages.Where(page => page.Id == panelId).Select(static page => page.Target)
            .OfType<IAssetDocumentSession>().FirstOrDefault(static session => session.DocumentPath is not null)
        : null;

    /// <summary>Whether any open document has unsaved edits.</summary>
    public bool HasUnsavedChanges => Sessions.Any(static session => session.IsDirty);

    /// <summary>Whether Undo has a step in the focused document editor; false when none has focus.</summary>
    public bool CanUndo => Active?.CanUndo ?? false;

    /// <summary>Whether Redo has a step in the focused document editor; false when none has focus.</summary>
    public bool CanRedo => Active?.CanRedo ?? false;

    /// <summary>Reverts the focused document editor's latest edit instead of the scene's.</summary>
    /// <returns>True when a document editor has focus and took the command.</returns>
    public bool Undo() => Route(static session => session.Undo(), static session => session.CanUndo);

    /// <summary>Reapplies the focused document editor's latest reverted edit instead of the scene's.</summary>
    /// <returns>True when a document editor has focus and took the command.</returns>
    public bool Redo() => Route(static session => session.Redo(), static session => session.CanRedo);

    bool Route(Action<IAssetDocumentSession> command, Func<IAssetDocumentSession, bool> available)
    {
        if (Active is not { } session)
        {
            return false;
        }

        if (available(session))
        {
            command(session);
        }

        return true;
    }

    /// <summary>Writes the focused document editor's document.</summary>
    /// <returns>Null when no document editor has focus; otherwise whether the write succeeded.</returns>
    public bool? SaveActive() => Active is { } session ? Save(session) : null;

    /// <summary>Writes one document; a failure leaves it dirty and is reported to the user.</summary>
    /// <param name="session">The document editor to save.</param>
    /// <returns>True when the document is written or was not dirty.</returns>
    public bool Save(IAssetDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!session.IsDirty)
        {
            return true;
        }

        string? error;
        bool saved;
        try
        {
            saved = session.TrySave(out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.LogError(ex, "Could not save {Path}", session.DocumentPath);
            saved = false;
            error = ex.Message;
        }

        if (!saved)
        {
            Report(session.DocumentPath, error);
        }

        return saved;
    }

    /// <summary>Writes every dirty document, continuing past one that fails.</summary>
    /// <returns>True when every dirty document was written.</returns>
    public bool SaveAll() => Sessions.Where(static session => session.IsDirty).ToArray()
        .Select(Save).ToArray().All(static saved => saved);

    /// <summary>Drops the unsaved edits of every open document and closes them.</summary>
    public void DiscardAll()
    {
        foreach (var session in Sessions)
        {
            session.Discard();
        }
    }

    /// <summary>Applies the user's choice for a document that is closing.</summary>
    /// <param name="session">The document editor closing its document.</param>
    /// <param name="resolution">Save writes first; Discard drops the edits.</param>
    /// <returns>True when the document may close; false when a save failed and it must stay open.</returns>
    public bool Resolve(IAssetDocumentSession session, UnsavedChanges resolution)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (resolution == UnsavedChanges.Save && !Save(session))
        {
            return false;
        }

        session.Discard();
        return true;
    }

    /// <summary>Closes a document, first asking what to do with unsaved edits; cancelling keeps it open.</summary>
    /// <param name="session">The document editor closing its document.</param>
    /// <param name="closed">Runs once the document is saved or discarded.</param>
    /// <param name="kept">Runs when the user cancels or a save fails, so the document stays open.</param>
    public void RequestClose(IAssetDocumentSession session, Action? closed = null, Action? kept = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!session.IsDirty || Prompt is null)
        {
            session.Discard();
            closed?.Invoke();
            return;
        }

        Prompt(session, choice =>
        {
            if (Resolve(session, choice))
            {
                closed?.Invoke();
            }
            else
            {
                kept?.Invoke();
            }
        }, () => kept?.Invoke());
    }

    bool? WriteOwnedSource(Asset asset)
    {
        var project = settings.Settings?.ProjectAbsoluteDir;
        if (project is null)
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(project, asset.RelativePath));
        var owner = Sessions.FirstOrDefault(session =>
            string.Equals(Path.GetFullPath(session.DocumentPath!), path, StringComparison.Ordinal));
        return owner is null ? null : Save(owner);
    }

    void Report(string? path, string? error) => notifications?.Post(new EditorNotification(
        $"Could not save {Path.GetFileName(path)}: {error}", IsError: true));

    /// <inheritdoc />
    public void Dispose()
    {
        if (assets.SourceWriter == WriteOwnedSource)
        {
            assets.SourceWriter = null;
        }
    }
}
