using MASS4.Bricks;

namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command BrickTwinsCommand(Option<DirectoryInfo> projectOption)
    {
        var stateArg = new Argument<string>("state") { Description = "on: take the bake twin of every registry brick that has a trusted one; off: take sources" };
        stateArg.AcceptOnlyFromAmong("on", "off");

        var command = new Command("twins", "Take registry bricks as their prebuilt and imported bake twins, so they neither compile nor import here")
        {
            stateArg, projectOption,
        };
        command.SetAction(result => RunBrick(() =>
        {
            var resolution = BrickService.SetTwins(result.GetValue(projectOption)!.FullName, result.GetValue(stateArg) == "on");
            PrintForms(resolution);
        }));
        return command;
    }

    static Command BrickSwapCommand(Option<DirectoryInfo> projectOption)
    {
        var idArg = new Argument<string>("id") { Description = "The brick's id, or its twin's" };
        var toArg = new Argument<string>("to") { Description = "source: download and use the brick's source, to change it; twin: go back to its bake twin" };
        toArg.AcceptOnlyFromAmong("source", "twin");

        var command = new Command("swap", "Use a brick's source in place of its bake twin, or the other way round; asset references survive the swap")
        {
            idArg, toArg, projectOption,
        };
        command.SetAction(result => RunBrick(() =>
        {
            var resolution = BrickService.Swap(result.GetValue(projectOption)!.FullName, result.GetValue(idArg)!,
                toSource: result.GetValue(toArg) == "source");
            PrintForms(resolution);
        }));
        return command;
    }

    static Command BrickBakeCommand()
    {
        var pathArg = new Argument<DirectoryInfo>("path")
        {
            Description = "Brick folder",
            DefaultValueFactory = _ => new DirectoryInfo("./"),
        };

        var command = new Command("bake", $"Import the brick's assets headlessly into {BrickBake.Folder}, which its bake twin ships so projects mount them instead of importing")
        {
            pathArg,
        };
        command.SetAction(async (result, _) => await RunBrickAsync(async () =>
        {
            var sync = await BrickBake.BakeAsync(result.GetValue(pathArg)!.FullName, Log.Logger).ConfigureAwait(false);
            Console.WriteLine($"Baked {sync.Entries.Count} asset(s) of {sync.Brick} {sync.Version}");
        }).ConfigureAwait(false));
        return command;
    }

    /// <summary>Prints which bricks resolved as twins and which as sources.</summary>
    static void PrintForms(PackageResolution resolution)
    {
        foreach (var brick in resolution.Packages.Where(static p => p.Origin is PackageOrigin.Registry or PackageOrigin.Embedded or PackageOrigin.Link))
        {
            Console.WriteLine(brick.Manifest.BakeOf is { } bakeOf
                ? $"  {bakeOf.Brick} {bakeOf.Version}: twin {brick.Id}"
                : brick.Origin == PackageOrigin.Link
                    ? $"  {brick.Id} {brick.Version}: source, linked to {brick.RootPath}"
                : brick.Origin == PackageOrigin.Embedded
                    ? $"  {brick.Id} {brick.Version}: source, editable in {ProjectManifest.DirectoryName}/{brick.Id}"
                    : $"  {brick.Id} {brick.Version}: source");
        }
    }
}
