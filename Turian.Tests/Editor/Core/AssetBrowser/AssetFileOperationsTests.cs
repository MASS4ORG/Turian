namespace Turian.Tests;

/// <summary>Tests for the asset browser's file operations as undoable steps.</summary>
public class AssetFileOperationsTests : IDisposable
{
    readonly string project = Path.Combine(Path.GetTempPath(), $"turian-fileops-{Guid.NewGuid():N}");
    readonly AssetManager assets = new();
    readonly UndoService undo;
    readonly AssetFileSystem files;
    readonly AssetFileOperations operations;

    string Assets => Path.Combine(project, "Assets");

    /// <summary>A project on disk; nothing here reaches the importer.</summary>
    public AssetFileOperationsTests()
    {
        Directory.CreateDirectory(Assets);
        var settings = new SettingsService();
        settings.Set(new AppSettings { ProjectAbsoluteDir = project });
        var loader = Substitute.For<IAssetLoader>();
        var sceneTree = new SceneTreeController(assets, settings, assetImporter: null!,
            assetLoader: loader, sceneManager: Substitute.For<ISceneManager>(), database: new AssetDatabase());
        undo = new UndoService(sceneTree, new NodeInspectorController(assets), assets, loader);
        files = new AssetFileSystem(settings, assetImporter: null!);
        operations = new AssetFileOperations(files, undo);
    }

    /// <summary>Deletes the project.</summary>
    public void Dispose()
    {
        undo.Dispose();
        if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        GC.SuppressFinalize(this);
    }

    string AddAsset(string name, string content = "data")
    {
        var path = Path.Combine(Assets, name);
        File.WriteAllText(path, content);
        File.WriteAllText($"{path}.meta",
            Serializer.Serialize(new DataAssetAsset { Id = Guid.NewGuid(), RelativePath = path }));
        return path;
    }

    /// <summary>A deleted asset goes to the trash; undo brings it back with its meta file, redo deletes it.</summary>
    [Fact]
    public void Delete_IsUndoneFromTheTrash()
    {
        var path = AddAsset("stats.dataasset");
        var meta = File.ReadAllText($"{path}.meta");

        Assert.True(operations.Delete(path));
        Assert.False(File.Exists(path));

        undo.Undo();
        Assert.True(File.Exists(path));
        Assert.Equal(meta, File.ReadAllText($"{path}.meta"));

        undo.Redo();
        Assert.False(File.Exists(path));
    }

    /// <summary>A rename is undone back to the old name, keeping the meta file beside the asset.</summary>
    [Fact]
    public void Rename_IsUndone()
    {
        var path = AddAsset("old.dataasset");

        var renamed = operations.Rename(path, isDirectory: false, "new.dataasset")!;
        Assert.True(File.Exists(renamed));

        undo.Undo();
        Assert.True(File.Exists(path));
        Assert.True(File.Exists($"{path}.meta"));
        Assert.False(File.Exists(renamed));

        undo.Redo();
        Assert.True(File.Exists(renamed));
    }

    /// <summary>Undoing a cut and paste moves the asset back with its meta file; redo moves it again.</summary>
    [Fact]
    public void CutPaste_IsUndone()
    {
        var path = AddAsset("stats.dataasset");
        var folder = Path.Combine(Assets, "Data");
        Directory.CreateDirectory(folder);

        files.Cut(path);
        var pasted = operations.Paste(folder)!;
        Assert.True(File.Exists(pasted));

        undo.Undo();
        Assert.True(File.Exists(path));
        Assert.True(File.Exists($"{path}.meta"));
        Assert.False(File.Exists(pasted));

        undo.Redo();
        Assert.True(File.Exists(pasted));
    }

    static Guid MetaId(string asset) =>
        Guid.Parse(JsonNode.Parse(File.ReadAllText($"{asset}.meta"))!["Id"]!.GetValue<string>());

    AssetEntry Entry(string path, bool directory = false) => new(path, directory, null, Path.GetDirectoryName(path));

    /// <summary>Deleting several files is one step that restores every original asset id.</summary>
    [Fact]
    public void MultiDeleteIsOneUndoStep()
    {
        var first = AddAsset("first.dataasset");
        var second = AddAsset("second.dataasset");
        var ids = new[] { MetaId(first), MetaId(second) };
        Assert.True(operations.DeleteMany([Entry(first), Entry(second)]));
        Assert.Single(undo.History.UndoSteps);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        undo.Undo();
        Assert.Equal(ids, new[] { MetaId(first), MetaId(second) });
        undo.Redo();
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    /// <summary>Moving several assets preserves metadata, avoids name collisions and is undone together.</summary>
    [Fact]
    public void MultiMovePreservesIdsAndAvoidsCycles()
    {
        var first = AddAsset("first.dataasset");
        var second = AddAsset("second.dataasset");
        var id = MetaId(first);
        var destination = Path.Combine(Assets, "Folder");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "first.dataasset"), "existing");
        Assert.True(operations.MoveMany([Entry(first), Entry(second)], destination));
        Assert.Single(undo.History.UndoSteps);
        Assert.Equal(id, MetaId(Path.Combine(destination, "first 1.dataasset")));
        undo.Undo();
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        undo.Redo();
        Assert.False(File.Exists(first));
        Assert.False(operations.MoveMany([Entry(destination, directory: true)], Path.Combine(destination, "Inside")));
        Assert.False(operations.MoveMany([], destination));
    }

    /// <summary>Duplicating multiple files restores the copies' own ids on redo.</summary>
    [Fact]
    public void MultiDuplicatePreservesCopiedIdsOnRedo()
    {
        var first = AddAsset("first.dataasset");
        var second = AddAsset("second.dataasset");
        Assert.True(operations.DuplicateMany([Entry(first), Entry(second)]));
        Assert.Single(undo.History.UndoSteps);
        var copy = Path.Combine(Assets, "first 1.dataasset");
        var copiedId = MetaId(copy);
        Assert.NotEqual(MetaId(first), copiedId);
        undo.Undo();
        Assert.False(File.Exists(copy));
        undo.Redo();
        Assert.Equal(copiedId, MetaId(copy));
    }

    /// <summary>A selected folder covers selected descendants, and failed batches roll back completed entries.</summary>
    [Fact]
    public void NestedSelectionAndFailedBatchAreHandled()
    {
        var folder = Path.Combine(Assets, "Folder");
        Directory.CreateDirectory(folder);
        var child = AddAsset(Path.Combine("Folder", "child.dataasset"));
        Assert.Equal([Entry(folder, true)], AssetFileOperations.Roots([Entry(folder, true), Entry(child)]));
        Assert.True(operations.DeleteMany([Entry(folder, true), Entry(child)]));
        undo.Undo();
        Assert.True(File.Exists(child));
        var missing = Entry(Path.Combine(Assets, "missing.dataasset"));
        Assert.False(operations.DeleteMany([Entry(child), missing]));
        Assert.True(File.Exists(child));
        Assert.False(operations.DeleteMany([]));
    }

    /// <summary>A copy of a read-only brick asset is writable, has its own id, and carries it in its payload too.</summary>
    [Fact]
    public void CopyOfAReadOnlyAsset_IsTheProjectsOwn()
    {
        var path = AddAsset("stats.dataasset");
        var oldId = MetaId(path);
        File.WriteAllText(path, $$"""{ "Id": "{{oldId}}" }""");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var folder = Path.Combine(Assets, "Mine");
        Directory.CreateDirectory(folder);

        files.Copy(path);
        var copy = operations.Paste(folder)!;

        var newId = MetaId(copy);
        Assert.NotEqual(oldId, newId);
        Assert.False(new FileInfo(copy).IsReadOnly);
        Assert.Contains(newId.ToString(), File.ReadAllText(copy), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(oldId.ToString(), File.ReadAllText(copy), StringComparison.OrdinalIgnoreCase);
        File.SetAttributes(path, FileAttributes.Normal);
    }

    /// <summary>A copied material carries its new id in its payload like any other text asset.</summary>
    [Fact]
    public void CopyOfAMaterial_RewritesItsOwnId()
    {
        var path = AddAsset("wood.material");
        var oldId = MetaId(path);
        File.WriteAllText(path, $$"""{ "Id": "{{oldId}}" }""");
        var folder = Path.Combine(Assets, "Mine");
        Directory.CreateDirectory(folder);

        files.Copy(path);
        var copy = operations.Paste(folder)!;

        Assert.DoesNotContain(oldId.ToString(), File.ReadAllText(copy), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MetaId(copy).ToString(), File.ReadAllText(copy), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Assets copied together with a folder point at each other's copies, not at the originals.</summary>
    [Fact]
    public void CopyOfAFolder_KeepsItsAssetsPointingAtEachOther()
    {
        var folder = Path.Combine(Assets, "Pack");
        Directory.CreateDirectory(folder);
        var material = AddAsset(Path.Combine("Pack", "material.dataasset"));
        var prefab = AddAsset(Path.Combine("Pack", "prefab.dataasset"));
        File.WriteAllText(prefab, $$"""{ "Material": "{{MetaId(material)}}" }""");
        var target = Path.Combine(Assets, "Mine");
        Directory.CreateDirectory(target);

        files.Copy(folder);
        var copy = operations.Paste(target)!;

        var copiedMaterial = MetaId(Path.Combine(copy, "material.dataasset"));
        Assert.NotEqual(MetaId(material), copiedMaterial);
        Assert.Contains(copiedMaterial.ToString(), File.ReadAllText(Path.Combine(copy, "prefab.dataasset")),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>File operations belong to the project, not to whichever scene is open.</summary>
    [Fact]
    public void Operations_BelongToTheProject()
    {
        operations.Delete(AddAsset("a.dataasset"));

        Assert.Equal(UndoService.ProjectDocument, undo.History.UndoSteps[^1].Document);
    }

    /// <summary>An undo that cannot move the file back keeps the step, and works once the way is clear.</summary>
    [Fact]
    public void Undo_ThatFails_KeepsTheStep()
    {
        var path = AddAsset("stats.dataasset", "original");
        operations.Delete(path);
        File.WriteAllText(path, "in the way");

        undo.Undo();
        Assert.True(undo.CanUndo);
        Assert.Equal("in the way", File.ReadAllText(path));

        File.Delete(path);
        undo.Undo();
        Assert.False(undo.CanUndo);
        Assert.Equal("original", File.ReadAllText(path));
    }
}
