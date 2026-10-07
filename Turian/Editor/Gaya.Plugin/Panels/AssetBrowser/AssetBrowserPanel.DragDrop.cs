namespace Gaya.Plugin.Turian;

sealed partial class AssetBrowserPanel
{
    static bool BrowserPayload(object payload) => payload switch
    {
        ReferenceDragPayload reference => reference.AssetPath is not null,
        ScriptDragPayload script => script.AssetPath is not null,
        _ => false,
    };

    object? DragPayload(TreeItem item) => item.Tag is AssetEntry entry
        ? AssetDragPayloads.Create(entry, SelectedEntries, build.LoadedAssemblies) : null;

    void OnDrop(TreeItem target, object payload)
    {
        if (target.Tag is not AssetEntry destination) return;
        var sources = rows.Select(row => row.Tag).OfType<AssetEntry>().ToArray();
        var request = AssetDropPolicy.Resolve(destination, payload, sources);
        if (request is null) return;
        if (!request.Copy) operations.MoveMany(request.Entries, request.Directory);
        else ConfirmBrickCopy(request);
    }

    void ConfirmBrickCopy(AssetDrop request)
    {
        confirm.Ask("Copy into project",
            $"Copy {request.Entries.Count} item(s) from a brick? Copies get new ids and do not follow brick updates.",
            "Copy", () => operations.CopyIntoMany(request.Entries, request.Directory));
    }

    void GridDropTarget(Gui gui, AssetEntry target, string id)
    {
        var reference = gui.DropTarget<ReferenceDragPayload>(id + "/reference",
            canAccept: payload => AssetDropPolicy.Resolve(target, payload, entries) is not null,
            onDrop: payload => OnDrop(new TreeItem(target.AbsolutePath, "", 0, Tag: target), payload));
        var script = gui.DropTarget<ScriptDragPayload>(id + "/script",
            canAccept: payload => AssetDropPolicy.Resolve(target, payload, entries) is not null,
            onDrop: payload => OnDrop(new TreeItem(target.AbsolutePath, "", 0, Tag: target), payload));
        gui.DrawDropIndicator(reference.State);
        gui.DrawDropIndicator(script.State);
    }
}
