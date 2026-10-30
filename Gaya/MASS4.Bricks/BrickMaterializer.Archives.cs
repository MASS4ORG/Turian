using System.IO.Compression;

namespace MASS4.Bricks;

public sealed partial class BrickMaterializer
{
    async Task ArchiveAsync(BrickMaterializationSource source, string? token, string staging, string payload,
        CancellationToken cancellationToken)
    {
        var archive = Path.Combine(staging, "download");
        using (var response = await DownloadAsync(new Uri(source.Url!), token, cancellationToken).ConfigureAwait(false))
        {
            await using var file = File.Create(archive);
            await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var actual = Convert.ToHexStringLower(Convert.FromBase64String(BrickArchive.ComputeIntegrity(archive)[7..]));
        if (!actual.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageException("The downloaded archive does not match its SHA256.");
        }

        await ExtractArchiveAsync(archive, payload, cancellationToken).ConfigureAwait(false);
        UnwrapProducer(staging, payload);
    }

    static async Task ExtractArchiveAsync(string archive, string payload, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(archive);
        var magic = new byte[2];
        _ = await file.ReadAsync(magic, cancellationToken).ConfigureAwait(false);
        file.Position = 0;
        if (magic[0] == 'P' && magic[1] == 'K')
        {
            ExtractZip(archive, payload);
            return;
        }

        await using Stream tar = magic[0] == 0x1f && magic[1] == 0x8b
            ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true)
            : file;
        await ExtractTarAsync(tar, payload, cancellationToken).ConfigureAwait(false);
    }

    static async Task ExtractTarAsync(Stream tar, string payload, CancellationToken cancellationToken)
    {
        using var reader = new TarReader(tar, leaveOpen: true);
        while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
        {
            var target = EntryPath(payload, entry.Name);
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
            }
            else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await entry.ExtractToFileAsync(target, overwrite: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new PackageException($"Archive entry '{entry.Name}' is not a regular file or directory.");
            }
        }
    }

    static void UnwrapProducer(string staging, string payload)
    {
        if (!File.Exists(Path.Combine(payload, PackageManifest.FileName)) && Directory.Exists(payload))
        {
            var entries = Directory.GetFileSystemEntries(payload);
            if (entries.Length == 1 && Directory.Exists(entries[0]))
            {
                var producer = Path.Combine(staging, "producer");
                Directory.Move(entries[0], producer);
                Directory.Delete(payload);
                Directory.Move(producer, payload);
            }
        }
    }

    async Task<HttpResponseMessage> DownloadAsync(Uri uri, string? token, CancellationToken cancellationToken)
    {
        var origin = uri.GetLeftPart(UriPartial.Authority);
        for (var redirects = 0; redirects < 6; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(token) && uri.GetLeftPart(UriPartial.Authority) == origin)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                var next = response.Headers.Location;
                response.Dispose();
                uri = RedirectLocation(uri, next);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = response.StatusCode;
            response.Dispose();
            throw new PackageException($"Archive fetch failed: HTTP {(int)status} {status}.");
        }

        throw new PackageException("Archive fetch exceeded the redirect limit.");
    }

    static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    static Uri RedirectLocation(Uri previous, Uri? location)
    {
        if (location is null || new Uri(previous, location) is not { Scheme: "https", UserInfo.Length: 0 } next)
        {
            throw new PackageException("Archive redirect must point to HTTPS without credentials.");
        }

        return next;
    }

    static void ExtractZip(string archive, string payload)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var target = EntryPath(payload, entry.FullName);
            var kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (kind is not (0 or 0x8000 or 0x4000))
            {
                throw new PackageException($"Archive entry '{entry.FullName}' is not a regular file or directory.");
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }
    }

    static string EntryPath(string root, string name)
    {
        if (name.Contains('\\') || name.Contains(':') || Path.IsPathRooted(name)
            || name.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new PackageException($"Unsafe archive entry '{name}'.");
        }

        return Path.GetFullPath(name, root);
    }
}
