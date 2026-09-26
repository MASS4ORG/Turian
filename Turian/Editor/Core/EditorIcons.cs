namespace Turian.Editor.Core;

/// <summary>
/// Font Awesome 6 Free Solid glyphs the editor draws. Text rendering falls back to the icon font for these
/// private-use code points, so they can be passed anywhere a label is drawn.
/// </summary>
public static class EditorIcons
{
    /// <summary>Play.</summary>
    public const string Play = "\uf04b";
    /// <summary>Pause.</summary>
    public const string Pause = "\uf04c";
    /// <summary>Stop.</summary>
    public const string Stop = "\uf04d";
    /// <summary>Rewind, for playing from the start.</summary>
    public const string Backward = "\uf04a";
    /// <summary>Advance one step.</summary>
    public const string ForwardStep = "\uf051";
    /// <summary>Closed fold arrow.</summary>
    public const string CaretRight = "\uf0da";
    /// <summary>Open fold arrow.</summary>
    public const string CaretDown = "\uf0d7";
    /// <summary>Close or remove.</summary>
    public const string Xmark = "\uf00d";
    /// <summary>Reset to default.</summary>
    public const string RotateLeft = "\uf2ea";
    /// <summary>Open elsewhere.</summary>
    public const string ArrowUpRightFromSquare = "\uf08e";
    /// <summary>Locked.</summary>
    public const string Lock = "\uf023";
    /// <summary>Unlocked.</summary>
    public const string LockOpen = "\uf3c1";
    /// <summary>Folder.</summary>
    public const string Folder = "\uf07b";
    /// <summary>Generic file.</summary>
    public const string File = "\uf15b";
    /// <summary>Scene.</summary>
    public const string Clapperboard = "\ue131";
    /// <summary>Material.</summary>
    public const string Palette = "\uf53f";
    /// <summary>Data asset.</summary>
    public const string Database = "\uf1c0";
    /// <summary>Image.</summary>
    public const string Image = "\uf03e";
    /// <summary>3D model or prefab.</summary>
    public const string Cube = "\uf1b2";
    /// <summary>Source code.</summary>
    public const string FileCode = "\uf1c9";
    /// <summary>Text file.</summary>
    public const string FileLines = "\uf15c";
    /// <summary>UI document.</summary>
    public const string WindowMaximize = "\uf2d0";
    /// <summary>Style sheet.</summary>
    public const string Brush = "\uf55d";
}
