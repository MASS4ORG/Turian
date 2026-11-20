namespace Gaya.Tests;

/// <summary>
/// The scope menu beside each option of a layered settings page — Move, Copy, Store and Reset — is the only way a
/// user changes which scope holds a value, so these tests drive it through the real panel.
/// </summary>
[Collection(SerialTests.Name)]
public sealed class SettingsScopeMenuTests : IDisposable
{
    const string PageId = "test.menu";

    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-scope-menu-{Guid.NewGuid():N}");

    [EditorSetting("Tools", Id = PageId, Scope = SettingsScope.User,
        Scopes = SettingsScope.Workspace | SettingsScope.Personal)]
    sealed class ToolsPage
    {
        public string Editor { get; [UsedImplicitly] set; } = "builtin";
    }

    /// <summary>Deletes everything the test wrote.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    (WorkbenchFrame Frame, ToolsPage Page) Open()
    {
        var frame = new WorkbenchFrame();
        var shell = PluginHost.Load([], NullLogger.Instance);
        frame.Panels.Register(shell.Panels.All.Single(static panel => panel.Id == ShellPanels.Settings));
        var page = new ToolsPage();
        foreach (var other in frame.Settings.Pages.ToArray())
        {
            frame.Settings.Remove(other.Id);
        }

        frame.Settings.BindWorkspace(Path.Combine(root, "project"));
        frame.Settings.BindPersonal(Guid.NewGuid().ToString("D"));
        frame.Settings.Register(page);
        frame.Draw();
        frame.Workbench.ShowPanel(ShellPanels.Settings);
        frame.Draw();
        frame.Draw();
        return (frame, page);
    }

    static IEnumerable<LayoutNode> Nodes(WorkbenchFrame frame) => WorkbenchFrame.Descendants(frame.Gui.RootNode!);

    static void Click(WorkbenchFrame frame, Vector2 pointer)
    {
        frame.Input.MousePosition.Returns(pointer);
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(true);
        frame.Draw();
        frame.Input.IsMouseButtonPressed(GMouseButton.Left).Returns(false);
        frame.Draw();
        frame.Draw();
    }

    /// <summary>The labels the menu offers, read from its open list.</summary>
    static string[] Choose(WorkbenchFrame frame, int index)
    {
        var title = Nodes(frame).Single(static node => node.Id == $"settings/{PageId}/field0/title");
        var button = WorkbenchFrame.Descendants(title).Single(static node => node.Id.EndsWith("/button", StringComparison.Ordinal));
        var prefix = button.Id[..^"/button".Length];
        Click(frame, button.Rect.Center);
        var items = Nodes(frame).Where(node => node.Id.StartsWith($"{prefix}/list/", StringComparison.Ordinal)
            && int.TryParse(node.Id[$"{prefix}/list/".Length..], out _)).ToList();
        Assert.NotEmpty(items);
        var labels = items.Select(static item => item.Id).ToArray();
        Click(frame, Nodes(frame).Single(node => node.Id == $"{prefix}/list/{index}").Rect.Center);
        return labels;
    }

    /// <summary>Store, Copy, Move and Reset each change which scopes hold the value, in that order of availability.</summary>
    [Fact]
    public void MenuStoresCopiesMovesAndResets()
    {
        var (frame, page) = Open();
        using (frame)
        {
            var settings = frame.Settings;
            Assert.Empty(settings.ScopesHolding(PageId, "Editor"));

            // Nothing holds the value: it can be stored in any scope, and there is nothing to reset.
            var offered = Choose(frame, 2);
            Assert.Equal(3, offered.Length);
            Assert.Equal([SettingsScope.Personal], settings.ScopesHolding(PageId, "Editor"));

            // Held by Personal: Move/Copy to User and Workspace, then Reset in Personal.
            offered = Choose(frame, 2);
            Assert.Equal(5, offered.Length);
            Assert.Equal([SettingsScope.Workspace], settings.ScopesHolding(PageId, "Editor"));

            // Held by Workspace: Move/Copy to User and Personal; Copy to Personal (index 3) keeps both.
            Choose(frame, 3);
            Assert.Equal([SettingsScope.Workspace, SettingsScope.Personal], settings.ScopesHolding(PageId, "Editor"));
            Assert.Equal(SettingsScope.Personal, settings.EffectiveScope(PageId, "Editor"));

            // Held by two scopes: Reset in Personal is the last entry, and lets Workspace show through.
            offered = Choose(frame, 5);
            Assert.Equal(6, offered.Length);
            Assert.Equal([SettingsScope.Workspace], settings.ScopesHolding(PageId, "Editor"));
            Assert.Equal("builtin", page.Editor);
        }
    }

    /// <summary>
    /// A Move whose destination file cannot be read leaves the value where it was, and the panel keeps drawing.
    /// </summary>
    [Fact]
    public void MenuMoveToAnUnwritableScopeLeavesTheValueInPlace()
    {
        var (frame, page) = Open();
        using (frame)
        {
            frame.Settings.Write(PageId, "Editor", SettingsScope.Workspace, "shared");
            var personal = frame.Settings.PathFor(SettingsScope.Personal);
            Directory.CreateDirectory(Path.GetDirectoryName(personal)!);
            File.WriteAllText(personal, "{broken");
            frame.Draw();

            // Held by Workspace: Move to User (0), Copy to User (1), Move to Personal (2).
            Choose(frame, 2);
            frame.Draw();

            Assert.Equal([SettingsScope.Workspace], frame.Settings.ScopesHolding(PageId, "Editor"));
            Assert.Equal("shared", page.Editor);
            Assert.Equal("{broken", File.ReadAllText(personal));
        }
    }
}
