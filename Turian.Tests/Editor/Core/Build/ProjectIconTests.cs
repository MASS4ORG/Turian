namespace Turian.Tests.Editor;

/// <summary>
/// Covers the project icon: the image the player settings name is found by its asset id, and a build gets
/// a PNG for the window and a multi-size ICO for the executable — or neither when there is no icon.
/// </summary>
public class ProjectIconTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"TurianIcon_{Guid.NewGuid():N}");
    readonly string output;

    /// <summary>Creates an empty project folder.</summary>
    public ProjectIconTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        output = Path.Combine(root, ".Cache");
    }

    /// <summary>Removes the project folder.</summary>
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    static SKBitmap Image(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.OrangeRed);
        return bitmap;
    }

    AppSettings ProjectWithIcon()
    {
        var imagePath = Path.Combine(root, "Assets", "logo.png");
        using (var image = Image(300, 150))
        using (var data = SKImage.FromBitmap(image).Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(imagePath, data.ToArray());

        var texture = new TextureAsset { Id = Guid.NewGuid(), RelativePath = "Assets/logo.png" };
        Serializer.Save<Asset>($"{imagePath}.meta", texture);

        var project = SettingsService.Load(root)!;
        ProjectSettingsFiles.Create(project, new PlayerSettings { Icon = new AssetReference<TextureAsset>(texture.Id) });
        return project;
    }

    /// <summary>The icon file is found by the id the player settings hold, even for a project that is not open.</summary>
    [Fact]
    public void TheIconIsFoundByItsAssetId()
    {
        ProjectWithIcon();

        Assert.Equal(Path.Combine(root, "Assets", "logo.png"), ProjectIcon.FindSource(root));
    }

    /// <summary>A build gets a square PNG no larger than 256 pixels and an ICO beside it.</summary>
    [Fact]
    public void ABuildGetsASquarePngAndAnIco()
    {
        Assert.True(ProjectIcon.Write(ProjectWithIcon(), output));

        using var png = SKBitmap.Decode(Path.Combine(output, ProjectIcon.PngFileName));
        Assert.Equal(256, png.Width);
        Assert.Equal(256, png.Height);
        Assert.True(File.Exists(Path.Combine(output, ProjectIcon.IcoFileName)));
    }

    /// <summary>A project without an icon leaves no icon files behind from an earlier build.</summary>
    [Fact]
    public void AProjectWithoutAnIconRemovesStaleIconFiles()
    {
        ProjectIcon.Write(ProjectWithIcon(), output);
        var project = SettingsService.Load(root)!;
        project.Loaded.Use(new PlayerSettings());

        Assert.False(ProjectIcon.Write(project, output));
        Assert.False(File.Exists(Path.Combine(output, ProjectIcon.PngFileName)));
        Assert.False(File.Exists(Path.Combine(output, ProjectIcon.IcoFileName)));
    }

    /// <summary>The ICO directory lists every size, each entry pointing at a PNG of that size.</summary>
    [Fact]
    public void TheIcoHoldsAPngPerSize()
    {
        using var image = Image(64, 64);
        var ico = ProjectIcon.EncodeIco(image);

        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));
        var count = BitConverter.ToUInt16(ico, 4);
        Assert.Equal(4, count);

        for (var entry = 0; entry < count; entry++)
        {
            var header = 6 + (16 * entry);
            var size = ico[header] == 0 ? 256 : ico[header];
            var length = BitConverter.ToInt32(ico, header + 8);
            var offset = BitConverter.ToInt32(ico, header + 12);

            using var png = SKBitmap.Decode(ico.AsSpan(offset, length).ToArray());
            Assert.Equal(size, png.Width);
        }
    }
}
