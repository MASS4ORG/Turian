namespace Turian.Engine.UI;

/// <summary>
/// The Guinevere-based in-game GUI layer for Turian.
///
/// This assembly hosts the runtime that drives a <see cref="Gui"/> frame from the
/// engine loop, the <c>.ui</c> document model and parser, the <c>.uss</c> style
/// resolver, the data-binding engine, the screen-space and world-space render
/// backends, and the <c>UiDocumentComponent</c> / <c>UiRaycasterComponent</c>
/// scene components.
/// </summary>
public static class UiModule
{
    /// <summary>
    /// Name of the resolved Guinevere assembly. Exists so a build/smoke check can
    /// confirm the Guinevere reference (local project or NuGet package) is wired up.
    /// </summary>
    public static string GuinevereAssemblyName => typeof(Gui).Assembly.GetName().Name ?? "Guinevere";
}
