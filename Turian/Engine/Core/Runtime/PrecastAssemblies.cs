namespace Turian.Engine.Core;

/// <summary>
/// Loads the prebuilt assemblies of the installed bricks in a game, which the export lists in its type manifest.
/// </summary>
public static class PrecastAssemblies
{
    /// <summary>The manifest property that lists the assembly names.</summary>
    public const string ManifestProperty = "PrecastAssemblies";

    /// <summary>
    /// Loads each listed assembly by name and registers its <see cref="TypeIdAttribute"/> types. A missing manifest
    /// or list loads nothing.
    /// </summary>
    /// <param name="manifestPath">The game's <c>usercode.typeids.json</c>.</param>
    /// <param name="logger">Receives assemblies that cannot be loaded.</param>
    public static void Load(string manifestPath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullPath = Path.GetFullPath(manifestPath);
        Load(new FileContentDirectory(Path.GetDirectoryName(fullPath)!), Path.GetFileName(fullPath), logger);
    }

    /// <summary>
    /// Loads each assembly the manifest in <paramref name="content"/> lists by name and registers its
    /// <see cref="TypeIdAttribute"/> types. A missing manifest or list loads nothing.
    /// </summary>
    /// <param name="content">The location holding the manifest, such as the folder beside the game.</param>
    /// <param name="manifest">The manifest path relative to <paramref name="content"/>.</param>
    /// <param name="logger">Receives assemblies that cannot be loaded.</param>
    public static void Load(IContentDirectory content, string manifest, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(logger);
        if (!content.Exists(manifest))
        {
            return;
        }

        using var document = JsonDocument.Parse(content.ReadAllText(manifest));
        if (!document.RootElement.TryGetProperty(ManifestProperty, out var names))
        {
            return;
        }

        foreach (var name in names.EnumerateArray().Select(static e => e.GetString()).OfType<string>())
        {
            try
            {
                TypeRegistry.ScanAssembly(Assembly.Load(new AssemblyName(name)));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                logger.LogWarning(ex, "Could not load brick assembly {AssemblyName}", name);
            }
        }
    }
}
