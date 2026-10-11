namespace Turian.Engine.Core;

/// <summary>A scene-authored display that receives completed game renders while it is enabled.</summary>
public interface IRenderStatisticsTarget
{
    /// <summary>Whether the component has started and the user has enabled its display.</summary>
    bool IsRecording { get; }

    /// <summary>The display's rolling render samples.</summary>
    FrameStatistics Statistics { get; }

    /// <summary>Successful load duration for the displayed scene, when available.</summary>
    double? SceneLoadMilliseconds { get; set; }
}

/// <summary>Resolves an enabled statistics display explicitly authored in the rendered hierarchy.</summary>
public static class RenderStatisticsTargets
{
    /// <summary>Records a completed render when its authored display remains enabled.</summary>
    public static void Record(IRenderStatisticsTarget? target, RenderFrameStats stats)
    {
        if (target is { IsRecording: true }) target.Statistics.Add(stats);
    }

    /// <summary>Finds the first started display under active nodes, without allocating during traversal.</summary>
    public static IRenderStatisticsTarget? Find(Node root)
    {
        if (!root.IsActive) return null;
        var components = root.Components;
        for (var i = 0; i < components.Count; i++)
        {
            var component = components[i];
            if (component.IsActive && component is IRenderStatisticsTarget { IsRecording: true } target)
                return target;
        }
        var children = root.Children;
        for (var i = 0; i < children.Count; i++)
            if (Find(children[i]) is { } target) return target;
        return null;
    }
}
