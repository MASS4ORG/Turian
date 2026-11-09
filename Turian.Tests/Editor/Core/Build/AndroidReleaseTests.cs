using Microsoft.Build.Construction;

namespace Turian.Tests;

/// <summary>Android release signing, package format and launcher icon generation.</summary>
[Collection(SerialTests.Name)]
public sealed class AndroidReleaseTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "turian-android-release-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the project folder.</summary>
    public AndroidReleaseTests() => Directory.CreateDirectory(directory);

    /// <summary>Removes the project folder and the password the tests set.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AndroidBuildPlatform.KeystorePasswordVariable, null);
        Directory.Delete(directory, recursive: true);
    }

    BuildPlatformContext Context(AndroidPlayerSettings android, bool development = false, string configuration = "Release")
    {
        var settings = new BuildAppSettings { ProjectAbsoluteDir = directory, Title = "Example" };
        settings.Loaded.Use(new PlayerSettings { ApplicationIdentifier = "org.example.app" });
        settings.Loaded.Use(android);
        return new BuildPlatformContext(settings, NullLogger.Instance, Path.Combine(directory, "app.csproj"), directory,
            configuration, new BuildProfile { Platform = "android", Development = development });
    }

    static Dictionary<string, string> Configure(BuildPlatformContext context)
    {
        var project = ProjectRootElement.Create(context.ProjectFile);
        new AndroidBuildPlatform().ConfigureProject(project, context);
        return project.Properties.ToDictionary(static p => p.Name, static p => p.Value);
    }

    /// <summary>Development builds keep the debug key, even with a release keystore configured.</summary>
    [Fact]
    public void DevelopmentBuildsUseTheDebugKey()
    {
        var properties = Configure(Context(new AndroidPlayerSettings
        {
            KeystorePath = "release.keystore",
            KeyAlias = "game",
            PackageFormat = AndroidPackageFormat.Aab,
        }, development: true));

        Assert.Equal("false", properties["AndroidKeyStore"]);
        Assert.Equal("apk", properties["AndroidPackageFormats"]);
    }

    /// <summary>A configured keystore signs the export, taking passwords from the environment instead of the project.</summary>
    [Fact]
    public void ReleaseKeystoreSignsWithEnvironmentPasswords()
    {
        var properties = Configure(Context(new AndroidPlayerSettings { KeystorePath = "keys/release.keystore", KeyAlias = "game" }));

        Assert.Equal("true", properties["AndroidKeyStore"]);
        Assert.Equal(Path.Combine(directory, "keys", "release.keystore"), properties["AndroidSigningKeyStore"]);
        Assert.Equal("game", properties["AndroidSigningKeyAlias"]);
        Assert.Equal("env:" + AndroidBuildPlatform.KeystorePasswordVariable, properties["AndroidSigningStorePass"]);
        Assert.Equal("env:" + AndroidBuildPlatform.KeystorePasswordVariable, properties["AndroidSigningKeyPass"]);
    }

    /// <summary>The bundle format produces a store bundle together with an installable APK.</summary>
    [Fact]
    public void BundleFormatAlsoProducesAnApk()
    {
        var properties = Configure(Context(new AndroidPlayerSettings
        {
            KeystorePath = "release.keystore",
            KeyAlias = "game",
            PackageFormat = AndroidPackageFormat.Aab,
        }));

        Assert.Equal("aab;apk", properties["AndroidPackageFormats"]);
    }

    /// <summary>A release export names what is missing before any time is spent building.</summary>
    [Theory]
    [InlineData(null, "game", "needs a keystore")]
    [InlineData("release.keystore", null, "needs a keystore")]
    [InlineData("debug.keystore", "game", "debug keystore")]
    [InlineData("release.keystore", "androiddebugkey", "debug keystore")]
    [InlineData("missing.keystore", "game", "does not exist")]
    public void ReleaseRefusesUnusableKeystores(string? keystore, string? alias, string problem)
    {
        File.WriteAllBytes(Path.Combine(directory, "release.keystore"), [1]);
        File.WriteAllBytes(Path.Combine(directory, "debug.keystore"), [1]);

        var message = AndroidBuildPlatform.ReleaseSigningProblem(
            Context(new AndroidPlayerSettings { KeystorePath = keystore, KeyAlias = alias }));

        Assert.Contains(problem, message);
    }

    /// <summary>A release export cannot start without its password.</summary>
    [Fact]
    public void ReleaseRequiresThePassword()
    {
        File.WriteAllBytes(Path.Combine(directory, "release.keystore"), [1]);
        var context = Context(new AndroidPlayerSettings { KeystorePath = "release.keystore", KeyAlias = "game" });

        Assert.Contains(AndroidBuildPlatform.KeystorePasswordVariable, AndroidBuildPlatform.ReleaseSigningProblem(context));

        Environment.SetEnvironmentVariable(AndroidBuildPlatform.KeystorePasswordVariable, "secret");
        Assert.Null(AndroidBuildPlatform.ReleaseSigningProblem(context));
    }

    /// <summary>Development and Debug exports need no release keystore.</summary>
    [Theory]
    [InlineData(true, "Release")]
    [InlineData(false, "Debug")]
    public void NonReleaseExportsNeedNoKeystore(bool development, string configuration) =>
        Assert.Null(AndroidBuildPlatform.ReleaseSigningProblem(
            Context(new AndroidPlayerSettings(), development, configuration)));

    AppSettings ProjectWithIcon()
    {
        Directory.CreateDirectory(Path.Combine(directory, "Assets"));
        var imagePath = Path.Combine(directory, "Assets", "logo.png");
        using (var image = new SKBitmap(300, 150))
        {
            image.Erase(SKColors.OrangeRed);
            using var data = SKImage.FromBitmap(image).Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(imagePath, data.ToArray());
        }

        var texture = new TextureAsset { Id = Guid.NewGuid(), RelativePath = "Assets/logo.png" };
        Serializer.Save<Asset>($"{imagePath}.meta", texture);
        var project = SettingsService.Load(directory)!;
        ProjectSettingsFiles.Create(project, new PlayerSettings { Icon = new AssetReference<TextureAsset>(texture.Id) });
        return project;
    }

    /// <summary>Every density gets a square launcher icon and adaptive foreground, and the manifest names them.</summary>
    [Fact]
    public void WritesEveryDensityAndReferencesTheManifest()
    {
        var export = Directory.CreateDirectory(Path.Combine(directory, "export")).FullName;
        File.WriteAllText(Path.Combine(export, "AndroidManifest.xml"),
            """<manifest xmlns:android="http://schemas.android.com/apk/res/android"><application /></manifest>""");

        Assert.True(AndroidIcons.Write(ProjectWithIcon(), export));

        foreach (var (density, icon, foreground) in AndroidIcons.Densities)
        {
            using var legacy = SKBitmap.Decode(Path.Combine(export, "Resources", $"mipmap-{density}", "appicon.png"));
            using var adaptive = SKBitmap.Decode(Path.Combine(export, "Resources", $"mipmap-{density}", "appicon_foreground.png"));
            Assert.Equal((icon, icon), (legacy.Width, legacy.Height));
            Assert.Equal((foreground, foreground), (adaptive.Width, adaptive.Height));
        }

        Assert.Contains("@mipmap/appicon_foreground", File.ReadAllText(Path.Combine(export, "Resources", "mipmap-anydpi-v26", "appicon.xml")));
        Assert.Contains("@mipmap/appicon", File.ReadAllText(Path.Combine(export, "AndroidManifest.xml")));
    }

    /// <summary>A project without an icon leaves no stale resources behind.</summary>
    [Fact]
    public void WithoutAnIconResourcesAreRemoved()
    {
        var export = Directory.CreateDirectory(Path.Combine(directory, "export")).FullName;
        Directory.CreateDirectory(Path.Combine(export, "Resources", "mipmap-hdpi"));
        var settings = new BuildAppSettings { ProjectAbsoluteDir = directory, Title = "Example" };
        settings.Loaded.Use(new PlayerSettings());

        Assert.False(AndroidIcons.Write(settings, export));
        Assert.False(Directory.Exists(Path.Combine(export, "Resources")));
    }
}
