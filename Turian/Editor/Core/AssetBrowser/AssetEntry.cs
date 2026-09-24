namespace Turian.Editor.Core;

/// <summary>
/// Flat, UI-agnostic description of a file-system entry discovered during a tree scan.
/// </summary>
public sealed record AssetEntry(
    // Absolute path of the asset file or directory.
    string AbsolutePath,
    // <see langword="true"/> for directories.
    bool IsDirectory,
    // Loaded asset metadata, or <see langword="null"/> for directories.
    Asset? AssetMetadata,
    // Absolute path of the parent directory, or <see langword="null"/> for root entries.
    string? ParentPath);
