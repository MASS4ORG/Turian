using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>
/// What the inspector shows for a brick selected in the Bricks panel: its manifest read-only, and buttons for what
/// can be done with it in its current state. The state decides the subclass, so each shows only its own buttons.
/// </summary>
public abstract class BrickView : IInspectorTitled
{
    readonly CatalogBrick brick;
    readonly ResolvedPackage? resolved;
    readonly string? projectFolder;

    /// <summary>Creates the view.</summary>
    /// <param name="controller">The panel's actions.</param>
    /// <param name="brick">The catalog entry shown.</param>
    /// <param name="resolved">The brick as the project resolves it, when it uses it.</param>
    protected BrickView(BricksController controller, CatalogBrick brick, ResolvedPackage? resolved)
    {
        Controller = controller;
        projectFolder = controller.ProjectFolder;
        this.brick = brick;
        this.resolved = resolved;
    }

    /// <summary>The panel's actions, for the buttons.</summary>
    protected BricksController Controller { get; }

    /// <summary>Runs a button's action, unless the project the view was made for is no longer the open one.</summary>
    /// <param name="action">The controller call.</param>
    protected void Act(Func<Task<bool>> action)
    {
        if (Controller.ProjectFolder == projectFolder) _ = action();
    }

    /// <summary>The catalog entry the buttons act on.</summary>
    protected CatalogBrick Brick => brick;

    /// <inheritdoc />
    public string InspectorTitle => $"Brick '{brick.DisplayName ?? brick.Id}'";

    /// <summary>The brick id, which is its package name.</summary>
    [ShowInEditor]
    public string Name => brick.Id;

    /// <summary>The name shown to users.</summary>
    [ShowInEditor]
    public string DisplayName => brick.DisplayName ?? brick.Id;

    /// <summary>The version on this machine or in use; the newest known one when it is only available.</summary>
    [ShowInEditor]
    public string Version => brick.InstalledVersion ?? brick.LatestVersion ?? "";

    /// <summary>The newest version any known source offers.</summary>
    [ShowInEditor]
    public string Latest => brick.LatestVersion ?? "";

    /// <summary>How far the brick is from being used by the project.</summary>
    [ShowInEditor]
    public string Status => brick.State switch
    {
        BrickState.Enabled => resolved?.Origin == PackageOrigin.Embedded ? "In use, local to the project" : "In use",
        BrickState.Installed => "On this machine, not in use",
        _ => "Available",
    };

    /// <summary>Who made the brick.</summary>
    [ShowInEditor]
    public string Author => brick.Author ?? "";

    /// <summary>The license the brick is under.</summary>
    [ShowInEditor]
    public string License => resolved?.Manifest.License ?? "";

    /// <summary>The groups the brick is listed in.</summary>
    [ShowInEditor]
    public List<string> Categories => [.. resolved?.Manifest.Categories ?? []];

    /// <summary>What the brick does.</summary>
    [ShowInEditor]
    public string Description => brick.Description ?? "";

    /// <summary>Where this client got, or would get, the brick.</summary>
    [ShowInEditor]
    public string Origin => brick.Origin ?? "unknown";

    /// <summary>The folder holding the brick on this machine.</summary>
    [ShowInEditor]
    public string InstalledAt => resolved?.RootPath ?? "";

    /// <summary>The bricks this one needs, with the versions it accepts.</summary>
    [ShowInEditor]
    public List<string> Needs => [.. resolved?.Manifest.Dependencies.OrderBy(static d => d.Key, StringComparer.Ordinal)
        .Select(static d => $"{d.Key} {d.Value}") ?? []];

    /// <summary>The bricks in use that need this one.</summary>
    [ShowInEditor]
    public List<string> NeededBy => [.. Controller.RequiredBy(brick.Id)];
}

/// <summary>A brick the project does not use and that is not on this machine yet.</summary>
public sealed class AvailableBrickView(BricksController controller, CatalogBrick brick) : BrickView(controller, brick, null)
{
    /// <summary>Downloads the brick if needed and uses it in the project.</summary>
    [Button]
    public void Enable() => Act(() => Controller.EnableAsync(Brick));
}

/// <summary>A brick on this machine that the project does not use.</summary>
public sealed class InstalledBrickView(BricksController controller, CatalogBrick brick) : BrickView(controller, brick, null)
{
    /// <summary>Uses the brick in the project.</summary>
    [Button]
    public void Enable() => Act(() => Controller.EnableAsync(Brick));

    /// <summary>Deletes the brick from this machine; every project loses it.</summary>
    [Button]
    public void Uninstall() => Act(() => Controller.UninstallAsync(Brick.Id));
}

/// <summary>A brick the project uses from the shared store.</summary>
public sealed class EnabledBrickView(BricksController controller, CatalogBrick brick, ResolvedPackage resolved)
    : BrickView(controller, brick, resolved)
{
    /// <summary>Stops using the brick in the project; it stays on this machine.</summary>
    [Button]
    public void Disable() => Act(() => Controller.DisableAsync(Brick.Id));

    /// <summary>Moves the brick to its newest version.</summary>
    [Button]
    public void Update() => Act(() => Controller.UpdateAsync(Brick.Id));

    /// <summary>Copies the brick into the project's Bricks folder so it can be edited and versioned with the project.</summary>
    [Button]
    public void MakeLocal() => Act(() => Controller.EmbedAsync(Brick.Id));
}

/// <summary>A brick that lives in the project's Bricks folder.</summary>
public sealed class LocalBrickView(BricksController controller, CatalogBrick brick, ResolvedPackage resolved)
    : BrickView(controller, brick, resolved)
{
    /// <summary>Uses the shared version again; the local copy moves to the project's trash.</summary>
    [Button]
    public void RevertToGlobal() => Act(() => Controller.RevertAsync(Brick.Id));

    /// <summary>Deletes the project's copy; it moves to the project's trash.</summary>
    [Button]
    public void Uninstall() => Act(() => Controller.UninstallAsync(Brick.Id));
}
