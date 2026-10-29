namespace Turian.Editor.Core;

public sealed partial class AssetTypeCatalog
{
    readonly Dictionary<Type, AssetTypePresentation> presentations = [];
    readonly Dictionary<string, PayloadStamp> payloads = new(StringComparer.Ordinal);

    readonly record struct PayloadStamp(long Modified, long Length, Guid TypeId);

    /// <summary>Registers built-in file kinds and appearances supplied by Studio bricks.</summary>
    public AssetTypeCatalog(IEnumerable<IAssetPresentationProvider> providers) : this()
    {
        ArgumentNullException.ThrowIfNull(providers);
        foreach (var presentation in providers.SelectMany(static provider => provider.Presentations))
        {
            Register(presentation);
        }
    }

    /// <summary>Adds or replaces a payload presentation; closest concrete or generic base registration wins.</summary>
    public void Register(AssetTypePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentation.Id);
        presentations[presentation.PayloadType] = presentation;
        Register(new AssetTypeDescriptor(presentation.Id, presentation.DisplayName, [], AssetActivation.Inspect));
    }

    /// <summary>Resolves a concrete payload or its nearest registered base family.</summary>
    public AssetTypePresentation? PresentationFor(Type? payloadType)
    {
        for (var current = payloadType; current is not null; current = current.BaseType)
        {
            if (presentations.TryGetValue(current, out var exact)) { return exact; }
            if (current.IsGenericType && presentations.TryGetValue(current.GetGenericTypeDefinition(), out var family))
            {
                return family;
            }
        }
        return null;
    }

    /// <summary>Uses imported asset metadata or a cached DataAsset payload identity without constructing payloads.</summary>
    public AssetTypePresentation? PresentationFor(AssetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory || presentations.Count == 0) { return null; }
        if (Resolve(entry.AbsolutePath)?.Id == "turian.dataAsset"
            && TypeRegistry.TryGetType(PayloadTypeId(entry.AbsolutePath), out var type)
            && PresentationFor(type) is { } payload) { return payload; }
        return PresentationFor(entry.AssetMetadata?.GetType());
    }

    /// <summary>Resolves the payload's registered browser kind, falling back to the file extension.</summary>
    public AssetTypeDescriptor? Resolve(AssetEntry entry) => PresentationFor(entry) is { } presentation
        ? types.FirstOrDefault(type => type.Id == presentation.Id) : Resolve(entry.AbsolutePath);

    /// <summary>Matches selected presentation kinds, including registered generic families of a concrete payload.</summary>
    public bool MatchesPresentation(AssetEntry entry, IReadOnlySet<string> selected)
    {
        if (PresentationFor(entry) is not { } concrete) { return false; }
        for (var current = concrete.PayloadType; current is not null; current = current.BaseType)
        {
            if (presentations.TryGetValue(current, out var exact) && selected.Contains(exact.Id)) { return true; }
            if (current.IsGenericType && presentations.TryGetValue(current.GetGenericTypeDefinition(), out var family)
                && selected.Contains(family.Id)) { return true; }
        }
        return false;
    }

    Guid PayloadTypeId(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) { return Guid.Empty; }
            if (payloads.TryGetValue(path, out var cached) && cached.Modified == file.LastWriteTimeUtc.Ticks
                && cached.Length == file.Length) { return cached.TypeId; }
            using var stream = File.OpenRead(path);
            using var json = JsonDocument.Parse(stream);
            var id = ReadTypeId(json.RootElement);
            payloads[path] = new PayloadStamp(file.LastWriteTimeUtc.Ticks, file.Length, id);
            return id;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return Guid.Empty;
        }
    }

    static Guid ReadTypeId(JsonElement json) => json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty("__TypeId", out var value)
        && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) ? id : Guid.Empty;
}
