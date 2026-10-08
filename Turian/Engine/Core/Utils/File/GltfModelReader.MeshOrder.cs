namespace Turian.Engine.Core;

public static partial class GltfModelReader
{
    /// <summary>Returns meshes in scene node traversal order, preserving repeated mesh instances.</summary>
    public static IReadOnlyList<int> GetMeshOrder(JsonElement root)
    {
        if (!root.TryGetProperty("meshes", out var meshes))
            return [];
        if (!root.TryGetProperty("scenes", out var scenes) || scenes.GetArrayLength() == 0 ||
            !root.TryGetProperty("nodes", out var nodes))
            return Enumerable.Range(0, meshes.GetArrayLength()).ToArray();

        var order = new List<int>();
        var active = new HashSet<int>();
        var scene = scenes[Integer(root, "scene")];
        if (scene.TryGetProperty("nodes", out var roots))
            foreach (var node in roots.EnumerateArray())
                VisitNode(node.GetInt32(), nodes, meshes.GetArrayLength(), active, order);
        return order;
    }

    static void VisitNode(int index, JsonElement nodes, int meshCount, HashSet<int> active, List<int> order)
    {
        if (index < 0 || index >= nodes.GetArrayLength() || !active.Add(index))
            throw new InvalidDataException("glTF scene contains an invalid or cyclic node reference.");
        var node = nodes[index];
        AddNodeMesh(node, meshCount, order);
        if (node.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
                VisitNode(child.GetInt32(), nodes, meshCount, active, order);
        active.Remove(index);
    }

    static void AddNodeMesh(JsonElement node, int meshCount, List<int> order)
    {
        if (node.TryGetProperty("mesh", out var mesh))
        {
            var meshIndex = mesh.GetInt32();
            if (meshIndex < 0 || meshIndex >= meshCount)
                throw new InvalidDataException("glTF node references an invalid mesh.");
            order.Add(meshIndex);
        }
    }
}
