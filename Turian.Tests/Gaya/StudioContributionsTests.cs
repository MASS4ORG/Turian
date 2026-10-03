namespace Turian.Tests;

/// <summary>Checks the Turian studio plugin's contributions are consistent with each other.</summary>
public class StudioContributionsTests
{
    sealed record Contributions(
        List<CommandDescriptor> Commands,
        List<MenuItemDescriptor> Menus,
        List<KeyBinding> Shortcuts,
        List<PanelDescriptor> Panels,
        List<ChromeDescriptor> Chrome);

    static Contributions Configure()
    {
        var found = new Contributions([], [], [], [], []);
        var context = Substitute.For<IPluginContext>();
        context.CommandLineArgs.Returns([]);
        context.Commands.When(r => r.Register(Arg.Any<CommandDescriptor>()))
            .Do(call => found.Commands.Add(call.Arg<CommandDescriptor>()));
        context.Menus.When(r => r.Add(Arg.Any<MenuItemDescriptor>()))
            .Do(call => found.Menus.Add(call.Arg<MenuItemDescriptor>()));
        context.Shortcuts.When(r => r.Add(Arg.Any<KeyBinding>()))
            .Do(call => found.Shortcuts.Add(call.Arg<KeyBinding>()));
        context.Panels.When(r => r.Register(Arg.Any<PanelDescriptor>()))
            .Do(call => found.Panels.Add(call.Arg<PanelDescriptor>()));
        context.Chrome.When(r => r.Register(Arg.Any<ChromeDescriptor>()))
            .Do(call => found.Chrome.Add(call.Arg<ChromeDescriptor>()));

        new GayaPlugin().Configure(context);
        return found;
    }

    /// <summary>Command ids are unique, and every menu entry and studio shortcut runs a registered command.</summary>
    [Fact]
    public void MenusAndShortcutsTargetRegisteredCommands()
    {
        var contributions = Configure();
        var ids = contributions.Commands.Select(command => command.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(contributions.Menus, item => Assert.Contains(item.CommandId, ids));
        var studioShortcuts = contributions.Shortcuts
            .Where(binding => binding.CommandId.StartsWith("gaya.turian.", StringComparison.Ordinal));
        Assert.All(studioShortcuts, binding => Assert.Contains(binding.CommandId, ids));
    }

    /// <summary>Configuring twice yields the same contributions, so a reloaded plugin registers identically.</summary>
    [Fact]
    public void ConfigureIsRepeatable()
    {
        var first = Configure();
        var second = Configure();

        Assert.Equal(first.Commands.Select(c => c.Id), second.Commands.Select(c => c.Id));
        Assert.Equal(first.Panels.Select(p => p.Id), second.Panels.Select(p => p.Id));
        Assert.Contains(first.Commands, command => command.Id == "gaya.turian.save");
    }

    /// <summary>Modal dialogs render independently of the application bar's widgets and drag region.</summary>
    [Fact]
    public void DialogsUseTheOverlaySlot()
    {
        var contributions = Configure();
        Assert.Equal(["gaya.turian.projectSwitcher", "gaya.turian.playToolbar"],
            contributions.Chrome.Where(item => item.Slot == ChromeSlot.MenuBar).Select(item => item.Id));
        Assert.Equal(["gaya.turian.fileDialog", "gaya.turian.unsavedChangesDialog",
                "gaya.turian.confirmDialog", "gaya.turian.aboutDialog"],
            contributions.Chrome.Where(item => item.Slot == ChromeSlot.Overlay).Select(item => item.Id));
    }
}
