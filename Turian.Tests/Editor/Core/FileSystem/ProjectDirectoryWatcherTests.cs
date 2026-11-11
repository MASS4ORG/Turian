namespace Turian.Tests;

/// <summary>File-system events reach subclasses without a failing one ending the process.</summary>
public sealed class ProjectDirectoryWatcherTests : IDisposable
{
    readonly string directory = Directory.CreateTempSubdirectory("turian-watcher-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(directory, recursive: true);

    sealed class RefusingWatcher() : ProjectDirectoryWatcher(NullLogger.Instance)
    {
        public SemaphoreSlim Created { get; } = new(0);

        public List<string> Handled { get; } = [];

        protected override void OnFileCreated(FileSystemEventArgs e)
        {
            lock (Handled)
            {
                Handled.Add(e.Name!);
            }

            Created.Release();
            // As Windows does for a file another program is still writing.
            if (e.Name!.StartsWith("busy", StringComparison.Ordinal))
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }
        }
    }

    /// <summary>
    /// A handler that throws, as reading a file still being written does on Windows, is reported and the watcher keeps
    /// delivering events; the exception would otherwise end the process from its thread-pool thread.
    /// </summary>
    [Fact]
    public async Task AFailingHandlerDoesNotStopTheWatcher()
    {
        using var watcher = new RefusingWatcher();
        watcher.Start(directory);

        await File.WriteAllTextAsync(Path.Combine(directory, "busy.meta"), "{}", TestContext.Current.CancellationToken);
        Assert.True(await watcher.Created.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(Path.Combine(directory, "later.meta"), "{}", TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (watcher.Handled)
            {
                if (watcher.Handled.Contains("later.meta"))
                {
                    break;
                }
            }

            await watcher.Created.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        lock (watcher.Handled)
        {
            Assert.Contains("busy.meta", watcher.Handled);
            Assert.Contains("later.meta", watcher.Handled);
        }

        Assert.True(watcher.IsWatching);
    }
}
