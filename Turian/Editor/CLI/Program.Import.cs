using Gaya.Packages;

namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command ImportCommand() =>
        new("import", "Bring assets from other tools into a project or a brick")
        {
            ImportUnityPackageCommand(),
        };

    static Command ImportUnityPackageCommand()
    {
        var fileArg = new Argument<FileInfo>("package") { Description = "The .unitypackage file" };
        var projectOption = new Option<DirectoryInfo?>("--project") { Description = "Import into this project's Assets folder" };
        var folderOption = new Option<string?>("--folder") { Description = "With --project: the folder under Assets; the package's name when omitted" };
        var brickOption = new Option<string?>("--brick") { Description = "Import into a new brick with this id instead, such as user.you.crates" };
        var outOption = new Option<DirectoryInfo?>("--out") { Description = "With --brick: the folder the brick folder is created in" };

        var command = new Command("unitypackage", "Import a Unity package: textures, models and audio with their ids, materials, prefabs and scenes as far as they map")
        {
            fileArg, projectOption, folderOption, brickOption, outOption,
        };
        command.SetAction(result =>
        {
            try
            {
                var file = result.GetValue(fileArg)!;
                var name = Path.GetFileNameWithoutExtension(file.Name);
                UnityImportTarget target;
                string where;

                if (result.GetValue(brickOption) is { } id)
                {
                    if (!PackageId.IsValid(id)) throw new PackageException($"'{id}' is not a valid brick id (lowercase reverse-DNS, e.g. user.you.crates).");
                    var root = Path.Combine((result.GetValue(outOption) ?? new DirectoryInfo("./")).FullName, id);
                    if (Directory.Exists(root)) throw new PackageException($"{root} already exists.");

                    Directory.CreateDirectory(root);
                    new PackageManifest { Name = id, Version = SemanticVersion.Parse("0.1.0"), DisplayName = name, License = "UNLICENSED" }.Save(root);
                    target = new UnityImportTarget(Path.Combine(root, "Runtime"), root);
                    where = $"brick {root}";
                }
                else if (result.GetValue(projectOption) is { } project)
                {
                    target = new UnityImportTarget(Path.Combine(project.FullName, "Assets", result.GetValue(folderOption) ?? name), project.FullName);
                    where = $"{target.Folder}";
                }
                else
                {
                    throw new PackageException("Say where to import: --project <folder>, or --brick <id> --out <folder>.");
                }

                var report = UnityPackageImporter.Import(file.FullName, target);
                foreach (var entry in report.Converted) Console.WriteLine($"{entry.Source} -> {entry.Target}{(entry.Note is null ? string.Empty : $"  ({entry.Note})")}");
                foreach (var entry in report.Skipped) Console.Error.WriteLine($"skipped {entry.Source}: {entry.Note}");
                foreach (var note in report.Notes) Console.WriteLine(note);
                Console.WriteLine($"Imported {report.Converted.Count} asset(s) into {where}; skipped {report.Skipped.Count}.");
                Console.WriteLine(UnityPackageImporter.LicenseNotice);
                return 0;
            }
            catch (Exception ex) when (ex is PackageException or InvalidDataException or IOException)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        });
        return command;
    }
}
