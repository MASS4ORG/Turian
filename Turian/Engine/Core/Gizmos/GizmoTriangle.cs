namespace Turian.Engine.Core;

/// <summary>A filled triangle with transformed world vertices and a display color.</summary>
public readonly record struct GizmoTriangle(Vector3 A, Vector3 B, Vector3 C, Vector4 Color);
