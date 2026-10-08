using System.Buffers.Binary;

namespace Turian.Editor.Core;

public static partial class AssimpModelConverter
{
    static unsafe void RepairExportedTangents(string path, AssimpScene* scene)
    {
        var data = File.ReadAllBytes(path);
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)));
        var root = JsonNode.Parse(data.AsSpan(20, jsonLength))!.AsObject();
        var tangents = TangentAccessorIndices(root);
        if (tangents.Count == 0)
            return;
        var accessors = root["accessors"]!.AsArray();
        if (!tangents.Any(index => accessors[index]!["type"]!.GetValue<string>() == "VEC3"))
            return;

        var binaryOffset = 28 + jsonLength;
        using var binary = new MemoryStream();
        binary.Write(data.AsSpan(binaryOffset));
        var sourceMeshes = TangentMeshes(scene);
        if (sourceMeshes.Count != tangents.Count)
            throw new InvalidDataException("Assimp tangent export does not match its source meshes.");
        for (var i = 0; i < tangents.Count; i++)
        {
            var accessor = accessors[tangents[i]]!.AsObject();
            var mesh = (AssimpMesh*)sourceMeshes[i];
            RepairTangentAccessor(root, accessor, binary, mesh);
        }
        root["buffers"]![0]!["byteLength"] = binary.Length;
        WriteGlb(path, root, binary);
    }

    internal static List<int> TangentAccessorIndices(JsonObject root)
    {
        var indices = new SortedSet<int>();
        if (root["meshes"] is not JsonArray meshes)
            return [];
        foreach (var mesh in meshes)
            foreach (var primitive in mesh!["primitives"]!.AsArray())
                if (primitive!["attributes"]!["TANGENT"] is { } tangent)
                    indices.Add(tangent.GetValue<int>());
        return [.. indices];
    }

    static unsafe List<nint> TangentMeshes(AssimpScene* scene)
    {
        var meshes = new List<nint>();
        for (uint i = 0; i < scene->MNumMeshes; i++)
        {
            var mesh = scene->MMeshes[i];
            if (mesh->MNumFaces > 0 && mesh->MTangents is not null)
                meshes.Add((nint)mesh);
        }
        return meshes;
    }

    static unsafe void RepairTangentAccessor(JsonObject root, JsonObject accessor, Stream binary, AssimpMesh* mesh)
    {
        if (accessor["type"]!.GetValue<string>() != "VEC3")
            return;
        if (accessor["count"]!.GetValue<uint>() != mesh->MNumVertices)
            throw new InvalidDataException("Assimp tangent export has an unexpected vertex count.");
        if (mesh->MNormals is null || mesh->MBitangents is null)
            throw new InvalidDataException("Assimp tangents require normals and bitangents for glTF handedness.");
        var views = root["bufferViews"]!.AsArray();
        accessor["bufferView"] = views.Count;
        accessor["byteOffset"] = 0;
        accessor["type"] = "VEC4";
        accessor.Remove("min");
        accessor.Remove("max");
        views.Add(new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = binary.Position,
            ["byteLength"] = (long)mesh->MNumVertices * 16,
            ["target"] = 34962,
        });
        // Some bundled Assimp exporters omit the fourth tangent component required by glTF.
        // Assimp's generated bitangent uses the same direction as the exported glTF UV basis.
        Span<byte> bytes = stackalloc byte[16];
        for (uint i = 0; i < mesh->MNumVertices; i++)
        {
            var tangent = mesh->MTangents[i];
            var normal = mesh->MNormals[i];
            var bitangent = mesh->MBitangents[i];
            var xyz = new Vector3(tangent.X, tangent.Y, tangent.Z);
            var normalVector = new Vector3(normal.X, normal.Y, normal.Z);
            var bitangentVector = new Vector3(bitangent.X, bitangent.Y, bitangent.Z);
            var sign = Vector3.Dot(Vector3.Cross(normalVector, xyz), bitangentVector) < 0 ? -1f : 1f;
            if (xyz.LengthSquared() > 0)
                xyz = Vector3.Normalize(xyz);
            var value = new Vector4(xyz, sign);
            BinaryPrimitives.WriteSingleLittleEndian(bytes, value.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], value.Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], value.Z);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[12..], value.W);
            binary.Write(bytes);
        }
    }

    static void WriteGlb(string path, JsonObject root, MemoryStream binary)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(root);
        var paddedLength = (json.Length + 3) & ~3;
        var header = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x46546c67);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), checked((uint)(28 + paddedLength + binary.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)paddedLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 0x4e4f534a);
        using var output = File.Create(path);
        output.Write(header);
        output.Write(json);
        for (var i = json.Length; i < paddedLength; i++)
            output.WriteByte(32);
        Span<byte> binaryHeader = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(binaryHeader, checked((uint)binary.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(binaryHeader[4..], 0x004e4942);
        output.Write(binaryHeader);
        output.Write(binary.GetBuffer().AsSpan(0, (int)binary.Length));
    }
}
