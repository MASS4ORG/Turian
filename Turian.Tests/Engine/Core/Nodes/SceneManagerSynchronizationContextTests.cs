namespace Turian.Tests;

/// <summary>
/// Covers loading a scene from a thread that owns a single-threaded synchronization context and
/// blocks on the returned task, the way the Studio's scene tree does on the UI thread.
/// </summary>
public class SceneManagerSynchronizationContextTests
{
    /// <summary>
    /// A synchronization context that queues continuations for a thread that is blocked, so any
    /// awaited continuation captured by the context never runs.
    /// </summary>
    sealed class BlockedSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => Posted++;

        public override void Send(SendOrPostCallback callback, object? state) => Posted++;

        public int Posted { get; private set; }
    }

    /// <summary>Loading a scene completes even when the calling thread's single-threaded context never runs continuations.</summary>
    [Fact]
    public void LoadNodeAsync_BlockedOnSingleThreadedContext_Completes()
    {
        TestAssetDatabase.Reset();

        var scenePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.prefab");
        File.WriteAllText(scenePath, Serializer.Serialize(new Node { Name = "SceneRoot" }));

        try
        {
            Node? root = null;
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new BlockedSynchronizationContext());
                try
                {
                    var sceneManager = new SceneManager(new AssetDatabase());
                    root = sceneManager.LoadNodeAsync(scenePath).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            thread.IsBackground = true;
            thread.Start();

            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Loading the scene deadlocked.");
            Assert.Null(failure);
            Assert.NotNull(root);
            Assert.Equal("SceneRoot", root!.Name);
        }
        finally
        {
            File.Delete(scenePath);
            TestAssetDatabase.Reset();
        }
    }
}
