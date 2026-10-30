namespace Gaya.Tests;

public sealed partial class StudioBricksTests
{
    /// <summary>Adding or disabling plugin assemblies displays a restart notice; unchanged restores do not.</summary>
    [Fact]
    public async Task StudioPluginChangesRequireRestart()
    {
        var path = Path.Combine(root, "plugin");
        Directory.CreateDirectory(Path.Combine(path, "Precast~", "lib"));
        File.WriteAllBytes(Path.Combine(path, "Precast~", "lib", "Plugin.dll"), [1, 2, 3]);
        new PackageManifest
        {
            Name = "com.acme.plugin",
            Version = SemanticVersion.Parse("1.0.0"),
            Scopes = [PackageScope.Studio],
        }.Save(path);
        var controller = new BricksController(studio, null) { Scope = BrickScope.Studio };
        Assert.True(await controller.RestoreAsync());
        Assert.False(controller.StudioRestartRequired);
        Assert.True(await controller.AddFromSourceAsync(Source("plugin")));
        Assert.True(controller.StudioRestartRequired);
        using var frame = new PanelFrame(new BricksPanel(controller));
        frame.Draw();
        Assert.Contains(frame.Nodes(), static node => node.Id.Contains("Restart Studio", StringComparison.Ordinal));

        var restarted = new BricksController(studio, null) { Scope = BrickScope.Studio };
        Assert.True(await restarted.RestoreAsync());
        Assert.False(restarted.StudioRestartRequired);
        Assert.True(await restarted.DisableAsync("com.acme.plugin"));
        Assert.True(restarted.StudioRestartRequired);
    }

    /// <summary>Live theme changes and failed installations do not request a plugin restart.</summary>
    [Fact]
    public async Task ContentAndFailedChangesDoNotRequireRestart()
    {
        var controller = new BricksController(studio, null) { Scope = BrickScope.Studio };
        Assert.True(await controller.AddFromSourceAsync(Source("sunset")));
        Assert.False(controller.StudioRestartRequired);
        Assert.False(await controller.InstallAsync("com.acme.missing", Source("missing")));
        Assert.False(controller.StudioRestartRequired);
    }
}
