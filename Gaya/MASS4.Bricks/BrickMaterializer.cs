namespace MASS4.Bricks;

/// <summary>Fetches verified producer layouts into writable folders without changing a project's package resolution.</summary>
/// <param name="http">The HTTP transport; a shared client when omitted.</param>
/// <param name="reservedCategoryPrefixes">Category prefixes reserved by the calling host.</param>
public sealed partial class BrickMaterializer(HttpClient? http = null,
    IReadOnlyCollection<string>? reservedCategoryPrefixes = null)
{
    static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    readonly HttpClient http = http ?? SharedHttp;

    /// <summary>Materializes a pinned source atomically, refusing to overwrite an existing producer folder.</summary>
    /// <param name="source">The validated source description.</param>
    /// <param name="destination">The output folder, absent or empty.</param>
    /// <param name="registry">The store location and independently trusted keys, required for store sources.</param>
    /// <param name="token">An optional fetch-only token; never saved with the materialized files.</param>
    /// <param name="cancellationToken">Cancels fetching and extraction.</param>
    /// <returns>The absolute producer folder.</returns>
    public async Task<string> MaterializeAsync(BrickMaterializationSource source, string destination,
        ScopedRegistry? registry = null, string? token = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.Validate();
        destination = Path.GetFullPath(destination);
        EnsureEmpty(destination);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new PackageException("The materialization destination must have a parent folder.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".materialize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var payload = Path.Combine(staging, "payload");
            switch (source.Type)
            {
                case "store":
                    await StoreAsync(source, registry ?? throw new PackageException("A store source needs a registry."),
                        token, staging, payload, cancellationToken).ConfigureAwait(false);
                    break;
                case "git":
                    await GitAsync(source, token, payload, cancellationToken).ConfigureAwait(false);
                    break;
                case "archive":
                    await ArchiveAsync(source, token, staging, payload, cancellationToken).ConfigureAwait(false);
                    break;
            }

            _ = PackageManifest.Load(payload, reservedCategoryPrefixes);
            EnsureEmpty(destination);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination);
            }

            Directory.Move(payload, destination);
            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException
                                   or InvalidDataException or JsonException)
        {
            throw new PackageException($"Could not materialize {source.Type} source: {ex.Message}", ex);
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    static void EnsureEmpty(string destination)
    {
        if (File.Exists(destination) || new DirectoryInfo(destination).LinkTarget is not null
            || (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()))
        {
            throw new PackageException($"Materialization destination '{destination}' must be absent or empty.");
        }
    }

    async Task StoreAsync(BrickMaterializationSource source, ScopedRegistry registry, string? token,
        string staging, string payload, CancellationToken cancellationToken)
    {
        var client = new RegistryClient(registry, http, token);
        var index = await client.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        var entry = SourceEntry(index, source, registry.Name);
        ValidateStoreEntry(entry, source);
        _ = RegistryTrust.Verify(registry, source.Id!, source.Version!, entry);
        var archive = await client.DownloadAsync(source.Id!, source.Version!, entry, staging, cancellationToken)
            .ConfigureAwait(false);
        ValidateStoreManifest(archive, source);
        ExtractZip(archive, payload);
    }

    static RegistryVersion SourceEntry(RegistryIndex index, BrickMaterializationSource source, string registryName)
    {
        if (!index.Bricks.TryGetValue(source.Id!, out var brick)
            || !brick.Versions.TryGetValue(source.Version!, out var entry))
        {
            throw new PackageException($"{registryName} does not serve {source.Id}@{source.Version}.");
        }

        if (entry.Yanked)
        {
            throw new PackageException($"{source.Id}@{source.Version} has been yanked.");
        }

        if (entry.Variants is not null)
        {
            entry = entry.Variants.GetValueOrDefault(PackageVariant.Source)
                ?? throw new PackageException($"{source.Id}@{source.Version} has no source variant.");
        }

        return entry;
    }

    static void ValidateStoreEntry(RegistryVersion entry, BrickMaterializationSource source)
    {
        if (entry.Variant is not (null or PackageVariant.Source)
            || (entry.PackageId is not null && entry.PackageId != source.Id)
            || (entry.Version is not null && entry.Version != source.Version))
        {
            throw new PackageException("The store source entry describes another identity or variant.");
        }

    }

    void ValidateStoreManifest(string archive, BrickMaterializationSource source)
    {
        var manifest = BrickArchive.ReadManifest(archive, reservedCategoryPrefixes);
        if (manifest.Name != source.Id || manifest.Version!.ToString() != source.Version
            || manifest.BakeOf is not null || manifest.Variant is not (null or PackageVariant.Source))
        {
            throw new PackageException("The downloaded archive does not match the requested source identity and version.");
        }
    }
}
