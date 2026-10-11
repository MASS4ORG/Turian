namespace Gaya.Host;

/// <summary>
/// Feeds the studio bricks' theme folders to the theme service, at startup and after every change made through the
/// Bricks panel or the command line, so a content-only brick applies without a restart.
/// </summary>
public static class StudioThemeBricks
{
    /// <summary>Scans the studio's theme bricks now and again whenever the studio's bricks change.</summary>
    /// <param name="studio">The studio's bricks.</param>
    /// <param name="themes">The service whose catalog scans the brick folders.</param>
    /// <param name="logger">Receives bricks that cannot be resolved, and plugins that need a restart.</param>
    public static void Bind(StudioBricks studio, ThemeService themes, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(studio);
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(logger);

        var initial = Scan(studio, logger);
        var loaded = initial is null ? [] : Libraries(initial);
        if (initial is not null) themes.SetBrickFolders(PackagedPlugins.ThemeFolders(initial));

        studio.Applied += _ =>
        {
            if (Scan(studio, logger) is not { } packages) return;
            themes.QueueBrickFolders(PackagedPlugins.ThemeFolders(packages));
            foreach (var added in Libraries(packages).Except(loaded))
                logger.LogInformation("Studio brick {Package} adds plugins; restart to load them", added);
        };
    }

    static IReadOnlyList<ResolvedPackage>? Scan(StudioBricks studio, ILogger logger)
    {
        try
        {
            return studio.Resolve();
        }
        catch (PackageException ex)
        {
            logger.LogError(ex, "Studio bricks could not be resolved");
            return null;
        }
    }

    static HashSet<string> Libraries(IEnumerable<ResolvedPackage> packages) =>
    [
        .. packages.Where(package => Directory.Exists(Path.Combine(package.RootPath, PackagedPlugins.LibraryDirectoryName)))
            .Select(package => package.Id),
    ];
}
