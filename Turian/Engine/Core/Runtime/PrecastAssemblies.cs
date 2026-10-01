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
        ArgumentNullException.ThrowIfNull(logger);
        if (!File.Exists(manifestPath)) return;

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!document.RootElement.TryGetProperty(ManifestProperty, out var names)) return;

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
