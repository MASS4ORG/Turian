namespace Gaya.Host;

/// <summary>
/// Writes the icon files embedded in the host to a folder named after the build, so the built-in icon sheets can
/// reference them with <c>url()</c> like any brick does. Folders of older builds are removed.
/// </summary>
static class EmbeddedIcons
{
    const string ResourceRoot = "Gaya.Host.";
    const string IconsFolder = "Icons/";
    const string FolderPrefix = "icons-";
    const string CompleteMarker = ".complete";

    /// <summary>Extracts the embedded icons once per build.</summary>
    /// <param name="cacheFolder">The folder that holds extracted files.</param>
    /// <param name="log">Receives a failure to write the files.</param>
    /// <returns>A file URI inside the extracted folder that relative <c>url()</c> values resolve against, or <c>null</c>.</returns>
    public static Uri? Extract(string cacheFolder, ILogger log)
    {
        var assembly = typeof(EmbeddedIcons).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourceRoot + IconsFolder, StringComparison.Ordinal))
            .ToArray();
        if (names.Length == 0) return null;

        var build = assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];
        var root = Path.Combine(cacheFolder, FolderPrefix + build);
        try
        {
            if (!File.Exists(Path.Combine(root, CompleteMarker)))
            {
                foreach (var name in names)
                {
                    var target = Path.Combine(root, name[ResourceRoot.Length..]);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var source = assembly.GetManifestResourceStream(name)!;
                    using var file = File.Create(target);
                    source.CopyTo(file);
                }
                File.WriteAllBytes(Path.Combine(root, CompleteMarker), []);
            }
            RemoveOtherBuilds(cacheFolder, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Built-in icons could not be written to {Folder}", root);
            return null;
        }
        return new Uri(Path.Combine(root, "sheet.pss"));
    }

    static void RemoveOtherBuilds(string cacheFolder, string current)
    {
        foreach (var folder in Directory.EnumerateDirectories(cacheFolder, FolderPrefix + "*"))
        {
            if (string.Equals(folder, current, StringComparison.Ordinal)) continue;
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another running studio may still read an older build's icons; the next start retries.
            }
        }
    }
}
