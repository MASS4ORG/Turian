namespace Turian.Engine.Core;

/// <summary>
/// Maps stable <see cref="Guid"/> identifiers (declared via <see cref="TypeIdAttribute"/>)
/// to runtime <see cref="Type"/> instances and vice versa. Used by the polymorphic JSON
/// serializers so that serialized data survives renames, namespace changes and assembly
/// moves of the annotated classes.
/// </summary>
public static class TypeRegistry
{
    static readonly ConcurrentDictionary<Guid, Type> idToType = new();
    static readonly ConcurrentDictionary<Type, Guid> typeToId = new();
    static readonly object scanLock = new();
    static readonly HashSet<Assembly> scannedAssemblies = [];

    static TypeRegistry()
    {
        ScanLoadedAssemblies();
    }

    /// <summary>
    /// Scans every assembly currently loaded in the AppDomain for classes annotated with
    /// <see cref="TypeIdAttribute"/> and registers them. Already-scanned assemblies are
    /// skipped, but registrations are overwritten with the most recently scanned type so
    /// hot-reloaded user code wins over stale assemblies kept alive by lingering instances.
    /// </summary>
    public static void ScanLoadedAssemblies()
    {
        lock (scanLock)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                ScanAssembly(assembly);
            }
        }
    }

    /// <summary>
    /// Scans a specific assembly for <see cref="TypeIdAttribute"/>-annotated classes and
    /// registers them.
    /// </summary>
    public static void ScanAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        lock (scanLock)
        {
            if (!scannedAssemblies.Add(assembly))
            {
                return;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = [.. ex.Types.Where(t => t is not null).Cast<Type>()];
            }
            catch
            {
                return;
            }

            foreach (var type in types)
            {
                // GetCustomAttribute resolves the attribute's own type, which can need loading a
                // dependency the scanned assembly references but that isn't present — e.g. an MSBuild-
                // pulled-in assembly like System.Composition.AttributedModel that a plain CLI process
                // never otherwise touches. That must not take down a scan of every loaded assembly.
                TypeIdAttribute? attr;
                try
                {
                    attr = type.GetCustomAttribute<TypeIdAttribute>(inherit: false);
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException)
                {
                    continue;
                }

                if (attr is null)
                {
                    continue;
                }

                Register(attr.Id, type);
            }
        }
    }

    /// <summary>
    /// Registers a <see cref="Type"/> with its stable id. Subsequent registrations for the
    /// same id overwrite previous entries (this matters for hot-reload of user code, where
    /// a recompiled assembly produces a new <see cref="Type"/> instance for the same id).
    /// </summary>
    public static void Register(Guid id, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("TypeId cannot be Guid.Empty.", nameof(id));
        }

        idToType[id] = type;
        typeToId[type] = id;
    }

    /// <summary>
    /// Tries to look up a registered <see cref="Type"/> by its stable id.
    /// </summary>
    public static bool TryGetType(Guid id, out Type? type)
    {
        if (idToType.TryGetValue(id, out type))
        {
            return true;
        }

        // An assembly annotated with this id may have loaded lazily since the last scan
        // (e.g. Turian.Engine.UI pulled in only when the first .ui asset is imported).
        ScanLoadedAssemblies();
        return idToType.TryGetValue(id, out type);
    }

    /// <summary>
    /// Tries to look up a registered <see cref="Type"/> by its full name, the form asset catalogs
    /// record. Only current registrations are searched, so hot-reloaded user code wins.
    /// </summary>
    public static bool TryGetType(string fullName, out Type? type)
    {
        type = idToType.Values.FirstOrDefault(candidate => candidate.FullName == fullName);
        if (type is not null)
        {
            return true;
        }

        ScanLoadedAssemblies();
        type = idToType.Values.FirstOrDefault(candidate => candidate.FullName == fullName);
        return type is not null;
    }

    /// <summary>
    /// Tries to look up the stable id assigned to a <see cref="Type"/>.
    /// </summary>
    public static bool TryGetId(Type type, out Guid id)
    {
        ArgumentNullException.ThrowIfNull(type);
        return typeToId.TryGetValue(type, out id);
    }

    /// <summary>
    /// Resolves a registered <see cref="Type"/> by id, or throws if no type is registered
    /// for the given id.
    /// </summary>
    public static Type GetTypeOrThrow(Guid id)
    {
        if (TryGetType(id, out var type) && type is not null)
        {
            return type;
        }

        throw new InvalidOperationException(
            $"No type registered with TypeId '{id}'. The originating class may have been removed " +
            "or its [TypeId] attribute changed.");
    }

    /// <summary>
    /// Returns the stable id assigned to a <see cref="Type"/>, or throws if the type is
    /// not annotated with <see cref="TypeIdAttribute"/>.
    /// </summary>
    public static Guid GetIdOrThrow(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (typeToId.TryGetValue(type, out var id))
        {
            return id;
        }

        var attr = type.GetCustomAttribute<TypeIdAttribute>(inherit: false);
        if (attr is not null)
        {
            Register(attr.Id, type);
            return attr.Id;
        }

        throw new InvalidOperationException(
            $"Type '{type.FullName}' is not annotated with [TypeId(\"...\")] and cannot be " +
            "polymorphically serialized. Add a stable [TypeId] attribute to the class.");
    }

    /// <summary>
    /// Clears all registrations and re-scans currently loaded assemblies. Intended to be
    /// called after user code is recompiled and reloaded.
    /// </summary>
    public static void Reset()
    {
        lock (scanLock)
        {
            idToType.Clear();
            typeToId.Clear();
            scannedAssemblies.Clear();
        }
        ScanLoadedAssemblies();
    }

    /// <summary>
    /// Loads a <c>usercode.typeids.json</c> manifest from <paramref name="manifestPath"/> and
    /// registers every entry into <see cref="TypeRegistry"/> by searching all currently loaded
    /// assemblies. Intended for use in play/export runtimes where user code is compiled as a
    /// static project reference (so no <c>[TypeId]</c> attributes exist on user types).
    /// </summary>
    public static void RegisterFromManifest(string manifestPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (!File.Exists(manifestPath))
        {
            logger.LogDebug("User-code type manifest not found at {Path}; user types will not be registered", manifestPath);
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse user-code type manifest at {Path}", manifestPath);
            return;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("Types", out var typesArray))
                return;

            // Force-load any DLLs in the manifest directory that aren't yet in the AppDomain.
            // In play/export mode the user-code assembly is a project reference and loads lazily;
            // without this, GetAssemblies() won't include it and all FQN lookups silently fail.
            var manifestDir = Path.GetDirectoryName(manifestPath);
            if (manifestDir is not null)
            {
                var loadedLocations = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .Select(a => a.Location)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var dll in Directory.GetFiles(manifestDir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    if (loadedLocations.Contains(dll)) continue;
                    try { Assembly.LoadFrom(dll); }
                    catch { /* ignore non-managed or already-loaded DLLs */ }
                }
            }

            // A single-file game bundles the user assembly, so there is no DLL above to load; it is
            // loaded by name instead.
            if (doc.RootElement.TryGetProperty("AssemblyName", out var assemblyNameProp)
                && assemblyNameProp.GetString() is { Length: > 0 } assemblyName)
            {
                try { Assembly.Load(new AssemblyName(assemblyName)); }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    logger.LogWarning(ex, "Could not load user assembly {AssemblyName}", assemblyName);
                }
            }

            var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            var registered = 0;

            foreach (var entry in typesArray.EnumerateArray())
            {
                var fqn = entry.TryGetProperty("FullyQualifiedName", out var fqnProp) ? fqnProp.GetString() : null;
                var tidStr = entry.TryGetProperty("TypeId", out var tidProp) ? tidProp.GetString() : null;

                if (string.IsNullOrEmpty(fqn) || !Guid.TryParse(tidStr, out var typeId))
                    continue;

                Type? type = null;
                foreach (var asm in allAssemblies)
                {
                    type = asm.GetType(fqn);
                    if (type is not null) break;
                }

                if (type is null)
                {
                    logger.LogWarning("User type '{Fqn}' not found in any loaded assembly; skipping", fqn);
                    continue;
                }

                Register(typeId, type);
                registered++;
                logger.LogDebug("TypeRegistry: {Fqn} → {TypeId}", fqn, typeId);
            }

            logger.LogInformation("Registered {Count}/{Total} user type(s) from manifest", registered, typesArray.GetArrayLength());
        }
    }

    /// <summary>
    /// Returns the count of currently registered types. Mainly useful for diagnostics
    /// and tests.
    /// </summary>
    public static int Count => idToType.Count;
}
