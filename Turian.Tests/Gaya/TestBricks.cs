namespace Turian.Tests;

/// <summary>Builds the Bricks panel's controller over a Turian project, as the studio wires it.</summary>
static class TestBricks
{
    /// <summary>A controller managing the project <paramref name="settings"/> points at.</summary>
    /// <param name="settings">Tells which project is open.</param>
    /// <param name="runner">Runs actions as editor tasks.</param>
    /// <param name="applier">Applies brick changes to the open project.</param>
    /// <param name="studio">The studio's bricks, or none.</param>
    public static BricksController Controller(SettingsService settings, BackgroundTaskRunner runner,
        IBrickApplier applier, IBrickWorkspace? studio = null)
    {
        var project = new TurianProjectBricks(settings, runner, applier);
        return new BricksController(studio, project, project, NullLogger.Instance);
    }

    /// <summary>The inspector form of the controller's selection.</summary>
    public static FormInspection? InspectSelection(this BricksController controller) =>
        BrickInspections.Selection(controller);

    /// <summary>The inspector form of the open project's brick settings.</summary>
    public static FormInspection? InspectSettings(this BricksController controller, SettingsService settings) =>
        new TurianBrickInspector(new NodeInspectorController(new AssetManager()), settings).InspectSettings(controller);
}
