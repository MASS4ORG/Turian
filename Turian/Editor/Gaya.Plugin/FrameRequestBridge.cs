namespace Gaya.Plugin.Turian;

/// <summary>Wakes the desktop GUI when editor services publish changes from background work.</summary>
public sealed class FrameRequestBridge : IDisposable
{
    readonly Action request;
    readonly AssetImporter importer;
    readonly BackgroundTaskManager tasks;
    readonly AssetWorkspace workspace;
    readonly IEditorSettings settings;
    readonly PlayModeService playMode;
    readonly SceneTreeController sceneTree;
    bool disposed;

    /// <summary>Connects the GUI to the editor's live services until the window closes.</summary>
    public FrameRequestBridge(Gui gui, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(gui);
        ArgumentNullException.ThrowIfNull(services);
        request = gui.RequestFrame;
        importer = services.GetRequiredService<AssetImporter>();
        tasks = services.GetRequiredService<BackgroundTaskManager>();
        workspace = services.GetRequiredService<AssetWorkspace>();
        settings = services.GetRequiredService<IEditorSettings>();
        playMode = services.GetRequiredService<PlayModeService>();
        sceneTree = services.GetRequiredService<SceneTreeController>();
        LogBuffer.Changed += request;
        importer.AssetsChanged += request;
        tasks.Changed += request;
        workspace.Changed += request;
        settings.Changed += request;
        playMode.StateChanged += OnPlayState;
        sceneTree.FrameNodeRequested += OnFrameNode;
        sceneTree.SceneLoaded += OnSceneLoaded;
    }

    void OnPlayState(PlayState _) => request();

    void OnFrameNode(Node _, FrameNodeOptions options) => request();

    void OnSceneLoaded(Node? _) => request();

    /// <summary>Disconnects callbacks before the window and its services are disposed.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        LogBuffer.Changed -= request;
        importer.AssetsChanged -= request;
        tasks.Changed -= request;
        workspace.Changed -= request;
        settings.Changed -= request;
        playMode.StateChanged -= OnPlayState;
        sceneTree.FrameNodeRequested -= OnFrameNode;
        sceneTree.SceneLoaded -= OnSceneLoaded;
    }
}
