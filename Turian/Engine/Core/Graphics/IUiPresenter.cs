namespace Turian.Engine.Core;

/// <summary>
/// Draws a game's 2D interface for one viewport. An interface package implements it; hosts (the player, the editor
/// viewports, the headless tools) only ever talk to this contract, so a game ships without any interface stack
/// unless it installs one.
/// </summary>
public interface IUiPresenter : IDisposable
{
    /// <summary>Resolves localized text; <c>null</c> shows authored text. Hosts update it as the session changes.</summary>
    LocaleService? Locale { get; set; }

    /// <summary>
    /// Renders the screen-space interface found under <paramref name="root"/>. A fault in a document or controller is
    /// logged and yields <c>null</c> rather than blanking the frame.
    /// </summary>
    /// <param name="root">The scene root being rendered this frame.</param>
    /// <param name="width">Target width in physical pixels, greater than zero.</param>
    /// <param name="height">Target height in physical pixels, greater than zero.</param>
    /// <param name="deltaTime">Seconds since the previous frame.</param>
    /// <returns>The texture to composite over the frame, or <c>null</c> when there is nothing to draw.</returns>
    Texture? TryRenderOverlay(Node root, int width, int height, float deltaTime);

    /// <summary>Renders the interface panels placed in the world under <paramref name="root"/>.</summary>
    /// <param name="root">The scene root being rendered this frame.</param>
    /// <param name="frame">The render target the panels are rasterised against.</param>
    /// <returns>One quad per visible panel.</returns>
    IReadOnlyList<WorldUiQuad> RenderWorldPanels(Node root, WorldUiFrame frame);
}

/// <summary>Creates the <see cref="IUiPresenter"/> of an interface package.</summary>
public interface IUiPresenterFactory
{
    /// <summary>Creates a presenter on the shared Vulkan context.</summary>
    /// <param name="vulkan">The context interface textures are created on.</param>
    /// <param name="input">Input for interface controls, or <c>null</c> for neutral input.</param>
    /// <param name="locale">Resolves localized text, or <c>null</c> to show authored text.</param>
    /// <returns>The presenter, owned by the caller.</returns>
    IUiPresenter Create(Vulkan vulkan, IInputSource? input, LocaleService? locale);
}

/// <summary>An interface package's way to draw one document on the CPU, without a scene or a GPU: what previews and CI use.</summary>
public interface IUiDocumentPreview
{
    /// <summary>Renders an interface document to a PNG.</summary>
    /// <param name="documentPath">The document file.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="dataJson">A JSON object bound as the document's data, or <c>null</c>.</param>
    /// <returns>The PNG bytes.</returns>
    byte[] RenderPng(string documentPath, int width, int height, string? dataJson);
}

/// <summary>Marks an assembly as an interface package, naming its <see cref="IUiPresenterFactory"/>.</summary>
/// <param name="factoryType">A type with a public parameterless constructor implementing the factory.</param>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class UiPresenterFactoryAttribute(Type factoryType) : Attribute
{
    /// <summary>The factory type.</summary>
    public Type FactoryType { get; } = factoryType;
}

/// <summary>Finds the interface package a running host has loaded.</summary>
public static class UiPresenters
{
    static readonly Lock CacheLock = new();
    static int _scannedAssemblies = -1;
    static IUiPresenterFactory? _found;

    /// <summary>
    /// The factory named by the first loaded assembly that declares one, or <c>null</c> when no interface package is
    /// installed. Cheap to call every frame: the app domain is scanned again only when an assembly was loaded since.
    /// </summary>
    /// <returns>The factory, or <c>null</c>.</returns>
    public static IUiPresenterFactory? Find()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        lock (CacheLock)
        {
            if (assemblies.Length == _scannedAssemblies) return _found;

            _scannedAssemblies = assemblies.Length;
            _found = Find(assemblies);
            return _found;
        }
    }

    /// <summary>The document preview of the installed interface package, or <c>null</c> when it offers none.</summary>
    /// <returns>The preview.</returns>
    public static IUiDocumentPreview? FindPreview() => Find() as IUiDocumentPreview;

    /// <summary>The factory named by the first of <paramref name="assemblies"/> that declares one.</summary>
    /// <param name="assemblies">The assemblies to look in.</param>
    /// <returns>The factory, or <c>null</c>.</returns>
    public static IUiPresenterFactory? Find(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var assembly in assemblies)
        {
            if (assembly.IsDynamic || assembly.GetCustomAttribute<UiPresenterFactoryAttribute>() is not { } attribute)
                continue;
            return (IUiPresenterFactory)Activator.CreateInstance(attribute.FactoryType)!;
        }

        return null;
    }
}
