namespace Gaya.Host;

/// <summary>
/// One settings file — the user's or a project's — keyed by page id. The file is a convenience, never
/// a requirement: an unreadable file, an entry for a page no plugin contributes any more, or a member
/// that has since changed type is discarded and the page keeps its defaults.
/// </summary>
public sealed class StudioSettingsStore(ILogger log, string path)
{
    static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    JsonObject? stored;

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

        if (Load()?[page.Id] is not JsonObject values) return;

        foreach (var (name, value) in values)
        {
            if (value is null) continue;
            if (Writable(page.Target.GetType(), name) is not { } member) continue;

            try
            {
                SetValue(member, page.Target, value.Deserialize(MemberType(member), jsonOptions));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or TargetInvocationException)
            {
                log.LogWarning(ex, "Settings: {Page}.{Member} could not be restored", page.Id, name);
            }
        }
    }

    /// <summary>Writes every page's current values, replacing the file.</summary>
    /// <param name="pages">The pages to persist.</param>
    public void Write(IEnumerable<SettingsPageDescriptor> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        var document = new JsonObject();
        foreach (var page in pages)
        {
            try
            {
                document[page.Id] = JsonSerializer.SerializeToNode(page.Target, page.Target.GetType(), jsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                log.LogWarning(ex, "Settings: page {Page} could not be serialized", page.Id);
            }
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(Path, document.ToJsonString(jsonOptions));
            stored = document;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Settings: could not be saved to {Path}", Path);
        }
    }

    /// <summary>The file's contents, read once and kept, or null when there is nothing usable.</summary>
    JsonObject? Load()
    {
        if (stored is not null) return stored;

        try
        {
            if (!File.Exists(Path)) return null;
            stored = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject;
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
