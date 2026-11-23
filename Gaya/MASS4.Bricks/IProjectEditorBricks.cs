namespace MASS4.Bricks;

/// <summary>The already-resolved brick selection for the currently open project.</summary>
public interface IProjectEditorBricks
{
    /// <summary>Returns a stable snapshot that changes when the project's enabled bricks change.</summary>
    IReadOnlyList<ResolvedPackage> EnabledBricks { get; }
}
