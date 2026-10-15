namespace Turian.Tests;

/// <summary>The in-game UI shipped as the built-in brick <c>org.mass4.turian.ui</c>.</summary>
public sealed class UiBrickTests : IDisposable
{
    const string brickId = "org.mass4.turian.ui";

    readonly string project = Path.Combine(Path.GetTempPath(), $"turian-ui-brick-{Guid.NewGuid():N}");

    /// <summary>Creates a project that installs the given built-in bricks.</summary>
    public UiBrickTests()
    {
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(project, recursive: true);

    /// <summary>The brick ships its own assembly prebuilt and the editor's importers apart; its packages come from the map.</summary>
    [Fact]
    public void BrickShipsPrebuiltAssemblies()
    {
        var brick = Install(brickId);

        Assert.Equal(["Turian.Engine.UI"],
            BrickAssemblies.RuntimeAssemblies(brick).Select(Path.GetFileNameWithoutExtension));
        Assert.Equal(["Turian.Editor.UI"],
            BrickAssemblies.EditorAssemblies(brick).Select(Path.GetFileNameWithoutExtension));
        Assert.Contains(typeof(UiDocumentComponent).Assembly.GetReferencedAssemblies(), static r => r.Name == "Guinevere");
        Assert.Contains("SkiaSharp", brick.Manifest.Nuget.Keys);
        Assert.Contains("MASS4.Guinevere", brick.Manifest.Nuget.Keys);
    }

    /// <summary>
    /// The manifest pins the NuGet versions the prebuilt assemblies were compiled against, so a game restoring them
    /// can load what the brick references.
    /// </summary>
    [Fact]
    public void ManifestPackagesMatchPrecastReferences()
    {
        var brick = Install(brickId);

        foreach (var path in BrickAssemblies.RuntimeAssemblies(brick))
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(path));
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                var name = metadata.GetString(reference.Name);
                if (!brick.Manifest.Nuget.TryGetValue(name, out var pinned))
                {
                    continue;
                }

                var package = Version.Parse(pinned);
                Assert.True(reference.Version.Major == package.Major && reference.Version.Minor == package.Minor,
                    $"{Path.GetFileName(path)} references {name} {reference.Version}; the manifest pins {pinned}");
            }
        }
    }

    /// <summary>The generated project of a game references the brick's assemblies and NuGet packages, and only when installed.</summary>
    [Fact]
    public void GeneratedProjectsReferenceTheBrick()
    {
        var settings = new BuildAppSettings { Title = "Game", ProjectAbsoluteDir = project };

        Assert.DoesNotContain(References(settings), static name => name == "Turian.Engine.UI");

        Install(brickId);
        var references = References(settings);
        Assert.Contains("Turian.Engine.UI", references);
        var packages = References(settings, "PackageReference");
        Assert.Contains("MASS4.Guinevere", packages);
        Assert.Contains("SkiaSharp.NativeAssets.Win32", packages);
    }

    /// <summary>A game lists the prebuilt assemblies in its type manifest so it registers their types at start.</summary>
    [Fact]
    public void TypeManifestListsPrecastAssemblies()
    {
        Install(brickId);

        var manifest = UserCodeTypeManifestGenerator.Generate(Path.Combine(project, "Assets"), NullLogger.Instance);

        Assert.Equal(["Turian.Engine.UI", "Turian.Engine.Vulkan"], manifest.PrecastAssemblies.Order());
    }

    /// <summary>Loading a brick registers its types once, and the presenter factory is then found without naming the UI.</summary>
    [Fact]
    public void LoadedBrickProvidesThePresenterFactory()
    {
        var brick = Install(brickId);

        BrickAssemblies.Load([brick], NullLogger.Instance);

        Assert.NotNull(UiPresenters.Find());
        Assert.Equal(typeof(UiDocumentComponent), TypeRegistry.GetTypeOrThrow(new Guid("da4b14b7-dfd9-51a0-afd0-83f1a4c982ac")));
    }

    /// <summary>The brick renders a single document to a PNG on the CPU, which the command line's <c>ui</c> command uses.</summary>
    [Fact]
    public void BrickRendersDocumentPreviews()
    {
        var file = Path.Combine(project, "hud.ui");
        File.WriteAllText(file, "<UI xmlns=\"https://turian.mass4.org/ui\"><VisualElement><Label text=\"Score {Score}\"/></VisualElement></UI>");

        var png = new UiManagerFactory().RenderPng(file, 128, 64, "{ \"Score\": 7 }");

        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
    }

    ResolvedPackage Install(string id)
    {
        new ProjectManifest { Dependencies = { [id] = $"builtin:{id}" } }.Save(project);
        return ProjectPackages.Resolve(project).Packages.Single(p => p.Id == id);
    }

    static string[] References(IBuildAppSettings settings, string item = "Reference") =>
        [.. CsProjectGenerator.GenerateUserCode(settings, NullLogger.Instance).Items
            .Where(i => i.ItemType == item).Select(static i => i.Include)];
}
