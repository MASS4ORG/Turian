namespace Turian.Tests;

/// <summary>The prefab section of a component's context menu: revert, apply, or remove an added component.</summary>
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
        Assert.All(items, item => Assert.True(item.Enabled));
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

    /// <summary>Builds the menu the panel opens for a component, then returns the items it added.</summary>
    static List<FlyoutItem> Menu(bool added, bool overridden)
    {
        var assets = new AssetManager();
        var inspector = new NodeInspectorController(assets);
        using var undo = new UndoService(
            new SceneTreeController(assets, new SettingsService(), null!, Substitute.For<IAssetLoader>(),
                Substitute.For<ISceneManager>(), new AssetDatabase()),
            inspector, assets, Substitute.For<IAssetLoader>());
        using var panel = new InspectorPanel(inspector, assets, new ReferencePicker(null!, null!, null!), null!,
            null!, new InspectorSettings(), null!, null!, undo, null!, null!, null!, new AssetDatabase());
        var menu = new FlyoutBuilder();
        typeof(InspectorPanel).GetMethod("BuildComponentPrefabMenu", instanceMembers)!
            .Invoke(panel, [menu, new MenuComponent(), added, overridden]);
        return (List<FlyoutItem>)typeof(FlyoutBuilder).GetField("Items", instanceMembers)!.GetValue(menu)!;
    }

    /// <summary>The item labels, leaving out the separators.</summary>
    static IEnumerable<string> Text(List<FlyoutItem> items) =>
        items.Select(item => item.Text).Where(text => text.Length > 0);

    [TypeId("c1b0f2a0-0000-4000-8000-000000000003")]
    sealed class MenuComponent : Component
    {
    }
}
