using System.Xml.Linq;
using SkiaSharp;
using Turian.Engine.Core;

namespace Turian.Editor.Core;

/// <summary>Turns the project's icon into Android launcher resources, including an adaptive icon.</summary>
public static class AndroidIcons
{
    /// <summary>The generated resources, relative to the export project.</summary>
    public const string ResourceGlob = "Resources/**/*";

    const string IconName = "appicon";
    const string ForegroundName = "appicon_foreground";
    const string ResourceDirectory = "Resources";
    static readonly XNamespace AndroidNamespace = "http://schemas.android.com/apk/res/android";

    /// <summary>The density folders and their legacy icon and adaptive foreground sizes in pixels.</summary>
    internal static readonly (string Density, int Icon, int Foreground)[] Densities =
    [
        ("mdpi", 48, 108), ("hdpi", 72, 162), ("xhdpi", 96, 216), ("xxhdpi", 144, 324), ("xxxhdpi", 192, 432),
    ];

    /// <summary>Writes the launcher icons and points the manifest at them, or removes stale ones when the project has none.</summary>
    /// <param name="settings">The project being exported.</param>
    /// <param name="exportDirectory">The generated export project's folder.</param>
    /// <returns>True when icons were written.</returns>
    public static bool Write(IAppSettings settings, string exportDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var resources = Path.Combine(exportDirectory, ResourceDirectory);
        if (Directory.Exists(resources))
        {
            Directory.Delete(resources, recursive: true);
        }

        if (ProjectIcon.FindSource(settings) is not { } source)
        {
            return false;
        }

        using var image = SKBitmap.Decode(File.ReadAllBytes(source))
            ?? throw new InvalidDataException($"The project icon '{source}' is not a readable image.");
        foreach (var (density, icon, foreground) in Densities)
        {
            var directory = Directory.CreateDirectory(Path.Combine(resources, $"mipmap-{density}")).FullName;
            File.WriteAllBytes(Path.Combine(directory, $"{IconName}.png"), ProjectIcon.EncodePng(image, icon));
            File.WriteAllBytes(Path.Combine(directory, $"{ForegroundName}.png"), EncodeForeground(image, foreground));
        }

        WriteAdaptiveIcon(resources);
        ReferenceFromManifest(Path.Combine(exportDirectory, "AndroidManifest.xml"));
        return true;
    }

    /// <summary>Centers the image in the middle two thirds of the canvas, the part no launcher mask can cut off.</summary>
    [Obsolete]
    static byte[] EncodeForeground(SKBitmap image, int size)
    {
        var inner = size * 2 / 3;
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using (var fitted = SKBitmap.Decode(ProjectIcon.EncodePng(image, inner)))
        {
            surface.Canvas.DrawBitmap(fitted, (size - inner) / 2f, (size - inner) / 2f);
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static void WriteAdaptiveIcon(string resources)
    {
        var directory = Directory.CreateDirectory(Path.Combine(resources, "mipmap-anydpi-v26")).FullName;
        new XDocument(new XElement("adaptive-icon",
            new XAttribute(XNamespace.Xmlns + "android", AndroidNamespace),
            new XElement("background", new XAttribute(AndroidNamespace + "drawable", "@android:color/white")),
            new XElement("foreground", new XAttribute(AndroidNamespace + "drawable", $"@mipmap/{ForegroundName}"))))
            .Save(Path.Combine(directory, $"{IconName}.xml"));
    }

    static void ReferenceFromManifest(string manifestPath)
    {
        var manifest = XDocument.Load(manifestPath);
        var application = manifest.Root!.Element("application")
            ?? throw new InvalidDataException("The Android manifest has no application element.");
        application.SetAttributeValue(AndroidNamespace + "icon", $"@mipmap/{IconName}");
        application.SetAttributeValue(AndroidNamespace + "roundIcon", $"@mipmap/{IconName}");
        manifest.Save(manifestPath);
    }
}
