namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command RegenerateCommand(Argument<FileSystemInfo> projectArg)
    {
        var command = new Command("regenerate",
            "Regenerate the project and solution files an external IDE opens, without compiling")
        {
            projectArg,
        };
        command.SetAction(parsed =>
        {
            var files = ProjectRegeneration.Regenerate(CreateSettings(parsed.GetValue(projectArg)!), Log.Logger);
            Console.WriteLine(files.Solution);
            foreach (var project in files.Projects)
            {
                Console.WriteLine(project);
            }

            Log.Logger.LogInformation(files.Changed ? "Generated files updated" : "Generated files already up to date");
        });
        return command;
    }
}
