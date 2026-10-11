namespace Turian.Engine.Core;

public partial class SceneManager
{
    readonly ConditionalWeakTable<Node, SceneLoadTiming> loadTimings = new();

    /// <summary>Returns this hierarchy's successful read, deserialize, prefab expansion and awake duration.</summary>
    public double? GetLoadMilliseconds(Node root) => loadTimings.TryGetValue(root, out var timing)
        ? timing.Milliseconds : null;

    Node RecordLoad(Node root, long started)
    {
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        loadTimings.Add(root, new SceneLoadTiming(milliseconds));
        Log.Logger.LogInformation("Loaded scene '{SceneName}' in {Elapsed:F1} ms", root.Name, milliseconds);
        return root;
    }

    sealed record SceneLoadTiming(double Milliseconds);
}
