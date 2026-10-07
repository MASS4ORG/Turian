namespace Gaya.Plugin.Turian;

/// <summary>How an Inspector panel presents a freshly selected node's components.</summary>
[Gaya.EditorSetting("Inspector", Id = "gaya.turian.inspector")]
public sealed class InspectorSettings
{
    /// <summary>Whether a node's component sections start open when it is newly selected.</summary>
    [Gaya.EditorSetting("Auto-Expand Components", Description = "Start a newly selected node's components open.")]
    public bool AutoExpandComponents { get; set; } = true;
}
