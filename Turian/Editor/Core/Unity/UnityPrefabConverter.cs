namespace Turian.Editor.Core;

/// <summary>
/// Converts a Unity <c>.prefab</c> or <c>.unity</c> scene into a Turian prefab: the GameObject hierarchy with local
/// transforms, active flags, lights, cameras and mesh renderers. Scripts, colliders, UI and nested prefab instances are
/// counted in the report and left out.
/// </summary>
static class UnityPrefabConverter
{
    static readonly Guid UnityBuiltinResources = Guid.Parse("0000000000000000e000000000000000");

    public static void Convert(UnityPackageAsset asset, string relative, UnityImportContext context)
    {
        var objects = UnityYaml.ParseObjects(File.ReadAllText(asset.AssetFile!));
        var byFile = objects.ToDictionary(static o => o.FileId);
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);

        var transformOf = objects.Where(static o => o.ClassId is 4 or 224)
            .ToDictionary(static o => Reference(o.Body["m_GameObject"]), static o => o);
        var nodes = new Dictionary<long, Node>();
        foreach (var gameObject in objects.Where(static o => o.ClassId == 1))
            nodes[gameObject.FileId] = BuildNode(asset.Guid, gameObject, byFile, transformOf, skipped, context);

        var roots = new List<Node>();
        foreach (var (gameObjectId, node) in nodes.OrderBy(static n => n.Key))
        {
            if (!transformOf.TryGetValue(gameObjectId, out var transform)) { roots.Add(node); continue; }

            var father = Reference(transform.Body["m_Father"]);
            if (father != 0 && byFile.TryGetValue(father, out var parentTransform) && nodes.TryGetValue(Reference(parentTransform.Body["m_GameObject"]), out var parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }

        var nested = objects.Count(static o => o.ClassId == 1001);
        if (nested > 0) skipped["nested prefab instance"] = nested;

        var isScene = Path.GetExtension(asset.Pathname).Equals(".unity", StringComparison.OrdinalIgnoreCase);
        var root = roots.Count == 1 && !isScene ? roots[0] : new Node { Name = Path.GetFileNameWithoutExtension(asset.Pathname), Id = AssetIdFactory.Derive(asset.Guid, "root") };
        if (root != roots.FirstOrDefault() || roots.Count != 1) foreach (var node in roots) root.Children.Add(node);

        var target = context.Place(Path.ChangeExtension(relative, ".prefab"));
        Serializer.Save(target, root);
        context.WriteMeta(target, new Prefab { Id = asset.Guid, RelativePath = context.MetaPath(target) });

        var notes = skipped.Select(static s => $"{s.Value} × {s.Key} not converted").ToList();
        context.Report.Converted.Add(new UnityImportEntry(asset.Pathname, context.Relative(target), notes.Count == 0 ? null : string.Join("; ", notes)));
    }

    static Node BuildNode(Guid prefab, UnityObject gameObject, Dictionary<long, UnityObject> byFile,
        Dictionary<long, UnityObject> transformOf, SortedDictionary<string, int> skipped, UnityImportContext context)
    {
        var node = new Node
        {
            Name = gameObject.Body["m_Name"]?.Text is { Length: > 0 } name ? name : "GameObject",
            IsActive = gameObject.Body["m_IsActive"]?.Long(1) != 0,
            Id = AssetIdFactory.Derive(prefab, gameObject.FileId.ToString(CultureInfo.InvariantCulture)),
        };

        if (transformOf.TryGetValue(gameObject.FileId, out var transform))
        {
            node.Transform = new Transform
            {
                Position = Vector(transform.Body["m_LocalPosition"], Vector3.Zero),
                Orientation = Rotation(transform.Body["m_LocalRotation"]),
                Scale = Vector(transform.Body["m_LocalScale"], Vector3.One),
            };
            if (transform.ClassId == 224) Count(skipped, "RectTransform (UI)");
        }

        var components = (gameObject.Body["m_Component"]?.Items ?? [])
            .Select(static item => Reference(item["component"])).Where(byFile.ContainsKey).Select(id => byFile[id]).ToList();
        var filter = components.FirstOrDefault(static c => c.ClassId == 33);
        var renderer = components.FirstOrDefault(static c => c.ClassId == 23);

        foreach (var component in components)
        {
            switch (component.ClassId)
            {
                case 1 or 4 or 224 or 33 or 23:
                    break;
                case 108:
                    AddLight(node, component, skipped);
                    break;
                case 20:
                    AddCamera(node, component);
                    break;
                default:
                    Count(skipped, component.TypeName);
                    break;
            }
        }

        if (filter is not null && renderer is not null) AddModel(node, filter, renderer, skipped, context);
        else if (renderer is not null) Count(skipped, "MeshRenderer without a mesh");
        return node;
    }

    static void AddLight(Node node, UnityObject light, SortedDictionary<string, int> skipped)
    {
        var type = light.Body["m_Type"]?.Long(2);
        if (type is not (1 or 2)) Count(skipped, type == 0 ? "spot light (made a point light)" : "area light (made a point light)");

        var color = light.Body["m_Color"];
        node.AddComponent(new LightComponent
        {
            Type = type == 1 ? LightType.Directional : LightType.Point,
            Intensity = light.Body["m_Intensity"]?.Float(1f) ?? 1f,
            Color = color?["r"] is null ? Vector4.One : new Vector4(color["r"]!.Float(1f), color["g"]!.Float(1f), color["b"]!.Float(1f), 1f),
        });
    }

    static void AddCamera(Node node, UnityObject camera)
    {
        var component = node.AddComponent<CameraComponent>();
        component.UsePerspective = camera.Body["orthographic"]?.Long() != 1;
        component.FieldOfViewDegrees = camera.Body["field of view"]?.Float(60f) ?? 60f;
        component.NearPlane = camera.Body["near clip plane"]?.Float(0.3f) ?? 0.3f;
        component.FarPlane = camera.Body["far clip plane"]?.Float(1000f) ?? 1000f;
        component.Priority = (int)(camera.Body["m_Depth"]?.Float() ?? 0f);
    }

    static void AddModel(Node node, UnityObject filter, UnityObject renderer, SortedDictionary<string, int> skipped, UnityImportContext context)
    {
        var mesh = filter.Body["m_Mesh"];
        _ = Guid.TryParseExact(mesh?["guid"]?.Text ?? string.Empty, "N", out var meshGuid);

        if (meshGuid == UnityBuiltinResources)
        {
            // Unity's cube is the only built-in mesh with a Turian counterpart.
            if (mesh?["fileID"]?.Long() == 10202) node.AddComponent<MeshComponent>();
            else Count(skipped, "built-in mesh other than the cube");
            return;
        }

        if (meshGuid == Guid.Empty || !context.Has(meshGuid))
        {
            Count(skipped, "mesh from outside this package");
            return;
        }

        var model = node.AddComponent<ModelComponent>();
        model.Model = new AssetReference<ModelAsset>(meshGuid);
        foreach (var material in renderer.Body["m_Materials"]?.Items ?? [])
        {
            _ = Guid.TryParseExact(material["guid"]?.Text ?? string.Empty, "N", out var materialGuid);
            model.Materials.Add(materialGuid != Guid.Empty && context.Has(materialGuid) ? new AssetReference<MaterialAsset>(materialGuid) : null);
        }

        // A model file holds several meshes; the component draws the whole model.
        if (mesh?["fileID"]?.Long() is > 0 and not 4300000) Count(skipped, "sub-mesh of a model (the whole model is used)");
    }

    static long Reference(UnityYamlValue? value) => value?["fileID"]?.Long() ?? 0;

    static Vector3 Vector(UnityYamlValue? value, Vector3 fallback) =>
        value?["x"] is null ? fallback : new Vector3(value["x"]!.Float(), value["y"]!.Float(), value["z"]!.Float());

    static Quaternion Rotation(UnityYamlValue? value) =>
        value?["x"] is null ? Quaternion.Identity : new Quaternion(value["x"]!.Float(), value["y"]!.Float(), value["z"]!.Float(), value["w"]!.Float(1f));

    static void Count(SortedDictionary<string, int> skipped, string what) =>
        skipped[what] = skipped.GetValueOrDefault(what) + 1;
}
