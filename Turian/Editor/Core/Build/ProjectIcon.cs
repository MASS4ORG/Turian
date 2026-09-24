namespace Turian.Editor.Core;

/// <summary>
/// A project's icon: finding the image <see cref="PlayerSettings.Icon"/> names, and turning it into the
/// files a build ships — <see cref="PngFileName"/> for the window and <see cref="IcoFileName"/> for the
/// Windows executable.
/// </summary>
public static class ProjectIcon
{
    /// <summary>The icon a built game loads for its window, beside the executable.</summary>
    public const string PngFileName = PlayerSettings.BuiltIconFileName;

    /// <summary>The icon the Windows executable is stamped with.</summary>
    public const string IcoFileName = "icon.ico";

    static readonly int[] icoSizes = [16, 32, 48, 256];

    static readonly string[] imageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tga", ".ico"];

    /// <summary>
    /// The source image of a project's icon, for a project that is not open: its player settings are read
    /// from its sources.
    /// </summary>
    /// <param name="projectDirectory">The project folder.</param>
    /// <returns>Absolute path of the image, or null when the project names none or it is missing.</returns>
    public static string? FindSource(string projectDirectory)
    {
        if (SettingsService.Load(projectDirectory) is not { } project) return null;

        ProjectSettingsLoader.LoadFromSources(project);
        return FindSource(project);
    }

    /// <summary>The source image of an open project's icon.</summary>
    /// <param name="project">The project.</param>
    /// <returns>Absolute path of the image, or null when the project names none or it is missing.</returns>
    public static string? FindSource(IAppSettings project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.Get<PlayerSettings>().Icon is not { IsEmpty: false } icon) return null;
        if (!Directory.Exists(project.AssetsAbsoluteDir)) return null;

        return Directory.EnumerateFiles(project.AssetsAbsoluteDir, "*.meta", SearchOption.AllDirectories)
            .Select(static meta => meta[..^".meta".Length])
            .Where(static source =>
                imageExtensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault(source => File.Exists(source) && MetaId(source) == icon.AssetId);
    }

    /// <summary>
    /// Writes <see cref="PngFileName"/> and <see cref="IcoFileName"/> for a project into a folder, or removes
    /// them when the project has no icon, so a build never ships a stale one.
    /// </summary>
    /// <param name="project">The project being built.</param>
    /// <param name="outputDirectory">Where the icon files go.</param>
    /// <returns>True when icon files were written.</returns>
    public static bool Write(IAppSettings project, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);
        var pngPath = Path.Combine(outputDirectory, PngFileName);
        var icoPath = Path.Combine(outputDirectory, IcoFileName);

        using var image = FindSource(project) is { } source ? SKBitmap.Decode(source) : null;
        if (image is null)
        {
            File.Delete(pngPath);
            File.Delete(icoPath);
            return false;
        }

        File.WriteAllBytes(pngPath, EncodePng(image, Math.Min(256, Math.Max(image.Width, image.Height))));
        File.WriteAllBytes(icoPath, EncodeIco(image));
        return true;
    }

    /// <summary>
    /// An <c>.ico</c> holding PNG-compressed square images at the usual sizes, which every Windows since
    /// Vista reads.
    /// </summary>
    /// <param name="image">The source image.</param>
    /// <returns>The file's bytes.</returns>
    public static byte[] EncodeIco(SKBitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var entries = icoSizes.Select(size => (Size: size, Png: EncodePng(image, size))).ToList();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)entries.Count);

        var offset = 6 + (16 * entries.Count);
        foreach (var (size, png) in entries)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in entries) writer.Write(png);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>The image fitted, centred and letterboxed, into a transparent square, as PNG.</summary>
    static byte[] EncodePng(SKBitmap image, int size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var scale = (float)size / Math.Max(image.Width, image.Height);
        var width = image.Width * scale;
        var height = image.Height * scale;

        surface.Canvas.Clear(SKColors.Transparent);
        using (var source = SKImage.FromBitmap(image))
            surface.Canvas.DrawImage(source, SKRect.Create((size - width) / 2, (size - height) / 2, width, height),
                new SKSamplingOptions(SKCubicResampler.Mitchell));

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static Guid MetaId(string sourcePath)
    {
        try
        {
            return Asset.Load($"{sourcePath}.meta")?.Id ?? Guid.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return Guid.Empty;
        }
    }
}
