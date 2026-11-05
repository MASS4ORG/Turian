using Android.OS;
using Microsoft.Win32.SafeHandles;
using AssetManager = Android.Content.Res.AssetManager;

namespace Turian.Android;

/// <summary>Reads stored APK assets through positional file-descriptor windows without extracting them.</summary>
public sealed class ApkContentDirectory(AssetManager assets, string packageName, long versionCode) : IContentDirectory
{
    /// <inheritdoc />
    public string Id => $"apk:{packageName}";

    /// <inheritdoc />
    public ContentIdentity? Probe(string path)
    {
        path = ValidatePath(path);
        if (!Exists(path))
        {
            return null;
        }
        using var source = Open(path);
        return source.Identity;
    }

    /// <inheritdoc />
    public IContentSource Open(string path)
    {
        path = ValidatePath(path);
        try
        {
            using var descriptor = assets.OpenFd(path);
            using var duplicate = ParcelFileDescriptor.Dup(descriptor.FileDescriptor!);
            var handle = new SafeFileHandle(duplicate!.DetachFd(), ownsHandle: true);
            try
            {
                var identity = new ContentIdentity($"{Id}/{path}",
                    FormattableString.Invariant($"{versionCode}:{descriptor.Length}"));
                return new FileContentSource(handle, descriptor.StartOffset, descriptor.Length, identity, ownsHandle: true);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (Java.IO.FileNotFoundException exception)
        {
            if (!Exists(path))
            {
                throw new FileNotFoundException($"APK asset '{path}' was not found.", path, exception);
            }
            throw new InvalidDataException($"APK asset '{path}' is compressed. Add its extension to AndroidStoreUncompressedFileExtensions; .oap content must be stored uncompressed.", exception);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> List(string folder, string extension)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);
        folder = string.IsNullOrEmpty(folder) ? string.Empty : ValidatePath(folder);
        return (assets.List(folder) ?? [])
            .Where(name => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Select(name => string.IsNullOrEmpty(folder) ? name : $"{folder}/{name}")
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    bool Exists(string path)
    {
        var slash = path.LastIndexOf('/');
        return (assets.List(slash < 0 ? string.Empty : path[..slash]) ?? [])
            .Contains(path[(slash + 1)..], StringComparer.Ordinal);
    }

    static string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\\') || path.Contains(':') || path.Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new ArgumentException("APK content paths must be relative paths without traversal.", nameof(path));
        }
        return path;
    }
}
