namespace Turian.Tests;

/// <summary>Reading Unity's YAML and importing a Unity package.</summary>
public sealed class UnityImportTests : IDisposable
{
    const string textureGuid = "0123456789abcdef0123456789abcdef";
    const string modelGuid = "11111111111111111111111111111111";
    const string materialGuid = "22222222222222222222222222222222";
    const string prefabGuid = "33333333333333333333333333333333";
    const string scriptGuid = "44444444444444444444444444444444";

    readonly string root = Path.Combine(Path.GetTempPath(), $"turian-unity-import-{Guid.NewGuid():N}");

    /// <summary>Creates the scratch folder.</summary>
    public UnityImportTests() => Directory.CreateDirectory(root);

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>Block maps, sequences at the key's indent, flow maps and lists, quotes and headers are read.</summary>
    [Fact]
    public void YamlIsReadAsUnityWritesIt()
    {
        var objects = UnityYaml.ParseObjects("""
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!1 &1000
            GameObject:
              m_Component:
              - component: {fileID: 2000}
              - component: {fileID: 3000}
              m_Name: 'Crate: big'
              m_IsActive: 1
            --- !u!21 &2100000
            Material:
              m_SavedProperties:
                m_TexEnvs:
                - _MainTex:
                    m_Texture: {fileID: 2800000, guid: abc, type: 3}
                    m_Scale: {x: 1, y: 1}
                m_Floats:
                - _Metallic: 0.25
                m_Colors:
                - _Color: {r: 1, g: 0.5, b: 0, a: 1}
              m_Empty: []
            """);

        Assert.Equal([(1, 1000L, "GameObject"), (21, 2100000L, "Material")], objects.Select(o => (o.ClassId, o.FileId, o.TypeName)));
        var crate = objects[0].Body;
        Assert.Equal("Crate: big", crate["m_Name"]!.Text);
        Assert.Equal([2000, 3000], crate["m_Component"]!.Items.Select(i => (int)i["component"]!["fileID"]!.Long()));
        var material = objects[1].Body["m_SavedProperties"]!;
        Assert.Equal("abc", material["m_TexEnvs"]!.Items[0]["_MainTex"]!["m_Texture"]!["guid"]!.Text);
        Assert.Equal(0.25f, material["m_Floats"]!.Items[0]["_Metallic"]!.Float());
        Assert.Equal(0.5f, material["m_Colors"]!.Items[0]["_Color"]!["g"]!.Float());
        Assert.Empty(objects[1].Body["m_Empty"]!.Items);
    }

    /// <summary>A package imports into a brick: files keep their ids and folders, settings map, YAML assets convert, the rest is reported.</summary>
    [Fact]
    public void PackageImportsIntoABrick()
    {
        var package = BuildPackage();
        var brick = Path.Combine(root, "user.you.crates");
        Directory.CreateDirectory(brick);
        new PackageManifest { Name = "user.you.crates", Version = SemanticVersion.Parse("0.1.0") }.Save(brick);

        var report = UnityPackageImporter.Import(package, new UnityImportTarget(Path.Combine(brick, "Runtime"), brick));

        Assert.Equal(["Assets/Art/Crate.png", "Assets/Mats/Wood.mat", "Assets/Models/crate.obj", "Assets/Prefabs/Crate.prefab"],
            report.Converted.Select(c => c.Source).Order(StringComparer.Ordinal));
        Assert.Equal(["Assets/Scripts/Spin.cs"], report.Skipped.Select(s => s.Source));
        Assert.Contains("UnityEngine", report.Skipped[0].Note, StringComparison.Ordinal);
        Assert.Empty(BrickVerifier.Verify(brick));

        var textureMeta = JsonNode.Parse(File.ReadAllText(Path.Combine(brick, "Runtime", "Art", "Crate.png.meta")))!;
        Assert.Equal(Guid.Parse(textureGuid).ToString(), (string)textureMeta["Id"]!);
        Assert.Equal("Runtime/Art/Crate.png", (string)textureMeta["RelativePath"]!);
        Assert.False((bool)textureMeta["IsSrgb"]!);
        Assert.False((bool)textureMeta["GenerateMips"]!);
        Assert.Equal(512, (int)textureMeta["ImportSettings"]!["MaxResolution"]!);
        Assert.Equal("crates", (string)textureMeta["Labels"]![0]!);
        Assert.True(File.Exists(Path.Combine(brick, "Runtime", "Models", "crate.obj")));
        Assert.Equal(Guid.Parse(modelGuid).ToString(), (string)JsonNode.Parse(File.ReadAllText(Path.Combine(brick, "Runtime", "Models", "crate.obj.meta")))!["Id"]!);

        var material = Serializer.Load<MaterialAsset>(Path.Combine(brick, "Runtime", "Mats", "Wood.material"))!;
        Assert.Equal(new Vector4(1f, 0.5f, 0f, 1f), material.BaseColorFactor);
        Assert.Equal(0.25f, material.MetallicFactor);
        Assert.Equal(0.7f, material.RoughnessFactor, 3);
        Assert.Equal(Guid.Parse(textureGuid), material.BaseColorTexture!.AssetId);
        Assert.Equal(Guid.Parse(materialGuid), material.Id);
    }

    /// <summary>A prefab becomes a node tree with transforms, a model with its material, a light and a camera; unknown components are counted.</summary>
    [Fact]
    public void PrefabsBecomeNodeTrees()
    {
        var project = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(project, "Assets"));

        var report = UnityPackageImporter.Import(BuildPackage(), new UnityImportTarget(Path.Combine(project, "Assets", "Crates"), project));

        var entry = report.Converted.Single(c => c.Source.EndsWith("Crate.prefab", StringComparison.Ordinal));
        Assert.Equal("Prefabs/Crate.prefab", entry.Target);
        Assert.Contains("1 × MonoBehaviour not converted", entry.Note, StringComparison.Ordinal);

        var crate = Serializer.Load<Node>(Path.Combine(project, "Assets", "Crates", "Prefabs", "Crate.prefab"))!;
        Assert.Equal("Crate", crate.Name);
        Assert.Equal(new Vector3(1f, 2f, 3f), crate.Transform.Position);
        Assert.Equal(new Vector3(2f, 2f, 2f), crate.Transform.Scale);
        var model = Assert.Single(crate.Components.OfType<ModelComponent>());
        Assert.Equal(Guid.Parse(modelGuid), model.Model!.AssetId);
        Assert.Equal(Guid.Parse(materialGuid), model.Materials.Single()!.AssetId);

        Assert.Equal(["Lamp", "Eye"], crate.Children.Select(c => c.Name));
        var lamp = crate.Children[0].Components.OfType<LightComponent>().Single();
        Assert.Equal(LightType.Directional, lamp.Type);
        Assert.Equal(2f, lamp.Intensity);
        Assert.False(crate.Children[0].IsActive);
        var camera = crate.Children[1].Components.OfType<CameraComponent>().Single();
        Assert.Equal(75f, camera.FieldOfViewDegrees, 3);
        Assert.Equal(0.1f, camera.NearPlane, 3);

        Assert.Equal(Guid.Parse(prefabGuid), JsonNode.Parse(File.ReadAllText(Path.Combine(project, "Assets", "Crates", "Prefabs", "Crate.prefab.meta")))!["Id"]!.GetValue<Guid>());
    }

    /// <summary>A path that climbs out of the destination is refused, and a file that is not a package is an error.</summary>
    [Fact]
    public void UnsafePathsAndBadFilesAreRefused()
    {
        var evil = BuildPackage(("55555555555555555555555555555555", "Assets/../../evil.txt", [1, 2, 3], "guid: 55555555555555555555555555555555\n"));
        var report = UnityPackageImporter.Import(evil, new UnityImportTarget(Path.Combine(root, "safe"), root));

        Assert.Contains(report.Skipped, s => s.Source.EndsWith("evil.txt", StringComparison.Ordinal) && s.Note!.Contains("outside", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(root, "evil.txt")));

        var bad = Path.Combine(root, "bad.unitypackage");
        File.WriteAllText(bad, "nope");
        Assert.Throws<InvalidDataException>(() => UnityPackageImporter.Import(bad, new UnityImportTarget(Path.Combine(root, "x"), root)));
    }

    static byte[] Yaml(string text) => Encoding.UTF8.GetBytes(text
        .Replace("@TEX@", textureGuid, StringComparison.Ordinal).Replace("@MODEL@", modelGuid, StringComparison.Ordinal)
        .Replace("@MAT@", materialGuid, StringComparison.Ordinal).Replace("@SCRIPT@", scriptGuid, StringComparison.Ordinal));

    string BuildPackage(params (string Guid, string Path, byte[] Content, string Meta)[] extra)
    {
        byte[] png;
        using (var bitmap = new SKBitmap(4, 4))
        using (var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100))
            png = encoded.ToArray();

        var entries = new List<(string Guid, string Path, byte[]? Content, string Meta)>
        {
            (textureGuid, "Assets/Art/Crate.png", png, $"fileFormatVersion: 2\nguid: {textureGuid}\nlabels:\n- crates\nTextureImporter:\n  sRGBTexture: 0\n  mipmaps:\n    enableMipMap: 0\n  maxTextureSize: 512\n"),
            (modelGuid, "Assets/Models/crate.obj", "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n"u8.ToArray(), $"fileFormatVersion: 2\nguid: {modelGuid}\n"),
            (materialGuid, "Assets/Mats/Wood.mat", Yaml("""
                %YAML 1.1
                --- !u!21 &2100000
                Material:
                  m_Name: Wood
                  m_SavedProperties:
                    m_TexEnvs:
                    - _MainTex:
                        m_Texture: {fileID: 2800000, guid: @TEX@, type: 3}
                    m_Floats:
                    - _Metallic: 0.25
                    - _Glossiness: 0.3
                    m_Colors:
                    - _Color: {r: 1, g: 0.5, b: 0, a: 1}
                """), $"fileFormatVersion: 2\nguid: {materialGuid}\n"),
            (prefabGuid, "Assets/Prefabs/Crate.prefab", Yaml("""
                %YAML 1.1
                --- !u!1 &100
                GameObject:
                  m_Component:
                  - component: {fileID: 400}
                  - component: {fileID: 330}
                  - component: {fileID: 230}
                  - component: {fileID: 1140}
                  m_Name: Crate
                  m_IsActive: 1
                --- !u!4 &400
                Transform:
                  m_GameObject: {fileID: 100}
                  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
                  m_LocalPosition: {x: 1, y: 2, z: 3}
                  m_LocalScale: {x: 2, y: 2, z: 2}
                  m_Children:
                  - {fileID: 401}
                  - {fileID: 402}
                  m_Father: {fileID: 0}
                --- !u!33 &330
                MeshFilter:
                  m_GameObject: {fileID: 100}
                  m_Mesh: {fileID: 4300000, guid: @MODEL@, type: 3}
                --- !u!23 &230
                MeshRenderer:
                  m_GameObject: {fileID: 100}
                  m_Materials:
                  - {fileID: 2100000, guid: @MAT@, type: 2}
                --- !u!114 &1140
                MonoBehaviour:
                  m_GameObject: {fileID: 100}
                  m_Script: {fileID: 11500000, guid: @SCRIPT@, type: 3}
                --- !u!1 &101
                GameObject:
                  m_Component:
                  - component: {fileID: 401}
                  - component: {fileID: 1080}
                  m_Name: Lamp
                  m_IsActive: 0
                --- !u!4 &401
                Transform:
                  m_GameObject: {fileID: 101}
                  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
                  m_LocalPosition: {x: 0, y: 1, z: 0}
                  m_LocalScale: {x: 1, y: 1, z: 1}
                  m_Children: []
                  m_Father: {fileID: 400}
                --- !u!108 &1080
                Light:
                  m_GameObject: {fileID: 101}
                  m_Type: 1
                  m_Color: {r: 1, g: 1, b: 1, a: 1}
                  m_Intensity: 2
                --- !u!1 &102
                GameObject:
                  m_Component:
                  - component: {fileID: 402}
                  - component: {fileID: 200}
                  m_Name: Eye
                  m_IsActive: 1
                --- !u!4 &402
                Transform:
                  m_GameObject: {fileID: 102}
                  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
                  m_LocalPosition: {x: 0, y: 0, z: -5}
                  m_LocalScale: {x: 1, y: 1, z: 1}
                  m_Children: []
                  m_Father: {fileID: 400}
                --- !u!20 &200
                Camera:
                  m_GameObject: {fileID: 102}
                  field of view: 75
                  near clip plane: 0.1
                  far clip plane: 500
                  orthographic: 0
                  m_Depth: 1
                """), $"fileFormatVersion: 2\nguid: {prefabGuid}\n"),
            (scriptGuid, "Assets/Scripts/Spin.cs", "using UnityEngine; class Spin : MonoBehaviour {}"u8.ToArray(), $"fileFormatVersion: 2\nguid: {scriptGuid}\n"),
            ("66666666666666666666666666666666", "Assets/Art", null, "fileFormatVersion: 2\nguid: 66666666666666666666666666666666\nfolderAsset: yes\n"),
        };
        entries.AddRange(extra.Select(static e => (e.Guid, e.Path, (byte[]?)e.Content, e.Meta)));

        var folder = Path.Combine(root, $"pack-{Guid.NewGuid():N}");
        foreach (var (guid, path, content, meta) in entries)
        {
            var directory = Path.Combine(folder, guid);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "pathname"), $"{path}\n00");
            File.WriteAllText(Path.Combine(directory, "asset.meta"), meta);
            if (content is not null) File.WriteAllBytes(Path.Combine(directory, "asset"), content);
        }

        var package = Path.Combine(root, $"{Guid.NewGuid():N}.unitypackage");
        using var file = File.Create(package);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        TarFile.CreateFromDirectory(folder, gzip, includeBaseDirectory: false);
        return package;
    }
}
