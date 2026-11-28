namespace Turian.Engine.Core;

/// <summary>
/// Runs the module initializer of a freshly loaded brick or user assembly and remembers the ones that fail, so a
/// missing dependency skips that assembly instead of ending the process the first time one of its types is touched.
/// </summary>
public static class BrokenAssemblies
{
    static readonly ConditionalWeakTable<Assembly, object> Broken = [];
    static readonly object Marker = new();

    /// <summary>Whether <paramref name="assembly"/> failed to load or initialize and must not be used.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <returns>True for an assembly <see cref="Initialize"/> or <see cref="Mark"/> rejected.</returns>
    public static bool IsBroken(Assembly assembly) => Broken.TryGetValue(assembly, out _);

    /// <summary>Runs the module initializer of <paramref name="assembly"/>, logging and marking it when that fails.</summary>
    /// <param name="assembly">The loaded assembly.</param>
    /// <param name="logger">Receives one warning naming the assembly and what it could not find.</param>
    /// <returns>True when the assembly is usable.</returns>
    public static bool Initialize(Assembly assembly, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(logger);
        if (IsBroken(assembly))
        {
            return false;
        }

        try
        {
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
            return true;
        }
        catch (Exception ex) when (IsLoadFailure(ex))
        {
            Mark(assembly, ex, logger);
            return false;
        }
    }

    /// <summary>Marks <paramref name="assembly"/> as unusable and logs it once.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="failure">What failed.</param>
    /// <param name="logger">Receives the warning.</param>
    public static void Mark(Assembly assembly, Exception failure, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(logger);
        if (Broken.TryGetValue(assembly, out _))
        {
            return;
        }

        Broken.AddOrUpdate(assembly, Marker);
        logger.LogWarning("Skipping assembly {Assembly}: it needs {Missing}, which cannot be loaded ({Reason})",
            assembly.GetName().Name, MissingAssembly(failure) ?? "a type or assembly it cannot resolve", failure.GetBaseException().Message);
    }

    /// <summary>Whether <paramref name="exception"/> is a type, assembly or initializer load failure.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns>True for the failures a missing dependency causes.</returns>
    public static bool IsLoadFailure(Exception exception) =>
        exception is TypeInitializationException or TypeLoadException or FileNotFoundException or FileLoadException
            or BadImageFormatException or ReflectionTypeLoadException;

    /// <summary>The first assembly a failure's inner exceptions report as missing.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns>The assembly name, or null when none is named.</returns>
    public static string? MissingAssembly(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case FileNotFoundException { FileName: { } missing }:
                    return missing;
                case FileLoadException { FileName: { } rejected }:
                    return rejected;
                case ReflectionTypeLoadException { LoaderExceptions: { } loaders }
                    when loaders.OfType<Exception>().Select(MissingAssembly).FirstOrDefault(static n => n is not null) is { } found:
                    return found;
            }
        }

        return null;
    }
}
