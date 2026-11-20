namespace Turian.Editor.Core;

/// <summary>Registers an <see cref="IExternalEditor"/> an editor brick provides, found when the brick's assembly is loaded.</summary>
/// <param name="editorType">A type with a public parameterless constructor implementing the editor.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ExternalEditorAttribute(Type editorType) : Attribute
{
    /// <summary>The editor type.</summary>
    public Type EditorType { get; } = editorType;
}
