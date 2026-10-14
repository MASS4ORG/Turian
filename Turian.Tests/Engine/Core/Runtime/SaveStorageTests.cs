using Turian.Engine.Save;

namespace Turian.Tests;

/// <summary>Shared storage contract and local crash recovery checks.</summary>
public sealed class SaveStorageTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static readonly SaveSlot Slot = new("slot-1");

    /// <summary>Both providers enforce create-only writes, stale-update protection, backups and deletion.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Providers_EnforceRevisionContract(bool files)
    {
        var root = Path.Combine(Path.GetTempPath(), "turian-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            ISaveStorage storage = files ? new FileSaveStorage(root) : new MemorySaveStorage();
            Assert.Null(await storage.ReadAsync(Slot, cancellationToken: Token));
            Assert.Null(await ((ISaveBackupStorage)storage).ReadBackupAsync(Slot, cancellationToken: Token));
            var data = new byte[] { 1, 2, 3 };
            var first = await storage.WriteAsync(Slot, data, null, cancellationToken: Token);
            data[0] = 9;
            Assert.Equal(1, (await storage.ReadAsync(Slot, cancellationToken: Token))!.Data[0]);
            await Assert.ThrowsAsync<SaveConflictException>(() => storage.WriteAsync(Slot, data, null,
                cancellationToken: Token));
            var second = await storage.WriteAsync(Slot, data, first, cancellationToken: Token);
            Assert.NotEqual(first, second);
            var backup = (await ((ISaveBackupStorage)storage).ReadBackupAsync(Slot, cancellationToken: Token))!;
            Assert.Equal(first, backup.Revision);
            Assert.Equal(1, backup.Data[0]);
            await Assert.ThrowsAsync<SaveConflictException>(() => storage.WriteAsync(Slot, data, first,
                cancellationToken: Token));
            await Assert.ThrowsAsync<SaveConflictException>(() => storage.DeleteAsync(Slot, first,
                cancellationToken: Token));
            Assert.Equal([Slot], await storage.ListAsync(cancellationToken: Token));
            var other = new SaveSlot("other");
            var otherRevision = await storage.WriteAsync(other, data, null, cancellationToken: Token);
            Assert.Equal([other, Slot], await storage.ListAsync(cancellationToken: Token));
            await storage.DeleteAsync(other, otherRevision, cancellationToken: Token);
            await storage.DeleteAsync(Slot, second, cancellationToken: Token);
            Assert.Empty(await storage.ListAsync(cancellationToken: Token));
            Assert.Null(await ((ISaveBackupStorage)storage).ReadBackupAsync(Slot, cancellationToken: Token));
            var third = await storage.WriteAsync(Slot, data, null, cancellationToken: Token);
            Assert.NotEqual(second, third);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    /// <summary>Orphaned temporary writes and cancellation leave the committed slot readable.</summary>
    [Fact]
    public async Task Local_InterruptedWritesDoNotReplaceCommittedData()
    {
        var root = Path.Combine(Path.GetTempPath(), "turian-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new FileSaveStorage(root, 1024);
            var scoped = FileSaveStorage.ForGame(new SaveSlot("CON"), new SaveSlot("User"), root);
            await scoped.WriteAsync(new SaveSlot("AUX"), new byte[] { 1 }, null, cancellationToken: Token);
            Assert.Equal(new SaveSlot("aux"), (await scoped.ListAsync(Token))[0]);
            var first = await storage.WriteAsync(Slot, new byte[] { 7 }, null, cancellationToken: Token);
            await File.WriteAllBytesAsync(Path.Combine(root, "slot-" + Slot.Name + ".aborted.tmp"), [0],
                cancellationToken: Token);
            var reopened = new FileSaveStorage(root, 1024);
            Assert.Equal(7, (await reopened.ReadAsync(Slot, cancellationToken: Token))!.Data[0]);
            Assert.Equal([Slot], await reopened.ListAsync(cancellationToken: Token));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                storage.WriteAsync(Slot, "\t"u8.ToArray(), first, cancellation.Token));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                storage.WriteAsync(Slot, new byte[1025], first, cancellationToken: Token));
            using (var lease = new FileStream(Path.Combine(root, "slot-" + Slot.Name + ".lock"),
                       FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsAsync<SaveConflictException>(() =>
                    reopened.WriteAsync(Slot, "\t"u8.ToArray(), first, cancellationToken: Token));
            }
            Assert.Equal(first, (await storage.ReadAsync(Slot, cancellationToken: Token))!.Revision);
            Assert.Null(await storage.ReadBackupAsync(Slot, cancellationToken: Token));
            await File.WriteAllBytesAsync(Path.Combine(root, "slot-" + Slot.Name + ".save"), new byte[2],
                cancellationToken: Token);
            await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReadAsync(Slot, cancellationToken: Token));
            await File.WriteAllBytesAsync(Path.Combine(root, "slot-" + Slot.Name + ".save"), new byte[33],
                cancellationToken: Token);
            await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReadAsync(Slot, cancellationToken: Token));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Concurrent devices cannot both commit from the same observed generation.</summary>
    [Fact]
    public async Task ConcurrentWriters_OnlyOneCommits()
    {
        var storage = new MemorySaveStorage();
        var revision = await storage.WriteAsync(Slot, new byte[] { 1 }, null, cancellationToken: Token);
        async Task<bool> Write(byte value)
        {
            try
            {
                await storage.WriteAsync(Slot, new byte[] { value }, revision, cancellationToken: Token);
                return true;
            }
            catch (SaveConflictException)
            {
                return false;
            }
        }
        var results = await Task.WhenAll(Task.Run(() => Write(2), cancellationToken: Token), Task.Run(() => Write(3),
            cancellationToken: Token));
        Assert.Single(results, static result => result);
    }
}
