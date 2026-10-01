using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>A registry the project takes bricks from, edited in the inspector and written when saved.</summary>
public sealed class RegistryView : IInspectorTitled
{
    readonly BricksController controller;
    readonly string? savedName;
    readonly string? projectFolder;

    /// <summary>Creates the view over a copy of a registry, or an empty one to fill in.</summary>
    /// <param name="controller">The panel's actions.</param>
    /// <param name="registry">The registry to edit, or null for a new one.</param>
    public RegistryView(BricksController controller, ScopedRegistry? registry = null)
    {
        this.controller = controller;
        projectFolder = controller.ProjectFolder;
        savedName = registry?.Name;
        Name = registry?.Name ?? "";
        Url = registry?.Url ?? "";
        Scopes = [.. registry?.Scopes ?? []];
        Keys = [.. registry?.Keys ?? []];
        AllowUnsigned = registry?.AllowUnsigned ?? false;
    }

    /// <summary>The name the registry has in the project, or null for one not saved yet.</summary>
    public string? SavedName => savedName;

    /// <inheritdoc />
    public string InspectorTitle => savedName is null ? "New registry" : $"Registry '{savedName}'";

    /// <summary>A name for the registry, shown in messages and used to look up its access token.</summary>
    public string Name { get; set; }

    /// <summary>The <c>https://…/v1</c> address, or the folder of a static copy.</summary>
    public string Url { get; set; }

    /// <summary>The name prefixes served from here, such as <c>com.acme</c>.</summary>
    public List<string> Scopes { get; set; }

    /// <summary>The keys whose signatures are trusted, as OpenSSH public key lines.</summary>
    public List<string> Keys { get; set; }

    /// <summary>Accepts bricks without a signature; only for a registry the studio runs itself.</summary>
    public bool AllowUnsigned { get; set; }

    /// <summary>Writes the registry to the project.</summary>
    [Button]
    public void Save()
    {
        if (controller.ProjectFolder != projectFolder) return;

        _ = controller.SaveRegistryAsync(savedName, new ScopedRegistry
        {
            Name = Name.Trim(),
            Url = Url.Trim(),
            Scopes = [.. Scopes.Where(static s => s.Trim().Length > 0).Select(static s => s.Trim())],
            Keys = [.. Keys.Where(static k => k.Trim().Length > 0).Select(static k => k.Trim())],
            AllowUnsigned = AllowUnsigned,
        });
    }

    /// <summary>Stops the project taking bricks from this registry.</summary>
    [Button]
    public void Remove()
    {
        if (controller.ProjectFolder == projectFolder) _ = controller.RemoveRegistryAsync(savedName ?? Name);
    }
}

/// <summary>The public registry every project can take bricks from: shown, never edited.</summary>
/// <param name="registry">The registry.</param>
public sealed class PublicRegistryView(ScopedRegistry registry) : IInspectorTitled
{
    /// <inheritdoc />
    public string InspectorTitle => $"Registry '{registry.Name}'";

    /// <summary>The registry's name.</summary>
    [ShowInEditor]
    public string Name => registry.Name;

    /// <summary>Where the registry is.</summary>
    [ShowInEditor]
    public string Url => registry.Url;

    /// <summary>The name prefixes served from here.</summary>
    [ShowInEditor]
    public List<string> Scopes => [.. registry.Scopes];

    /// <summary>The keys whose signatures are trusted.</summary>
    [ShowInEditor]
    public List<string> Keys => [.. registry.Keys];
}
