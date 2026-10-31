namespace Turian.Tests;

/// <summary>The clipboard and prefab sections of a component's context menu.</summary>
[Collection(SerialTests.Name)]
public sealed class InspectorComponentMenuTests
{
    const BindingFlags instanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>A component the prefab provides but the instance changed offers revert and apply.</summary>
    [Fact]
    public void OverriddenComponentOffersRevertAndApply()
    {
        var items = Menu(added: false, overridden: true);

        Assert.Equal(["Revert Component", "Apply Component to Prefab"], Text(items));
        Assert.All(items, static item => Assert.True(item.Enabled));
    }

    /// <summary>A component the instance added offers removal only: there is nothing to apply back.</summary>
    [Fact]
    public void AddedComponentOffersRemovalOnly()
    {
        var items = Menu(added: true, overridden: false);

        Assert.Equal(["Remove Added Component"], Text(items));
    }

    /// <summary>A component with neither overrides nor additions leaves the prefab section out.</summary>
    [Fact]
    public void UnchangedComponentHasNoPrefabActions() => Assert.Empty(Menu(added: false, overridden: false));

    /// <summary>The clipboard items follow what the system clipboard holds, and Copy fills it.</summary>
    [Fact]
    public void ClipboardItemsFollowTheCopiedComponent()
    {
        var clipboard = new MemoryClipboard();
        using var panel = Panel();
        typeof(InspectorPanel).GetField("systemClipboard", instanceMembers)!.SetValue(panel, clipboard);
        var component = new MenuComponent();

        var empty = ClipboardItems(panel, component);
        Assert.Equal(["Copy Component", "Paste Component Values", "Paste Component as New"], Text(empty));
        Assert.True(empty[0].Enabled);
        Assert.False(empty[1].Enabled);
        Assert.False(empty[2].Enabled);

        empty[0].Action!();
        Assert.True(NodeClipboard.HoldsComponent(clipboard.Text));

        Assert.All(ClipboardItems(panel, component), static item => Assert.True(item.Enabled));
    }

    /// <summary>Builds the menu the panel opens for a component, then returns the items it added.</summary>
    static List<FlyoutItem> Menu(bool added, bool overridden)
    {
        using var panel = Panel();
        var menu = new FlyoutBuilder();
        typeof(InspectorPanel).GetMethod("BuildComponentPrefabMenu", instanceMembers)!
            .Invoke(panel, [menu, new MenuComponent(), added, overridden]);
        return Items(menu);
    }

    /// <summary>Builds the clipboard section of the component menu.</summary>
    static List<FlyoutItem> ClipboardItems(InspectorPanel panel, Component component)
    {
        var menu = new FlyoutBuilder();
        typeof(InspectorPanel).GetMethod("BuildComponentClipboardMenu", instanceMembers)!
            .Invoke(panel, [menu, component]);
        return Items(menu);
    }

    static List<FlyoutItem> Items(FlyoutBuilder menu) =>
        (List<FlyoutItem>)typeof(FlyoutBuilder).GetField("Items", instanceMembers)!.GetValue(menu)!;

    /// <summary>The item labels, leaving out the separators.</summary>
    static IEnumerable<string> Text(List<FlyoutItem> items) =>
        items.Select(static item => item.Text).Where(static text => text.Length > 0);

    /// <summary>A panel whose menus can be built without a scene behind it.</summary>
    static InspectorPanel Panel()
    {
        var assets = new AssetManager();
        var inspector = new NodeInspectorController(assets);
        var undo = new UndoService(
            new SceneTreeController(assets, new SettingsService(), null!, Substitute.For<IAssetLoader>(),
                Substitute.For<ISceneManager>(), new AssetDatabase()),
            inspector, assets, Substitute.For<IAssetLoader>());
        return new InspectorPanel(inspector, assets, new ReferencePicker(null!, null!, null!), null!, null!,
            new InspectorSettings(), null!, null!, undo, null!, null!, null!, new AssetDatabase());
    }

    sealed class MemoryClipboard : IClipboard
    {
        public string Text { get; set; } = string.Empty;
        public string GetClipboardText() => Text;
        public void SetClipboardText(string text) => Text = text;
    }

    [TypeId("c1b0f2a0-0000-4000-8000-000000000003")]
    sealed class MenuComponent : Component
    {
    }
}
