namespace Turian.Engine.Core;

/// <summary>
/// A vertex stream: a run of equally sized elements holding one or more attributes.
/// </summary>
/// <param name="VertexCount">Number of elements in the stream.</param>
/// <param name="ByteStride">Size of one element in bytes.</param>
/// <param name="Attributes">The attributes packed into each element.</param>
public sealed record MeshBlobStream(uint VertexCount, uint ByteStride, IReadOnlyList<MeshBlobAttribute> Attributes);
