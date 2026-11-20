namespace Gaya.Host;

/// <summary>
/// Stores user or workspace preferences by page id, preserving unloaded pages and externally changed values.
/// Unreadable files retain their contents while the editor uses defaults.
/// </summary>
public sealed class StudioSettingsStore(ILogger log, string path)
{
    static JsonSerializerOptions JsonOptions => SettingsFile.JsonOptions;

    readonly SettingsFile file = new(path, (message, exception) => log.LogWarning(exception, "{Message}", message));
    readonly Dictionary<string, JsonNode?> restored = new(StringComparer.Ordinal);

    /// <summary>The file this store reads and writes.</summary>
    public string Path => file.Path;

    /// <summary>
    /// Copies the values saved for a page onto its target. Members the file does not mention keep
    /// whatever the class initialised them to, so adding an option never invalidates a stored file.
    /// </summary>
    /// <param name="page">The page to restore.</param>
    /// <param name="previousId">An earlier storage id used when migrating the same settings object.</param>
    public void Restore(SettingsPageDescriptor page, string? previousId = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        Apply(page, previousId);
        restored[page.Id] = SerializePage(page)?.DeepClone();
    }

    /// <summary>
    /// Copies only the members this file mentions onto a page's target, without recording a baseline.
    /// This is how one layer of a cascade is applied over the layers beneath it.
    /// </summary>
    /// <param name="page">The page to apply onto.</param>
    /// <param name="previousId">An earlier storage id used when migrating the same settings object.</param>
    public void Apply(SettingsPageDescriptor page, string? previousId = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (file.Load()?[previousId ?? page.Id] is JsonObject values)
        {
            RestoreMembers(page, values);
        }
    }

    /// <summary>Whether the file holds a value for a member of a page.</summary>
    /// <param name="pageId">The page's storage id.</param>
    /// <param name="member">The member's name; matched without regard to case, as a restore does.</param>
    public bool Has(string pageId, string member) => file.Has(pageId, member);

    /// <summary>The JSON a file holds for a member of a page, or null when it holds none.</summary>
    /// <param name="pageId">The page's storage id.</param>
    /// <param name="member">The member's name.</param>
    public JsonNode? Get(string pageId, string member) => file.Get(pageId, member);

    /// <summary>
    /// Changes individual members of one page in the latest contents of the file and writes it back,
    /// keeping a backup. A file left holding nothing is deleted.
    /// </summary>
    /// <param name="pageId">The page's storage id.</param>
    /// <param name="set">Members to store, by name.</param>
    /// <param name="remove">Members to take out, by name.</param>
    /// <returns>Whether the file now holds the change. A file that cannot be read or written is left as it was.</returns>
    public bool Update(string pageId, IReadOnlyDictionary<string, JsonNode?> set, IReadOnlyCollection<string> remove) =>
        file.Update(pageId, set, remove);

    /// <summary>Whether another process changed the file since this store last read or wrote it.</summary>
    public bool ChangedOnDisk() => file.ChangedOnDisk();

    /// <summary>Forgets what was read, so the next restore reads the file again.</summary>
    public void Reload() => file.Invalidate();

    /// <summary>A page's members as JSON, the way they are stored; null when the page cannot be serialized.</summary>
    /// <param name="page">The page to read.</param>
    public JsonObject? Snapshot(SettingsPageDescriptor page) => SerializePage(page) as JsonObject;

    /// <summary>Converts a value to the JSON a member of a type stores.</summary>
    /// <param name="type">The settings class.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="value">The value to convert.</param>
    /// <exception cref="ArgumentException">The class has no such writable member, or the value does not fit it.</exception>
    public static JsonNode? ToNode(Type type, string member, object? value)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (Writable(type, member) is not { } info)
        {
            throw new ArgumentException($"{type.Name} has no settable member {member}.", nameof(member));
        }

        try
        {
            return JsonSerializer.SerializeToNode(value, MemberType(info), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new ArgumentException($"{member} cannot hold that value.", nameof(value), ex);
        }
    }

    /// <summary>Converts stored JSON to the type of a member.</summary>
    /// <param name="type">The settings class.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="node">The stored JSON.</param>
    public static object? FromNode(Type type, string member, JsonNode? node)
    {
        ArgumentNullException.ThrowIfNull(type);

        return Writable(type, member) is { } info && node is not null
            ? node.Deserialize(MemberType(info), JsonOptions)
            : null;
    }

    void RestoreMembers(SettingsPageDescriptor page, JsonObject values)
    {
        foreach (var (name, value) in values)
        {
            if (value is null)
            {
                continue;
            }

            if (Writable(page.Target.GetType(), name) is not { } member)
            {
                continue;
            }

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
            var document = file.ReadDocument() ?? new JsonObject();
            var snapshots = new Dictionary<string, JsonNode?>();
            foreach (var page in pages)
            {
                if (SerializePage(page) is not { } values)
                {
                    continue;
                }

                MergePage(document, page.Id, values);
                snapshots[page.Id] = values;
            }

            file.WriteDocument(document);
            foreach (var (id, values) in snapshots)
            {
                restored[id] = values;
            }
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
        if (JsonNode.DeepEquals(values, baseline) && document.ContainsKey(id))
        {
            return;
        }

        if (values is not JsonObject members || document[id] is not JsonObject existing)
        {
            document[id] = values.DeepClone();
            return;
        }
        foreach (var (name, value) in members)
        {
            if (baseline is JsonObject original && JsonNode.DeepEquals(value, original[name])
                && existing.ContainsKey(name))
            {
                continue;
            }

            existing[name] = value?.DeepClone();
        }
    }

    /// <summary>The public property or field a stored name refers to, or null when there is none.</summary>
    static MemberInfo? Writable(Type type, string name)
    {
        const BindingFlags scope = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

        if (type.GetProperty(name, scope) is { CanWrite: true } property)
        {
            return property;
        }

        return type.GetField(name, scope) is { IsInitOnly: false } field ? field : null;
    }

    static Type MemberType(MemberInfo member) =>
        member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;

    static void SetValue(MemberInfo member, object target, object? value)
    {
        if (member is PropertyInfo property)
        {
            property.SetValue(target, value);
        }
        else
        {
            ((FieldInfo)member).SetValue(target, value);
        }
    }
}
