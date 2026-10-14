namespace Turian.Tests;

/// <summary>
/// Runs Khronos <c>ktx validate</c> over what <see cref="TextureAssetImporter"/> actually cooks, so the
/// importer's output, not just the KTX2 library's own fixtures, is checked against the specification.
/// The validator is located through the <c>KTX_VALIDATE</c> environment variable; without it the tests skip.
/// </summary>
public sealed class TextureCookValidationTests : IDisposable
{
    const string validatorVariable = "KTX_VALIDATE";

    readonly TextureAssetImporter importer = new();
    readonly string workingDirectory = Directory.CreateTempSubdirectory("turian-texture-cook-").FullName;
    readonly string? validator = Environment.GetEnvironmentVariable(validatorVariable);

    /// <summary>Removes the cooked artifacts.</summary>
    public void Dispose() => Directory.Delete(workingDirectory, recursive: true);

    /// <summary>Block-compressed DDS data passes through into a valid container for every BCn family.</summary>
    [Theory]
    [InlineData("Antenna_Metal_BaseColor.dds", "DXT1", 8u)]
    [InlineData("Antenna_Metal_Specular.dds", "DXT1", 8u)]
    [InlineData("Antenna_Metal_Emissive.dds", "DXT5", 16u)]
    [InlineData("Antenna_Metal_Normal.dds", "ATI2", 16u)]
    [InlineData("Antenna_Metal_Mask.dds", "ATI1", 8u)]
    public void DdsPassthrough_IsValidKtx2(string fileName, string fourCc, uint blockBytes)
    {
        SkipWithoutValidator();

        var artifacts = Cook(fileName, DdsFixture.Build(fourCc, 16, 16, 5, blockBytes));

        AssertValid(artifacts);
    }

    /// <summary>Decoded sources become valid RGBA8 sRGB or UNORM containers.</summary>
    [Theory]
    [InlineData("Albedo.png")]
    [InlineData("Surface_Normal.png")]
    public void DecodedPng_IsValidKtx2(string fileName)
    {
        SkipWithoutValidator();

        var artifacts = Cook(fileName, EncodePng(8, 8));

        AssertValid(artifacts);
    }

    /// <summary>A mip-capped bake rewrites the base extent and still produces a valid container.</summary>
    [Fact]
    public void MaxResolutionCap_IsValidKtx2()
    {
        SkipWithoutValidator();
        var asset = CreateAsset("Antenna_Metal_BaseColor.dds");
        asset.ImportSettings.MaxResolution = 4;

        var artifacts = Cook(asset, "Antenna_Metal_BaseColor.dds", DdsFixture.Build("DXT1", 16, 16, 5, 8));

        AssertValid(artifacts);
    }

    void SkipWithoutValidator() =>
        Assert.SkipWhen(string.IsNullOrWhiteSpace(validator),
            $"{validatorVariable} is unset; point it at a KTX-Software 'ktx' binary to validate cooked textures");

    TextureAsset CreateAsset(string fileName) =>
        Assert.IsType<TextureAsset>(importer.CreateAsset(Path.Combine(workingDirectory, fileName)));

    IReadOnlyList<string> Cook(string fileName, byte[] source) => Cook(CreateAsset(fileName), fileName, source);

    IReadOnlyList<string> Cook(TextureAsset asset, string fileName, byte[] source)
    {
        var sourcePath = Path.Combine(workingDirectory, fileName);
        File.WriteAllBytes(sourcePath, source);
        return importer.ImportToCache(asset, sourcePath, workingDirectory);
    }

    void AssertValid(IReadOnlyList<string> artifacts)
    {
        Assert.NotEmpty(artifacts);
        foreach (var artifact in artifacts)
        {
            var (exitCode, output) = Validate(Path.Combine(workingDirectory, artifact));
            Assert.True(exitCode == 0, $"ktx validate rejected {artifact}:{Environment.NewLine}{output}");
        }
    }

    (int ExitCode, string Output) Validate(string path)
    {
        var start = new ProcessStartInfo(validator!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("validate");
        start.ArgumentList.Add("--warnings-as-errors");
        start.ArgumentList.Add(path);

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException($"Could not start '{validator}'");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr.Result);
    }

    static byte[] EncodePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Magenta);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
