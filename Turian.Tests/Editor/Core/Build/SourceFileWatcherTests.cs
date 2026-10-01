namespace Turian.Tests;

/// <summary>What makes the source watcher ask for a recompile.</summary>
public sealed class SourceFileWatcherTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), $"turian-watch-{Guid.NewGuid():N}");

    /// <summary>Creates the watched folder.</summary>
    public SourceFileWatcherTests() => Directory.CreateDirectory(directory);

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(directory, recursive: true);

    /// <summary>Scripts and assembly definitions trigger a recompile; other assets do not.</summary>
    [Fact]
    public async Task ScriptsAndAssemblyDefinitionsTriggerRecompiles()
    {
        Assert.False(await ChangeTriggers("Icon.png", "not a script"));
        Assert.False(await ChangeTriggers("Stats.dataasset", """{ "__TypeId": "2072d8c2-86ad-52b9-9f91-42695ff0800d" }"""));
        Assert.True(await ChangeTriggers("Player.cs", "class Player {}"));
        Assert.True(await ChangeTriggers("Game.dataasset", $$"""{ "__TypeId": "{{AssemblyDefinition.TypeIdValue}}" }"""));
    }

    /// <summary>Changes in nested source folders emit a debounced notification.</summary>
    [Fact]
    public async Task NestedCSharpEdit_TriggersSourceChanged()
    {
        var assets = Path.Combine(directory, "Assets");
        Directory.CreateDirectory(Path.Combine(assets, "Inventory"));

        using var watcher = new SourceFileWatcher(NullLogger.Instance, TimeSpan.FromMilliseconds(50));
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.SourceChanged += changed => observed.TrySetResult(changed);
        watcher.Start(assets);

        await File.WriteAllTextAsync(Path.Combine(assets, "Inventory", "InventoryRule.cs"),
            "public class InventoryRule { public int MaxItems2 { get; set; } = 10; }",
            TestContext.Current.CancellationToken);

        Assert.Equal(assets, await observed.Task.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
    }

    async Task<bool> ChangeTriggers(string fileName, string content)
    {
        using var watcher = new SourceFileWatcher(NullLogger.Instance, TimeSpan.FromMilliseconds(50));
        var changed = new TaskCompletionSource();
        watcher.SourceChanged += _ => changed.TrySetResult();
        watcher.Start(directory);

        await File.WriteAllTextAsync(Path.Combine(directory, fileName), content);

        var finished = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        return finished == changed.Task;
    }
}
