namespace Gaya.Plugin.Turian;

/// <summary>Gives an open Turian project its product name and the Turian application icon.</summary>
sealed class ProjectWindowIdentity(SettingsService settings) : IWindowIdentity
{
    static readonly byte[] Icon = ReadIcon();

    /// <inheritdoc />
    public WindowIdentity? Current => settings.Settings is { } project
        ? new WindowIdentity($"{ProjectPresentation.Name(project)} - Turian", Icon)
        : null;

    static byte[] ReadIcon()
    {
        using var stream = typeof(GayaPlugin).Assembly.GetManifestResourceStream("Gaya.Plugin.Turian.Resources.logo.png")
            ?? throw new InvalidOperationException("The Turian application icon is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
