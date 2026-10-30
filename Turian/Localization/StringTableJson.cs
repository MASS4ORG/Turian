namespace MASS4.Localization;

/// <summary>
/// JSON serialization for a <see cref="StringTable"/>. The shape is shared by the <c>.strings</c>
/// source files, their baked import artifacts, and the
/// <c>LocalizationExchange</c> exporters:
///
/// <code>
/// {
///   "locale": "pt-BR",
///   "entries": [
///     { "key": "menu.file", "source": "File", "translation": "Arquivo", "state": "translated",
///       "note": "Main menu, first item", "plurals": { "one": "# item", "other": "# items" } }
///   ]
/// }
/// </code>
/// </summary>
public static partial class StringTableJson
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter<PluralCategory>(JsonNamingPolicy.CamelCase) },
    };
    static readonly StringTableJsonContext JsonContext = new(Options);

    [JsonSerializable(typeof(TableDto))]
    sealed partial class StringTableJsonContext : JsonSerializerContext;

    /// <summary>Parses a table from its JSON text.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The parsed table.</returns>
    /// <exception cref="JsonException">Raised for malformed JSON.</exception>
    /// <exception cref="ArgumentException">Raised when the JSON lacks a locale.</exception>
    public static StringTable Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var dto = JsonSerializer.Deserialize(json, JsonContext.TableDto);
        if (string.IsNullOrWhiteSpace(dto?.Locale))
        {
            throw new ArgumentException("A string table JSON document requires a locale.", nameof(json));
        }

        var table = new StringTable(dto.Locale);
        foreach (var entry in dto.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            table.Add(new StringTableEntry
            {
                Key = entry.Key ?? throw new JsonException("A string table entry requires a key."),
                Source = entry.Source ?? throw new JsonException("A string table entry requires source text."),
                Translation = entry.Translation,
                Note = entry.Note,
                State = entry.State ?? "new",
                Plurals = entry.Plurals is null
                    ? []
                    : new Dictionary<PluralCategory, string>(
                        entry.Plurals.Where(static kv => kv.Value is not null).Select(static kv => new KeyValuePair<PluralCategory, string>(kv.Key, kv.Value))),
            });
        }

        return table;
    }

    /// <summary>Serializes a table to JSON text.</summary>
    /// <param name="table">The table to serialize.</param>
    /// <returns>The JSON text.</returns>
    public static string Save(StringTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var dto = new TableDto
        {
            Locale = table.Locale,
            Entries = [.. table.Entries
                .Select(static e => new EntryDto
                {
                    Key = e.Key,
                    Source = e.Source,
                    Translation = e.Translation,
                    Note = e.Note,
                    State = e.State,
                    Plurals = e.Plurals.Count > 0 ? e.Plurals : null,
                })],
        };

        return JsonSerializer.Serialize(dto, JsonContext.TableDto);
    }

    sealed class TableDto
    {
        public string? Locale { get; set; }
        public EntryDto[] Entries { get; set; } = [];
    }

    sealed class EntryDto
    {
        public string? Key { get; set; }
        public string? Source { get; set; }
        public string? Translation { get; set; }
        public string? Note { get; set; }
        public string? State { get; set; }
        public Dictionary<PluralCategory, string>? Plurals { get; set; }
    }
}
