namespace Turian.Editor.Core;

/// <summary>
/// Manages two fixed output slots (A / B) so that a newly compiled assembly can be
/// swapped in without leaving stale timestamped DLLs on disk.
///
/// Strategy:
///   - Changed code is compiled into the <em>inactive</em> slot; unchanged code may reuse either slot.
///   - On a successful load the selected slot becomes active.
///   - On a load failure the active slot is left unchanged (automatic revert).
///   - The current active slot name is persisted in <c>active_slot.txt</c> beside
///     the slot directories so it survives editor restarts.
/// </summary>
public sealed class AssemblySlotManager
{
    const string slotA = "SlotA";
    const string slotB = "SlotB";
    const string activeSlotFile = "active_slot.txt";

    readonly string slotRootDirectory;
    readonly ILogger logger;

    UserAssemblyLoadContext? loadContext;
    string activeSlot;

    /// <summary>Path of the currently loaded assembly, or <c>null</c> if nothing is loaded.</summary>
    public string? LoadedAssemblyPath { get; private set; }

    /// <summary>The assembly at <see cref="LoadedAssemblyPath"/>, or <c>null</c> if nothing is loaded.</summary>
    public Assembly? LoadedAssembly { get; private set; }

    /// <summary>The assemblies compiled from the project's scripts, <see cref="LoadedAssembly"/> last.</summary>
    public IReadOnlyList<Assembly> UserAssemblies { get; private set; } = [];

    /// <param name="slotRootDirectory">
    /// Directory that will contain the <c>SlotA/</c> and <c>SlotB/</c> subdirectories.
    /// Typically <c>&lt;cache&gt;/bin/</c>.
    /// </param>
    /// <param name="logger"></param>
    public AssemblySlotManager(string slotRootDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotRootDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        this.slotRootDirectory = Path.GetFullPath(slotRootDirectory);
        this.logger = logger;

        Directory.CreateDirectory(SlotPath(slotA));
        Directory.CreateDirectory(SlotPath(slotB));

        activeSlot = ReadPersistedActiveSlot();
    }

    /// <summary>
    /// Returns the output directory that should be used for the <em>next</em> compilation.
    /// </summary>
    public string InactiveSlotDirectory => SlotPath(activeSlot == slotA ? slotB : slotA);

    /// <summary>
    /// Returns the output directory of the currently active (loaded) slot.
    /// </summary>
    public string ActiveSlotDirectory => SlotPath(activeSlot);

    /// <summary>
    /// Attempts to load the assembly at <paramref name="assemblyPath"/>, with every other assembly in its slot,
    /// into a fresh <see cref="UserAssemblyLoadContext"/>.  On success, unloads the previous context
    /// and promotes the new slot to active.  On any failure the previous state is preserved.
    /// </summary>
    /// <returns><c>true</c> if the assembly was loaded successfully.</returns>
    public bool TrySwapAndLoad(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        var newContext = new UserAssemblyLoadContext();
        try
        {
            newContext.SetResolver(assemblyPath);

            // Every assembly the compile wrote to the slot: the definitions first, the default one last.
            var fullPath = Path.GetFullPath(assemblyPath);
            var userAssemblies = new List<Assembly>();
            foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(fullPath)!, "*.dll")
                         .Where(path => !string.Equals(Path.GetFullPath(path), fullPath, StringComparison.Ordinal))
                         .Order(StringComparer.Ordinal))
            {
                userAssemblies.Add(newContext.LoadFromAssemblyPath(path));
            }

            var primary = newContext.LoadFromAssemblyPath(fullPath);
            userAssemblies.Add(primary);

            // Success – unload the old context first
            UnloadCurrentContext();

            loadContext = newContext;
            LoadedAssemblyPath = assemblyPath;
            LoadedAssembly = primary;
            UserAssemblies = userAssemblies;

            activeSlot = Path.GetFileName(Path.GetDirectoryName(fullPath))!;
            PersistActiveSlot(activeSlot);

            Serializer.ResetOptions();
            UserCodeTypeManifest.RegisterFromManifest(assemblyPath, userAssemblies, logger);

            logger.LogInformation("Assembly swapped to {Slot}: {Path}", activeSlot, assemblyPath);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load assembly from {Path}. Reverting to previous slot", assemblyPath);

            try
            {
                Release(newContext);
                newContext.Unload();
            }
            catch { /* best effort */ }

            return false;
        }
    }

    /// <summary>
    /// Unloads the current assembly context and clears the state.
    /// </summary>
    public void Unload()
    {
        UnloadCurrentContext();
        LoadedAssemblyPath = null;
        LoadedAssembly = null;
        UserAssemblies = [];
        logger.LogInformation("Assembly unloaded");
    }

    /// <summary>Returns all assemblies currently loaded in the active context.</summary>
    public IEnumerable<Assembly> LoadedAssemblies =>
        loadContext?.Assemblies ?? [];

    // -------------------------------------------------------------------------

    void UnloadCurrentContext()
    {
        if (loadContext is null) return;

        try
        {
            Release(loadContext);
            loadContext.Unload();
            loadContext = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error while unloading previous assembly context");
        }
    }

    // Static caches holding the context's types would keep it from ever being collected.
    static void Release(UserAssemblyLoadContext context)
    {
        CollectibleAssemblies.Release(context.Assemblies.ToArray());
        ObjectState.ReleaseCollectible();
    }

    string SlotPath(string slot) => Path.Combine(slotRootDirectory, slot);

    string ActiveSlotFilePath => Path.Combine(slotRootDirectory, activeSlotFile);

    string ReadPersistedActiveSlot()
    {
        try
        {
            if (File.Exists(ActiveSlotFilePath))
            {
                var text = File.ReadAllText(ActiveSlotFilePath).Trim();
                if (text == slotA || text == slotB)
                    return text;
            }
        }
        catch { /* ignore – default to SlotA */ }

        return slotA;
    }

    void PersistActiveSlot(string slot)
    {
        try { File.WriteAllText(ActiveSlotFilePath, slot); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not persist active slot"); }
    }
}
