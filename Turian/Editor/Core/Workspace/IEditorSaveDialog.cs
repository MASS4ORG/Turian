namespace Turian.Editor.Core;

/// <summary>Lets editor tools ask for an output file through Studio's shared dialog.</summary>
public interface IEditorSaveDialog
{
    /// <summary>Prompts for a new output file and completes with an absolute path or null on cancel.</summary>
    void SaveNewFile(string title, string? directory, string suggestedName, Action<string?> completed);
}
