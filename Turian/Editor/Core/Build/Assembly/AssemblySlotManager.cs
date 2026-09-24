namespace Turian.Editor.Core;

/// <summary>
/// Manages two fixed output slots (A / B) so that a newly compiled assembly can be
/// swapped in without leaving stale timestamped DLLs on disk.
///
/// Strategy:
///   - The <em>inactive</em> slot is always the "compile" target.
///   - On a successful load the inactive slot becomes active.
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
    /// Attempts to load the assembly at <paramref name="assemblyPath"/> into a fresh
    /// <see cref="UserAssemblyLoadContext"/>.  On success, unloads the previous context
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
            var userAssembly = newContext.LoadFromAssemblyPath(assemblyPath);

            // Success – unload the old context first
            UnloadCurrentContext();

            loadContext = newContext;
            LoadedAssemblyPath = assemblyPath;

            // Flip the active slot and persist
            activeSlot = activeSlot == slotA ? slotB : slotA;
            PersistActiveSlot(activeSlot);

            Serializer.ResetOptions();
            UserCodeTypeManifest.RegisterFromManifest(assemblyPath, userAssembly, logger);

            logger.LogInformation("Assembly swapped to {Slot}: {Path}", activeSlot, assemblyPath);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load assembly from {Path}. Reverting to previous slot", assemblyPath);

            try { newContext.Unload(); }
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
