namespace Gaya.Plugin.Turian;

/// <summary>
/// The commands that belong to one panel and only fire while it has focus — Delete in the scene tree
/// removes a node, Delete in the asset browser removes a file. Each one reaches its panel through
/// <see cref="IPanelAccessor"/>, so the binding is declared here rather than inside the panel and is
/// listed in the keybindings editor whether or not the panel has ever been opened.
/// </summary>
static class PanelCommands
{
    /// <summary>Registers every panel-scoped command with its default binding.</summary>
    /// <param name="context">The registration surface handed to the plugin.</param>
    public static void Register(IPluginContext context)
    {
        SceneTree(context);
        Assets(context);
        Viewport(context);
    }

    static void SceneTree(IPluginContext context)
    {
        var panelId = GayaPlugin.SceneTreePanelId;

        Add(context, panelId, KeyboardKey.Delete, new CommandDescriptor(
            "gaya.turian.sceneTree.delete", "Scene Tree: Delete Node",
            services => Panel<SceneTreePanel>(services, panelId)?.DeleteSelected(),
            services => Panel<SceneTreePanel>(services, panelId)?.HasEditableSelection ?? false)
        { MenuLabel = "Delete Node" });

        Add(context, panelId, KeyboardKey.F2, new CommandDescriptor(
            "gaya.turian.sceneTree.rename", "Scene Tree: Rename Node",
            services => Panel<SceneTreePanel>(services, panelId)?.RenameSelected(),
            services => Panel<SceneTreePanel>(services, panelId)?.HasEditableSelection ?? false)
        { MenuLabel = "Rename Node" });

        Add(context, panelId, KeyboardKey.D, new CommandDescriptor(
            "gaya.turian.sceneTree.duplicate", "Scene Tree: Duplicate Node",
            services => Panel<SceneTreePanel>(services, panelId)?.DuplicateSelected(),
            services => Panel<SceneTreePanel>(services, panelId)?.HasEditableSelection ?? false)
        { MenuLabel = "Duplicate Node" }, KeyModifiers.Ctrl);
    }

    static void Assets(IPluginContext context)
    {
        var panelId = GayaPlugin.AssetsPanelId;

        Add(context, panelId, KeyboardKey.Delete, new CommandDescriptor(
            "gaya.turian.assets.delete", "Assets: Delete",
            services => Panel<AssetBrowserPanel>(services, panelId)?.DeleteSelected(),
            services => Panel<AssetBrowserPanel>(services, panelId)?.HasSelection ?? false)
        { MenuLabel = "Delete" });

        Add(context, panelId, KeyboardKey.F2, new CommandDescriptor(
            "gaya.turian.assets.rename", "Assets: Rename",
            services => Panel<AssetBrowserPanel>(services, panelId)?.RenameSelected(),
            services => Panel<AssetBrowserPanel>(services, panelId)?.HasSelection ?? false)
        { MenuLabel = "Rename" });

        Add(context, panelId, KeyboardKey.D, new CommandDescriptor(
            "gaya.turian.assets.duplicate", "Assets: Duplicate",
            services => Panel<AssetBrowserPanel>(services, panelId)?.DuplicateSelected(),
            services => Panel<AssetBrowserPanel>(services, panelId)?.HasSelection ?? false)
        { MenuLabel = "Duplicate" }, KeyModifiers.Ctrl);
    }

    /// <summary>
    /// The viewport's own keys. W / E / R switch the gizmo without a modifier, which is why they need
    /// a context: the same keys drive the editor camera and mean nothing outside the scene view.
    /// </summary>
    static void Viewport(IPluginContext context)
    {
        var panelId = GayaPlugin.ViewportPanelId;

        Add(context, panelId, KeyboardKey.F, new CommandDescriptor(
            "gaya.turian.viewport.frameSelected", "Scene View: Frame Selected",
            services => Panel<ScenePanel>(services, panelId)?.FrameSelected())
        { MenuLabel = "Frame Selected" });

        GizmoMode(context, panelId, "translate", "Move", TransformGizmoMode.Translate, KeyboardKey.W);
        GizmoMode(context, panelId, "rotate", "Rotate", TransformGizmoMode.Rotate, KeyboardKey.E);
        GizmoMode(context, panelId, "scale", "Scale", TransformGizmoMode.Scale, KeyboardKey.R);
    }

    static void GizmoMode(IPluginContext context, string panelId, string id, string label,
        TransformGizmoMode mode, KeyboardKey key) =>
        Add(context, panelId, key, new CommandDescriptor(
            $"gaya.turian.viewport.{id}", $"Scene View: {label} Tool",
            services =>
            {
                if (Panel<ScenePanel>(services, panelId) is { } panel) panel.Gizmo.Mode = mode;
            })
        { MenuLabel = $"{label} Tool" });

    static void Add(IPluginContext context, string panelId, KeyboardKey key,
        CommandDescriptor command, KeyModifiers modifiers = KeyModifiers.None)
    {
        context.Commands.Register(command);
        context.Shortcuts.Add(new KeyBinding(command.Id, key, modifiers, panelId));
    }

    /// <summary>The panel a command acts on, or null before the workbench has created it.</summary>
    static T? Panel<T>(IServiceProvider services, string panelId) where T : class, IPanel =>
        services.GetService<IPanelAccessor>()?.Panel(panelId) as T;
}
