namespace Turian.Tests;

/// <summary>
/// A dirty tab of a brick whose editor assembly is recompiled stays on the old code, which is unloading. Saving it
/// either writes the file or fails with the edits kept; it never crashes or drops them.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class RecompiledDocumentTests : IDisposable
{
    const string Plugin = """
        using System;
        using System.IO;
        using Turian.Editor.Core;

        public sealed class BrickPlugin : Gaya.Sdk.ILivePlugin
        {
            public IDisposable Activate(Gaya.Sdk.LivePluginContext context) =>
                ((WorkspaceDocumentKindRegistry)context.Services.GetService(typeof(WorkspaceDocumentKindRegistry)))
                    .Add(new BrickKind());
        }

        public sealed class BrickKind : IWorkspaceDocumentKind
        {
            public string Id => "test.recompiled";

            public string Title => "Recompiled";

            public bool CanOpen(string sourcePath) => sourcePath.EndsWith(".fake", StringComparison.Ordinal);

            public IWorkspaceDocument Open(DocumentOpenContext context) => new BrickDocument(context);
        }

        public sealed class BrickDocument : IWorkspaceDocument
        {
            readonly DocumentOpenContext context;

            public BrickDocument(DocumentOpenContext context) => this.context = context;

            public string Id => context.AssetId.ToString();
            public string Kind => "test.recompiled";
            public string Title => "menu";
            public string SourcePath => context.SourcePath;
            public bool IsDirty { get; private set; } = true;
            public bool IsReadOnly => false;
            public string Conflict => null;
            public event Action Changed;

            public DocumentSaveResult Save(bool overwrite = false)
            {
                File.WriteAllText(context.SourcePath, WRITE);
                IsDirty = false;
                Changed?.Invoke();
                return DocumentSaveResult.Saved;
            }

            public void Reload() { }

            public void Dispose() { }
        }
        """;

    readonly DocumentProject project = new(liveKinds: true);
    readonly string slots = Directory.CreateTempSubdirectory("turian-recompile-").FullName;
    readonly ServiceProvider services;
    readonly AssemblySlotManager manager;
    readonly LivePluginBridge bridge;

    /// <summary>Follows the slot manager's assemblies, as the studio's bridge follows a recompile's.</summary>
    public RecompiledDocumentTests()
    {
        manager = new AssemblySlotManager(slots, NullLogger.Instance);
        services = new ServiceCollection().AddSingleton(project.Registry).BuildServiceProvider();
        var context = new LivePluginContext(services, new PanelRegistry(), new CommandRegistry(), new MenuRegistry())
        {
            DocumentKinds = new DocumentKindRegistry(),
        };
        bridge = new LivePluginBridge(() => manager.UserAssemblies, static () => "project", context,
            NullLogger.Instance);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        bridge.Dispose();
        project.Dispose();
        manager.Unload();
        services.Dispose();
        Directory.Delete(slots, recursive: true);
    }

    // Compiles a build into the inactive slot and swaps it in, which unloads the build before it.
    void Recompile(string write)
    {
        var path = Path.Combine(manager.InactiveSlotDirectory, $"Brick{Guid.NewGuid():N}.dll");
        PluginAssemblies.Emit(path, Path.GetFileNameWithoutExtension(path), Plugin.Replace("WRITE", write,
            StringComparison.Ordinal));
        Assert.True(manager.TrySwapAndLoad(path));
        bridge.Sync();
    }

    AssetWrapper OpenDirtyThenRecompile(string write, out string path)
    {
        Recompile(write);
        path = project.Source("menu.fake").Path;
        var tab = project.Workspace.Open(path)!;
        var old = tab.Document!.GetType().Assembly;
        project.Reported();

        Recompile("\"second\"");

        Assert.DoesNotContain(old, manager.UserAssemblies);
        Assert.Same(old, tab.Document!.GetType().Assembly);
        Assert.Same(tab, Assert.Single(project.Workspace.Documents));
        Assert.Contains("unsaved changes", Assert.Single(project.Reported()), StringComparison.Ordinal);
        return tab;
    }

    /// <summary>The old code still runs while the tab holds it, so its save writes the edits.</summary>
    [Fact]
    public void ADirtyTabIsStillSavedAfterARecompile()
    {
        var tab = OpenDirtyThenRecompile("\"first\"", out var path);

        Assert.True(project.Workspace.Close(tab, UnsavedChanges.Save));

        Assert.Equal("first", File.ReadAllText(path));
        Assert.Empty(project.Workspace.Documents);
        Assert.Empty(project.Reported());
    }

    /// <summary>
    /// A save that needs code the unloading build never loaded cannot get it: the tab stays dirty, the file is
    /// untouched and the user is told.
    /// </summary>
    [Fact]
    public void ASaveThatNeedsUnloadedCodeFailsCleanly()
    {
        var helpers = Directory.CreateTempSubdirectory("turian-recompile-helper-");
        try
        {
            // Only the save refers to the helper, which reaches the old slot after that build was loaded.
            var helper = Path.Combine(helpers.FullName, "LateHelper.dll");
            PluginAssemblies.Emit(helper, "LateHelper",
                "public static class Late { public static string Text() => \"late\"; }");
            var tab = OpenDirtyThenRecompileWithHelper(helper, out var path, out var oldSlot);
            File.Copy(helper, Path.Combine(oldSlot, "LateHelper.dll"));

            var result = project.Workspace.Save(tab);

            Assert.False(result.Succeeded);
            Assert.Contains("LateHelper", result.Error, StringComparison.Ordinal);
            Assert.True(tab.IsDirty);
            Assert.Equal(string.Empty, File.ReadAllText(path));
            Assert.Contains("Could not save menu", Assert.Single(project.Reported()), StringComparison.Ordinal);
            Assert.False(project.Workspace.Close(tab, UnsavedChanges.Save));
            Assert.False(project.Workspace.SaveAll());
            Assert.Same(tab, Assert.Single(project.Workspace.Documents));
            Assert.True(project.Workspace.Close(tab));
        }
        finally
        {
            helpers.Delete(recursive: true);
        }
    }

    AssetWrapper OpenDirtyThenRecompileWithHelper(string helper, out string path, out string oldSlot)
    {
        var build = Path.Combine(manager.InactiveSlotDirectory, $"Brick{Guid.NewGuid():N}.dll");
        PluginAssemblies.Emit(build, Path.GetFileNameWithoutExtension(build),
            Plugin.Replace("WRITE", "Late.Text()", StringComparison.Ordinal), helper);
        Assert.True(manager.TrySwapAndLoad(build));
        bridge.Sync();
        oldSlot = Path.GetDirectoryName(build)!;
        path = project.Source("menu.fake").Path;
        var tab = project.Workspace.Open(path)!;
        project.Reported();

        Recompile("\"second\"");

        project.Reported();
        return tab;
    }
}
