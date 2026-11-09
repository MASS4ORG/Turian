using Microsoft.Build.Construction;

namespace Turian.Tests;

/// <summary>Checks feature-neutral staging of native brick payloads.</summary>
public sealed class BrickNativeAssetsTests
{
    static ResolvedPackage Brick(string root, string id)
    {
        var manifest = new PackageManifest { Name = id, Version = SemanticVersion.Parse("1.0.0") };
        return new(id, manifest, root, PackageOrigin.File, "file:" + root, null, null, 1, false);
    }

    /// <summary>Only the requested RID is staged; identical shared payloads are deduplicated.</summary>
    [Fact]
    public void StagesSelectedRuntimeAndDeduplicatesPayloads()
    {
        var root = Directory.CreateTempSubdirectory("turian-native-assets-");
        try
        {
            var a = Directory.CreateDirectory(Path.Combine(root.FullName, "a", "runtimes", "test-rid", "native"));
            var b = Directory.CreateDirectory(Path.Combine(root.FullName, "b", "runtimes", "test-rid", "native"));
            File.WriteAllBytes(Path.Combine(a.FullName, "native.bin"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(b.FullName, "native.bin"), [1, 2, 3]);
            var project = ProjectRootElement.Create();
            ResolvedPackage[] bricks = [Brick(Path.Combine(root.FullName, "a"), "a.native"),
                Brick(Path.Combine(root.FullName, "b"), "b.native")];
            BrickNativeAssets.AddToProject(project, bricks, "test-rid");
            var item = Assert.Single(project.Items);
            Assert.Contains(item.Metadata, static metadata => metadata.Name == "Link" && metadata.Value == "native.bin");
            var editor = ProjectRootElement.Create();
            BrickNativeAssets.AddToProject(editor, bricks, "test-rid", publish: false);
            var editorItem = Assert.Single(editor.Items);
            Assert.Contains(editorItem.Metadata, static metadata =>
                metadata.Name == "CopyToOutputDirectory" && metadata.Value == "PreserveNewest");
            Assert.Contains(editorItem.Metadata, static metadata =>
                metadata.Name == "CopyToPublishDirectory" && metadata.Value == "Never");
            var other = ProjectRootElement.Create();
            BrickNativeAssets.AddToProject(other, bricks, "missing-rid");
            Assert.Empty(other.Items);
            File.WriteAllBytes(Path.Combine(b.FullName, "native.bin"), [4, 5, 6]);
            Assert.Throws<InvalidOperationException>(() => BrickNativeAssets.AddToProject(ProjectRootElement.Create(), bricks, "test-rid"));
        }
        finally { root.Delete(true); }
    }

    /// <summary>Android payloads become native libraries of the matching ABI, so they land in the APK's lib folder.</summary>
    [Theory]
    [InlineData("android-arm64", "arm64-v8a")]
    [InlineData("android-x64", "x86_64")]
    public void StagesAndroidPayloadsAsNativeLibraries(string rid, string abi)
    {
        var root = Directory.CreateTempSubdirectory("turian-native-assets-");
        try
        {
            var native = Directory.CreateDirectory(Path.Combine(root.FullName, "a", "runtimes", rid, "native"));
            File.WriteAllBytes(Path.Combine(native.FullName, "libsound.so"), [1]);
            File.WriteAllText(Path.Combine(native.FullName, "libsound.so.meta"), "{}");
            var project = ProjectRootElement.Create();

            BrickNativeAssets.AddToProject(project, [Brick(Path.Combine(root.FullName, "a"), "a.native")], rid);

            var item = Assert.Single(project.Items);
            Assert.Equal("AndroidNativeLibrary", item.ItemType);
            Assert.Equal(abi, Assert.Single(item.Metadata, static metadata => metadata.Name == "Abi").Value);
        }
        finally { root.Delete(true); }
    }
}
