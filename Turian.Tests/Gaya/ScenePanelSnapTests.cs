namespace Turian.Tests;

/// <summary>Checks that the snapping popover edits each operation independently.</summary>
public sealed class ScenePanelSnapTests
{
    /// <summary>Typing updates only its operation, and invalid input retains the valid interval.</summary>
    [Theory]
    [InlineData("move")]
    [InlineData("rotate")]
    [InlineData("scale")]
    public void SnapFieldEditsOnlyItsOperation(string operation)
    {
        using var host = new SceneToolbarHarness();
        host.Click("scene/toolbar/snapOptions");
        host.Replace(operation, "2.5");
        var tools = host.Settings.Tools;
        Assert.Equal(operation == "move" ? 2.5f : 1f, tools.TranslationSnap);
        Assert.Equal(operation == "rotate" ? 2.5f : 15f, tools.RotationSnap);
        Assert.Equal(operation == "scale" ? 2.5f : 0.1f, tools.ScaleSnap);
        host.Replace(operation, "invalid");
        Assert.Equal(operation == "rotate" ? 2.5f : 15f, tools.RotationSnap);
        host.Replace(operation, "NaN");
        Assert.Equal(operation == "move" ? 2.5f : 1f, tools.TranslationSnap);
        host.Replace(operation, "-1");
        Assert.Equal(0, operation switch
        {
            "move" => tools.TranslationSnap,
            "rotate" => tools.RotationSnap,
            _ => tools.ScaleSnap
        });
        host.Settings.Store!.Received().NotifyChanged("gaya.turian.sceneTransform");
    }

    /// <summary>Camera fields constrain invalid ranges and notify the same Settings page.</summary>
    [Fact]
    public void CameraFieldsEditLivePreferences()
    {
        using var host = new SceneToolbarHarness();
        host.Click("scene/toolbar/View");
        var row = host.Nodes().Single(node => node.Id.Contains("/menubar/", StringComparison.Ordinal)
            && node.Id.EndsWith("/i9"));
        host.Click(row.Id);
        host.Replace("fov", "150");
        Assert.Equal(120, host.Settings.FieldOfView);
        host.Replace("near", "-1");
        Assert.Equal(0.001f, host.Settings.NearClip);
        host.Replace("far", "0");
        Assert.Equal(0.011f, host.Settings.FarClip);
        host.Replace("speed", "200");
        Assert.Equal(100, host.Settings.MoveSpeed);
        host.Replace("look", "0");
        Assert.Equal(0.0005f, host.Settings.LookSensitivity);
        host.Settings.Store!.Received().NotifyChanged("gaya.turian.editorCamera");
    }
}
