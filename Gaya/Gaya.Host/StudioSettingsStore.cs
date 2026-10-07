namespace Gaya.Host;

/// <summary>
/// Stores user or workspace preferences by page id, preserving unloaded pages and externally changed values.
/// Unreadable files retain their contents while the editor uses defaults.
/// </summary>
public sealed class StudioSettingsStore(ILogger log, string path)
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    JsonObject? stored;
    readonly Dictionary<string, JsonNode?> restored = new(StringComparer.Ordinal);

    /// <summary>The file this store reads and writes.</summary>
    public string Path { get; } = path;

    /// <summary>
    /// Copies the values saved for a page onto its target. Members the file does not mention keep
    /// whatever the class initialised them to, so adding an option never invalidates a stored file.
    /// </summary>
    /// <param name="page">The page to restore.</param>
    public void Restore(SettingsPageDescriptor page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (Load()?[page.Id] is JsonObject values) RestoreMembers(page, values);
        restored[page.Id] = SerializePage(page)?.DeepClone();
    }

    void RestoreMembers(SettingsPageDescriptor page, JsonObject values)
    {
        foreach (var (name, value) in values)
        {
            if (value is null) continue;
            if (Writable(page.Target.GetType(), name) is not { } member) continue;

            try
            {
                SetValue(member, page.Target, value.Deserialize(MemberType(member), JsonOptions));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or TargetInvocationException)
            {
                log.LogWarning(ex, "Settings: {Page}.{Member} could not be restored", page.Id, name);
            }
        }
    }

    /// <summary>Merges changed page values into the latest file and retains a backup of the previous contents.</summary>
    /// <param name="pages">The pages to persist.</param>
    public void Write(IEnumerable<SettingsPageDescriptor> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        try
        {
            var document = ReadDocument() ?? new JsonObject();
            var snapshots = new Dictionary<string, JsonNode?>();
            foreach (var page in pages)
            {
                if (SerializePage(page) is not { } values) continue;
                MergePage(document, page.Id, values);
                snapshots[page.Id] = values;
            }

            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            WriteDocument(document);
            stored = document;
            foreach (var (id, values) in snapshots) restored[id] = values;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.LogWarning(ex, "Settings: could not be saved to {Path}", Path);
        }
    }

    JsonNode? SerializePage(SettingsPageDescriptor page)
    {
        try { return JsonSerializer.SerializeToNode(page.Target, page.Target.GetType(), JsonOptions); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            log.LogWarning(ex, "Settings: page {Page} could not be serialized", page.Id);
            return null;
        }
    }

    void MergePage(JsonObject document, string id, JsonNode values)
    {
        restored.TryGetValue(id, out var baseline);
        if (JsonNode.DeepEquals(values, baseline) && document.ContainsKey(id)) return;
        if (values is not JsonObject members || document[id] is not JsonObject existing)
        {
            document[id] = values.DeepClone();
            return;
        }
        foreach (var (name, value) in members)
        {
            if (baseline is JsonObject original && JsonNode.DeepEquals(value, original[name])
                && existing.ContainsKey(name)) continue;
            existing[name] = value?.DeepClone();
        }
    }

    void WriteDocument(JsonObject document)
    {
        var temporary = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, document.ToJsonString(JsonOptions));
            if (File.Exists(Path)) File.Copy(Path, $"{Path}.bak", overwrite: true);
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    JsonObject? ReadDocument() => !File.Exists(Path) ? null
        : JsonNode.Parse(File.ReadAllText(Path)) as JsonObject
          ?? throw new JsonException("Settings must contain an object keyed by page id.");

    /// <summary>The file's contents, read once and kept, or null when there is nothing usable.</summary>
    JsonObject? Load()
    {
        if (stored is not null) return stored;

        try
        {
            stored = ReadDocument();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.LogWarning(ex, "Settings: could not be read from {Path}", Path);
        }

        return stored;
    }

    /// <summary>The public property or field a stored name refers to, or null when there is none.</summary>
    static MemberInfo? Writable(Type type, string name)
    {
        const BindingFlags scope = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

        if (type.GetProperty(name, scope) is { CanWrite: true } property) return property;
        return type.GetField(name, scope) is { IsInitOnly: false } field ? field : null;
    }

    static Type MemberType(MemberInfo member) =>
        member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;

    static void SetValue(MemberInfo member, object target, object? value)
    {
        if (member is PropertyInfo property) property.SetValue(target, value);
        else ((FieldInfo)member).SetValue(target, value);
    }
}
