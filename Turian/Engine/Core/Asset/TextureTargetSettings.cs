namespace Turian.Engine.Core;

/// <summary>
/// Import settings that differ per build target. A phone wants a tighter resolution cap than a
/// desktop even when both sample the same source file. Compression format and quality belong here
/// too once a second encoder exists.
/// </summary>
public class TextureTargetSettings
{
    /// <summary>
    /// Longest edge this target uploads, or <c>0</c> to inherit
    /// <see cref="TextureImportSettings.MaxResolution"/>.
    /// </summary>
    public int MaxResolution { get; set; }
}
