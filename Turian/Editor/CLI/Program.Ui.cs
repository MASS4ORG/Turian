using Gaya.Packages;

namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command UiCommand()
    {
        var outputOption = new Option<string>("--out") { Description = "PNG file to write", DefaultValueFactory = _ => "ui.png" };
        var widthOption = new Option<int>("--width") { Description = "Output width in pixels", DefaultValueFactory = _ => 1280 };
        var heightOption = new Option<int>("--height") { Description = "Output height in pixels", DefaultValueFactory = _ => 720 };
        var fileOption = new Option<string?>("--file") { Description = "Render a .ui document", Required = true };
        var dataOption = new Option<string?>("--data") { Description = "JSON object (inline or @path) bound as the document's data context" };

        var command = new Command("ui", "Render a .ui document to a PNG (no scene, no GPU) with the in-game UI brick")
        {
            outputOption, widthOption, heightOption, fileOption, dataOption,
        };
        command.SetAction(result => RunBrick(() =>
        {
            // The preview belongs to the UI brick, which ships with the engine, so no project is needed.
            var ui = ProjectPackages.Fetch("org.mass4.turian.ui", new BuiltinSource("org.mass4.turian.ui"));
            var brick = new ResolvedPackage("org.mass4.turian.ui", PackageManifest.Load(ui.Folder, ["gaya", ProjectPackages.HostName]), ui.Folder,
                PackageOrigin.Builtin, "builtin:org.mass4.turian.ui", null, null, 1, false);
            BrickAssemblies.Load([brick], Log.Logger);

            var preview = UiPresenters.FindPreview() ?? throw new PackageException("The in-game UI brick is not available.");
            var data = result.GetValue(dataOption);
            var png = preview.RenderPng(result.GetValue(fileOption)!, result.GetValue(widthOption), result.GetValue(heightOption),
                data is not null && data.StartsWith('@') ? File.ReadAllText(data[1..]) : data);

            var output = Path.GetFullPath(result.GetValue(outputOption)!);
            File.WriteAllBytes(output, png);
            Log.Logger.LogInformation("Wrote {Width}×{Height} UI render: {OutputPath}", result.GetValue(widthOption), result.GetValue(heightOption), output);
        }));
        return command;
    }
}
