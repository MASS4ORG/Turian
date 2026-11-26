namespace Turian.Editor.Core;

/// <summary>Opens source documents in a contributed editor panel.</summary>
public interface IAssetDocumentEditor
{
    /// <summary>Whether this editor accepts the source file.</summary>
    /// <param name="sourcePath">The absolute source file path.</param>
    /// <returns>True if the editor accepts the file.</returns>
    bool CanOpen(string sourcePath);

    /// <summary>Opens the source file in this editor.</summary>
    /// <param name="sourcePath">The absolute source file path.</param>
    /// <param name="isReadOnly">Whether the source belongs to a read-only package.</param>
    void Open(string sourcePath, bool isReadOnly = false);
}
