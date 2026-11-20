namespace Turian.Editor.Core;

/// <summary>Which editor window Studio asks for when it opens the project.</summary>
public enum ExternalEditorWindow
{
    /// <summary>Show the window that already has this project open, and open a new one otherwise. The default.</summary>
    NewUnlessOpen,

    /// <summary>Always open the project in a new window.</summary>
    NewWindow,

    /// <summary>Open the project in the window the editor used last.</summary>
    Reuse,
}

/// <summary>The stored form and the display text of an <see cref="ExternalEditorWindow"/>.</summary>
public static class ExternalEditorWindows
{
    /// <summary>The option key an editor's window choice is stored under.</summary>
    public const string OptionKey = "window";

    /// <summary>The value stored for <paramref name="window"/>.</summary>
    /// <param name="window">The window policy.</param>
    /// <returns>A short lowercase name, such as <c>new-unless-open</c>.</returns>
    public static string ToValue(ExternalEditorWindow window) => window switch
    {
        ExternalEditorWindow.NewWindow => "new",
        ExternalEditorWindow.Reuse => "reuse",
        _ => "new-unless-open",
    };

    /// <summary>The policy a stored value names.</summary>
    /// <param name="value">The stored value; case is ignored.</param>
    /// <returns>The policy, or <see cref="ExternalEditorWindow.NewUnlessOpen"/> for a missing or unknown value.</returns>
    public static ExternalEditorWindow Parse(string? value) => Enum.GetValues<ExternalEditorWindow>()
        .FirstOrDefault(window => string.Equals(ToValue(window), value, StringComparison.OrdinalIgnoreCase));

    /// <summary>What the preferences call <paramref name="window"/>.</summary>
    /// <param name="window">The window policy.</param>
    /// <returns>The label.</returns>
    public static string Label(ExternalEditorWindow window) => window switch
    {
        ExternalEditorWindow.NewWindow => "Always a new window",
        ExternalEditorWindow.Reuse => "Reuse the last window",
        _ => "New window unless this project is open",
    };
}
