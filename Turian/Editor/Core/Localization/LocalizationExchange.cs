using System.Xml.Linq;

namespace Turian.Editor.Core;

/// <summary>Lossless XLIFF 1.2 and RFC-4180 CSV interchange for string tables.</summary>
/// <remarks>
/// Imports map the interchange format onto a <see cref="StringTable"/>, so a translation agency can
/// hand back an XLIFF/CSV export and the editor re-imports it into the project's <c>.strings</c>
/// assets. Exports preserve key/source/translation and the translator note and state where the
/// format allows; plural variants are kept in the JSON tables and are not flattened, so an
/// interchange round-trip is expected to carry the singular form only.
/// </remarks>
public static class LocalizationExchange
{
    /// <summary>Parses an XLIFF 1.2 document into a table for its target language.</summary>
    /// <param name="xml">The XLIFF document text.</param>
    /// <returns>The parsed table.</returns>
    /// <exception cref="InvalidDataException">Raised when the document lacks a <c>file</c> element.</exception>
    public static StringTable ReadXliff(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var root = XDocument.Parse(xml).Root ?? throw new InvalidDataException("XLIFF has no root element.");
        var file = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "file")
                   ?? throw new InvalidDataException("XLIFF has no file element.");
        var locale = (string?)file.Attribute("target-language") ?? (string?)file.Attribute("source-language") ?? "en";
        var table = new StringTable(locale);

        foreach (var unit in root.Descendants().Where(e => e.Name.LocalName == "trans-unit"))
        {
            var source = unit.Elements().FirstOrDefault(e => e.Name.LocalName == "source")?.Value ?? string.Empty;
            var targetElement = unit.Elements().FirstOrDefault(e => e.Name.LocalName == "target");
            var target = targetElement?.Value;
            var note = unit.Elements().FirstOrDefault(e => e.Name.LocalName == "note")?.Value;

            table.Add(new StringTableEntry
            {
                Key = (string?)unit.Attribute("id") ?? source,
                Source = source,
                Translation = string.IsNullOrEmpty(target) ? null : target,
                Note = note,
                State = (string?)targetElement?.Attribute("state") ?? "new",
            });
        }

        return table;
    }

    /// <summary>Serializes a table as an XLIFF 1.2 document targeting its locale.</summary>
    /// <param name="table">The table to export.</param>
    /// <returns>The XLIFF document text.</returns>
    public static string WriteXliff(StringTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var file = new XElement("file",
            new XAttribute("source-language", "en"),
            new XAttribute("target-language", table.Locale),
            new XAttribute("datatype", "plaintext"),
            new XElement("body", table.Entries.Select(entry =>
                new XElement("trans-unit",
                    new XAttribute("id", entry.Key),
                    new XElement("source", entry.Source),
                    entry.Translation is null
                        ? null
                        : new XElement("target", new XAttribute("state", entry.State), entry.Translation),
                    entry.Note is null ? null : new XElement("note", entry.Note)))));

        return new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement("xliff", new XAttribute("version", "1.2"), file)).ToString();
    }

    /// <summary>Parses an RFC-4180 CSV document (key,source,translation,note,state) into a table.</summary>
    /// <param name="csv">The CSV text.</param>
    /// <param name="locale">The locale the rows belong to.</param>
    /// <returns>The parsed table.</returns>
    /// <exception cref="InvalidDataException">Raised when the CSV is empty.</exception>
    public static StringTable ReadCsv(string csv, string locale)
    {
        ArgumentNullException.ThrowIfNull(csv);

        var rows = ParseCsv(csv).ToList();
        if (rows.Count == 0) throw new InvalidDataException("CSV is empty.");

        var header = rows[0].Select((value, index) => (value, index))
            .ToDictionary(pair => pair.value, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        var table = new StringTable(locale);

        foreach (var row in rows.Skip(1))
        {
            if (!header.TryGetValue("key", out var keyIndex) || keyIndex >= row.Count) continue;

            var translation = Value(row, header, "translation");
            table.Add(new StringTableEntry
            {
                Key = row[keyIndex],
                Source = Value(row, header, "source"),
                Translation = translation.Length > 0 ? translation : null,
                Note = Value(row, header, "note"),
                State = Value(row, header, "state") is { Length: > 0 } state ? state : "new",
            });
        }

        return table;
    }

    /// <summary>Serializes a table as RFC-4180 CSV with a header row.</summary>
    /// <param name="table">The table to export.</param>
    /// <returns>The CSV text.</returns>
    public static string WriteCsv(StringTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var builder = new StringBuilder("key,source,translation,note,state\n");
        foreach (var entry in table.Entries)
        {
            builder.AppendJoin(',', new[]
            {
                entry.Key, entry.Source, entry.Translation ?? string.Empty, entry.Note ?? string.Empty, entry.State,
            }.Select(Escape)).Append('\n');
        }

        return builder.ToString();
    }

    static string Value(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> header, string name) =>
        header.TryGetValue(name, out var index) && index < row.Count ? row[index] : string.Empty;

    static IEnumerable<IReadOnlyList<string>> ParseCsv(string csv)
    {
        using var reader = new StringReader(csv);
        var fields = new List<string>();
        var value = new StringBuilder();
        var quoted = false;

        while (reader.Read() is var code && code >= 0)
        {
            var ch = (char)code;
            if (ch == '"')
            {
                if (quoted && reader.Peek() == '"')
                {
                    value.Append('"');
                    reader.Read();
                }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else if ((ch == '\n' || ch == '\r') && !quoted)
            {
                if (ch == '\r' && reader.Peek() == '\n') reader.Read();
                fields.Add(value.ToString());
                value.Clear();
                if (fields.Any(static field => field.Length > 0)) yield return fields.ToArray();
                fields.Clear();
            }
            else value.Append(ch);
        }

        if (value.Length > 0 || fields.Count > 0)
        {
            fields.Add(value.ToString());
            yield return fields.ToArray();
        }
    }

    static string Escape(string value) =>
        value.Contains(',', StringComparison.InvariantCulture) ||
        value.Contains('"', StringComparison.InvariantCulture) ||
        value.Contains('\n', StringComparison.InvariantCulture)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}
