namespace Turian.Engine.Core;

/// <summary>
/// Maps stable <see cref="System.Guid"/> identifiers (declared via <see cref="TypeIdAttribute"/>)
/// to runtime <see cref="System.Type"/> instances and vice versa. Used by the polymorphic JSON
/// serializers so that serialized data survives renames, namespace changes and assembly
/// moves of the annotated classes.
/// </summary>
public static class TypeRegistry
{
    static readonly ConcurrentDictionary<Guid, Type> IdToType = new();
    static readonly ConcurrentDictionary<Type, Guid> TypeToId = new();
    static readonly object ScanLock = new();
    static readonly HashSet<Assembly> ScannedAssemblies = [];

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
        lock (ScanLock)
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

        lock (ScanLock)
        {
            if (!ScannedAssemblies.Add(assembly))
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
    /// Registers a <see cref="System.Type"/> with its stable id. Subsequent registrations for the
    /// same id overwrite previous entries (this matters for hot-reload of user code, where
    /// a recompiled assembly produces a new <see cref="System.Type"/> instance for the same id).
    /// </summary>
    public static void Register(Guid id, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("TypeId cannot be Guid.Empty.", nameof(id));
        }

        IdToType[id] = type;
        TypeToId[type] = id;
    }

    /// <summary>
    /// Tries to look up a registered <see cref="System.Type"/> by its stable id.
    /// </summary>
    public static bool TryGetType(Guid id, out Type? type)
    {
        if (IdToType.TryGetValue(id, out type))
        {
            return true;
        }

        // An assembly annotated with this id may have loaded lazily since the last scan
        // (e.g. Turian.Engine.UI pulled in only when the first .ui asset is imported).
        ScanLoadedAssemblies();
        return IdToType.TryGetValue(id, out type);
    }

    /// <summary>
    /// Tries to look up a registered <see cref="System.Type"/> by its full name, the form asset catalogs
    /// record. Only current registrations are searched, so hot-reloaded user code wins.
    /// </summary>
    public static bool TryGetType(string fullName, out Type? type)
    {
        type = IdToType.Values.FirstOrDefault(candidate => candidate.FullName == fullName);
        if (type is not null)
        {
            return true;
        }

        ScanLoadedAssemblies();
        type = IdToType.Values.FirstOrDefault(candidate => candidate.FullName == fullName);
        return type is not null;
    }

    /// <summary>
    /// Tries to look up the stable id assigned to a <see cref="System.Type"/>.
    /// </summary>
    public static bool TryGetId(Type type, out Guid id)
    {
        ArgumentNullException.ThrowIfNull(type);
        return TypeToId.TryGetValue(type, out id);
    }

    /// <summary>
    /// Resolves a registered <see cref="System.Type"/> by id, or throws if no type is registered
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
    /// Returns the stable id assigned to a <see cref="System.Type"/>, or throws if the type is
    /// not annotated with <see cref="TypeIdAttribute"/>.
    /// </summary>
    public static Guid GetIdOrThrow(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (TypeToId.TryGetValue(type, out var id))
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
        lock (ScanLock)
        {
            IdToType.Clear();
            TypeToId.Clear();
            ScannedAssemblies.Clear();
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

        using var doc = ReadManifest(manifestPath, logger);
        if (doc is null || !doc.RootElement.TryGetProperty("Types", out var typesArray)) return;

        // In play/export mode the user-code assembly is a project reference and loads lazily;
        // without this, GetAssemblies() won't include it and all FQN lookups silently fail.
        if (Path.GetDirectoryName(manifestPath) is { } manifestDir) LoadAssembliesIn(manifestDir);

        // A single-file game bundles the user assembly, so there is no DLL above to load; it is
        // loaded by name instead.
        if (doc.RootElement.TryGetProperty("AssemblyName", out var assemblyNameProp)
            && assemblyNameProp.GetString() is { Length: > 0 } assemblyName)
            LoadAssemblyByName(assemblyName, logger);

        var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        var registered = typesArray.EnumerateArray().Count(entry => RegisterManifestEntry(entry, allAssemblies, logger));

        logger.LogInformation("Registered {Count}/{Total} user type(s) from manifest", registered,
            typesArray.GetArrayLength());
    }

    static JsonDocument? ReadManifest(string manifestPath, ILogger logger)
    {
        if (!File.Exists(manifestPath))
        {
            logger.LogDebug("User-code type manifest not found at {Path}; user types will not be registered",
                manifestPath);
            return null;
        }

        try
        {
            return JsonDocument.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse user-code type manifest at {Path}", manifestPath);
            return null;
        }
    }

    static void LoadAssembliesIn(string directory)
    {
        var loadedLocations = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .Select(a => a.Location)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (loadedLocations.Contains(dll)) continue;
            try { Assembly.LoadFrom(dll); }
            catch { /* ignore non-managed or already-loaded DLLs */ }
        }
    }

    static void LoadAssemblyByName(string assemblyName, ILogger logger)
    {
        try { Assembly.Load(new AssemblyName(assemblyName)); }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            logger.LogWarning(ex, "Could not load user assembly {AssemblyName}", assemblyName);
        }
    }

    static bool RegisterManifestEntry(JsonElement entry, Assembly[] assemblies, ILogger logger)
    {
        var fqn = ReadString(entry, "FullyQualifiedName");
        if (string.IsNullOrEmpty(fqn) || !Guid.TryParse(ReadString(entry, "TypeId"), out var typeId)) return false;

        // A game ships without its editor-only assemblies.
        if (entry.TryGetProperty("EditorOnly", out var editorOnly) && editorOnly.ValueKind == JsonValueKind.True)
            return false;

        // A single-file game loads a referenced user assembly only on first use, so load it by name.
        if (ReadString(entry, "Assembly") is { Length: > 0 } assemblyName
            && assemblies.All(asm => asm.GetName().Name != assemblyName))
        {
            LoadAssemblyByName(assemblyName, logger);
            assemblies = AppDomain.CurrentDomain.GetAssemblies();
        }

        if (assemblies.Select(asm => asm.GetType(fqn)).FirstOrDefault(candidate => candidate is not null) is not { } type)
        {
            logger.LogWarning("User type '{Fqn}' not found in any loaded assembly; skipping", fqn);
            return false;
        }

        Register(typeId, type);
        logger.LogDebug("TypeRegistry: {Fqn} → {TypeId}", fqn, typeId);
        return true;
    }

    static string? ReadString(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) ? value.GetString() : null;

    /// <summary>
    /// Returns the count of currently registered types. Mainly useful for diagnostics
    /// and tests.
    /// </summary>
    public static int Count => IdToType.Count;
}
