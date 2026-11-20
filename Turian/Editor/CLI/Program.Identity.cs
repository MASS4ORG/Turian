namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command NewIdentityCommand(Argument<FileSystemInfo> projectArg)
    {
        var command = new Command("new-identity",
            "Give a copied or template-made project its own identity, so it does not share personal settings with the original")
        {
            projectArg,
        };
        command.SetAction(parsed =>
        {
            var project = CreateSettings(parsed.GetValue(projectArg)!);
            if (ProjectSettingsFiles.RenewIdentity(project) is not { } identity)
            {
                Log.Logger.LogError("{Path} has no {File} to give an identity to", project.ProjectAbsoluteDir,
                    ProjectAsset.FileName);
                return 1;
            }

            Console.WriteLine(identity.ToString("D"));
            return 0;
        });
        return command;
    }
}
