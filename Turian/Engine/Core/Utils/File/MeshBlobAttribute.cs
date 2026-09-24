namespace Turian.Engine.Core;

/// <summary>
/// One attribute inside a vertex-stream element.
/// </summary>
/// <param name="Semantic">What the attribute means.</param>
/// <param name="ComponentType">glTF 2.0 component type constant; <c>5126</c> is <c>FLOAT</c>.</param>
/// <param name="ComponentCount">Components per element: 2, 3 or 4.</param>
/// <param name="ByteOffset">Offset of the attribute from the start of the element.</param>
public readonly record struct MeshBlobAttribute(
    MeshAttributeSemantic Semantic,
    uint ComponentType,
    uint ComponentCount,
    uint ByteOffset);
