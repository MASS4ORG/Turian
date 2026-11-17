namespace Turian.Editor.Core;

/// <summary>What an external editor can do when Studio hands it a project or a file.</summary>
[Flags]
public enum ExternalEditorCapabilities
{
    /// <summary>The editor cannot be driven beyond being started.</summary>
    None = 0,

    /// <summary>It opens a project folder or solution.</summary>
    OpenProject = 1,

    /// <summary>It opens a single file.</summary>
    OpenFile = 2,

    /// <summary>It places the caret on a line and column when opening a file.</summary>
    GoToLine = 4,

    /// <summary>It opens files in an editor window that is already running instead of starting another.</summary>
    ReuseInstance = 8,

    /// <summary>It can attach its debugger to a running process.</summary>
    Attach = 16,
}
