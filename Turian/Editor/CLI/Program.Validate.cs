namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command ValidateCommand(Argument<FileSystemInfo> projectArg)
    {
        var command = new Command("validate", "Validate authored DataAssets across the project") { projectArg };
        command.SetAction(async (result, cancellationToken) =>
        {
            var settings = CreateSettings(result.GetValue(projectArg)!);
            using var preparation = await PrepareProjectAsync(settings, reimport: false, loadUserCode: true,
                requireUserCode: true).ConfigureAwait(false);
            var projectRoot = settings.ProjectAbsoluteDir;
            var packages = ProjectPackages.ResolveOrEmpty(projectRoot);
            var roots = packages.Select(static package => package.RootPath).Prepend(settings.AssetsAbsoluteDir);
            var errors = DataAssetValidation.ValidateProject(roots, cancellationToken);
            foreach (var error in errors)
            {
                var path = Path.GetRelativePath(projectRoot, error.Path);
                var location = error.Member is { } member ? $"{path}: {member}" : path;
                Console.Error.WriteLine($"{location}: {error.Message}");
            }

            Console.WriteLine(errors.Count == 0 ? "DataAsset validation passed."
                : $"DataAsset validation failed with {errors.Count} error(s).");
            return errors.Count == 0 ? 0 : 1;
        });
        return command;
    }
}
