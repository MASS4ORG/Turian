using System.Text;

namespace Turian.Engine.Save;

/// <summary>Local slots committed through same-directory replacement with a previous-generation backup.</summary>
public sealed class FileSaveStorage : ISaveStorage, ISaveBackupStorage
{
    readonly string directory;
    readonly int maximumBytes;

    /// <summary>Uses an explicit game/account directory and a bounded file size.</summary>
    public FileSaveStorage(string directory, int maximumBytes = 128 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        this.directory = Path.GetFullPath(directory);
        this.maximumBytes = maximumBytes;
        Directory.CreateDirectory(this.directory);
    }

    /// <summary>Creates game/account storage under the user-local data folder or a platform-supplied root.</summary>
    public static FileSaveStorage ForGame(SaveSlot game, SaveSlot account, string? userDataDirectory = null)
    {
        MemorySaveStorage.Validate(game, default);
        MemorySaveStorage.Validate(account, default);
        return new FileSaveStorage(Path.Combine(
            userDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "turian-" + game.Name, "Saves", "account-" + account.Name));
    }

    string GetPath(SaveSlot slot, string suffix)
    {
        MemorySaveStorage.Validate(slot, default);
        return Path.Combine(directory, "slot-" + slot.Name + suffix);
    }

    /// <inheritdoc />
    public Task<StoredSave?> ReadAsync(SaveSlot slot, CancellationToken cancellationToken = default)
        => ReadFileAsync(GetPath(slot, ".save"), cancellationToken);

    /// <inheritdoc />
    public Task<StoredSave?> ReadBackupAsync(SaveSlot slot, CancellationToken cancellationToken = default)
        => ReadFileAsync(GetPath(slot, ".bak"), cancellationToken);

    async Task<StoredSave?> ReadFileAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        await using (stream)
        {
            if (stream.Length < 32 || stream.Length - 32 > maximumBytes)
            {
                throw new InvalidDataException("Save file size is invalid.");
            }
            var header = new byte[32];
            await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            var revision = Encoding.ASCII.GetString(header);
            if (!Guid.TryParseExact(revision, "N", out _))
            {
                throw new InvalidDataException("Save revision is invalid.");
            }
            var bytes = new byte[(int)stream.Length - 32];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            return new StoredSave(bytes, revision);
        }
    }

    /// <inheritdoc />
    public async Task<string> WriteAsync(SaveSlot slot, ReadOnlyMemory<byte> data, string? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        MemorySaveStorage.Validate(slot, cancellationToken);
        if (data.Length > maximumBytes)
        {
            throw new InvalidDataException("Save exceeds the storage size limit.");
        }
        await using var lease = AcquireLease(slot);
        var previous = await ReadAsync(slot, cancellationToken).ConfigureAwait(false);
        MemorySaveStorage.CheckRevision(slot, previous?.Revision, expectedRevision);
        var revision = Guid.NewGuid().ToString("N");
        var temporary = GetPath(slot, "." + revision + ".tmp");
        try
        {
            await WriteTemporaryAsync(temporary, revision, data, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var destination = GetPath(slot, ".save");
            if (previous is null)
            {
                File.Move(temporary, destination);
            }
            else
            {
                File.Replace(temporary, destination, GetPath(slot, ".bak"));
            }
            return revision;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    static async Task WriteTemporaryAsync(string path, string revision, ReadOnlyMemory<byte> data,
        CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(revision), token).ConfigureAwait(false);
        await stream.WriteAsync(data, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    FileStream AcquireLease(SaveSlot slot)
    {
        // The persistent lock file prevents an unlink/recreate race between independent game processes.
        try
        {
            return new FileStream(GetPath(slot, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new SaveConflictException(slot);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(SaveSlot slot, string expectedRevision, CancellationToken cancellationToken = default)
    {
        MemorySaveStorage.Validate(slot, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        await using var lease = AcquireLease(slot);
        var previous = await ReadAsync(slot, cancellationToken).ConfigureAwait(false);
        MemorySaveStorage.CheckRevision(slot, previous?.Revision, expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(GetPath(slot, ".bak"));
        File.Delete(GetPath(slot, ".save"));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SaveSlot>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slots = Directory.EnumerateFiles(directory, "slot-*.save")
            .Select(static path => new SaveSlot(Path.GetFileNameWithoutExtension(path)[5..]))
            .OrderBy(static slot => slot.Name).ToArray();
        return Task.FromResult<IReadOnlyList<SaveSlot>>(slots);
    }
}
