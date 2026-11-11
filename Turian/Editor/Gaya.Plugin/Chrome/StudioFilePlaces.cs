namespace Gaya.Plugin.Turian;

/// <summary>
/// The places the file dialog lists on Linux. Asking a mount whether it is ready can block forever when it is a stuck
/// FUSE service, and the dialog shows nothing until it has its places, so drives come from the mount table alone.
/// </summary>
static partial class StudioFilePlaces
{
    const string MountTable = "/proc/mounts";

    /// <summary>The places for this system, or null to keep the dialog's own defaults.</summary>
    /// <returns>The places to offer.</returns>
    public static IReadOnlyList<FilePlace>? ForThisSystem()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        FilePlace[] folders =
        [
            new("Home", home, WidgetIcons.House),
            new("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), WidgetIcons.Desktop),
            new("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), WidgetIcons.FileLines),
            new("Downloads", Path.Combine(home, "Downloads"), WidgetIcons.Download),
        ];
        var drives = File.Exists(MountTable) ? Drives(File.ReadLines(MountTable)) : [];
        return [.. folders.Where(static place => place.Path.Length > 0 && Directory.Exists(place.Path)), .. drives];
    }

    /// <summary>The block devices in a mount table; services such as FUSE and system partitions are left out.</summary>
    /// <param name="mountTable">The lines of the mount table.</param>
    /// <returns>A place for each drive.</returns>
    internal static IEnumerable<FilePlace> Drives(IEnumerable<string> mountTable) => mountTable
        .Select(static line => line.Split(' '))
        .Where(static fields => fields.Length >= 2 && fields[0].StartsWith("/dev/", StringComparison.Ordinal))
        .Select(static fields => OctalEscape().Replace(fields[1], static escape =>
            ((char)Convert.ToInt32(escape.Groups[1].Value, 8)).ToString()))
        .Where(static mount => mount != "/boot" && !mount.StartsWith("/boot/", StringComparison.Ordinal)
                               && !mount.StartsWith("/snap/", StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .Select(static mount => new FilePlace(mount == "/" ? "System" : Path.GetFileName(mount), mount));

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex OctalEscape();
}
