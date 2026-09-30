namespace Turian.Tests;

/// <summary>Verifies that edits to project source files reach the hot-reload watcher.</summary>
public sealed class SourceFileWatcherTests
{
    /// <summary>Changes in nested source folders emit a debounced notification.</summary>
    [Fact]
    public async Task NestedCSharpEdit_TriggersSourceChanged()
    {
        var root = Path.Combine(Path.GetTempPath(), $"turian-source-watch-{Guid.NewGuid():N}");
        var assets = Path.Combine(root, "Assets");
        Directory.CreateDirectory(Path.Combine(assets, "Inventory"));
        try
        {
            using var watcher = new SourceFileWatcher(NullLogger.Instance, TimeSpan.FromMilliseconds(50));
            var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.SourceChanged += directory => observed.TrySetResult(directory);
            watcher.Start(assets);

            await File.WriteAllTextAsync(Path.Combine(assets, "Inventory", "InventoryRule.cs"),
                "public class InventoryRule { public int MaxItems2 { get; set; } = 10; }",
                TestContext.Current.CancellationToken);

            Assert.Equal(assets, await observed.Task.WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
