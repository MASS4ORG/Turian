namespace Turian.Editor.Core;

/// <summary>A value in a Unity YAML file.</summary>
public abstract record UnityYamlValue
{
    /// <summary>The value as text; empty for anything that is not a scalar.</summary>
    public string Text => this is UnityYamlScalar scalar ? scalar.Value : string.Empty;

    /// <summary>The value as a number; <paramref name="fallback"/> when it is not one.</summary>
    /// <param name="fallback">What to return for a missing or malformed number.</param>
    /// <returns>The number.</returns>
    public float Float(float fallback = 0f) =>
        float.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary>The value as an integer; <paramref name="fallback"/> when it is not one.</summary>
    /// <param name="fallback">What to return for a missing or malformed number.</param>
    /// <returns>The integer.</returns>
    public long Long(long fallback = 0) =>
        long.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary>The value as a mapping entry lookup: the entry named <paramref name="key"/>, or null.</summary>
    /// <param name="key">The entry name.</param>
    public UnityYamlValue? this[string key] =>
        this is UnityYamlMap map ? map.Entries.FirstOrDefault(e => e.Key == key).Value : null;

    /// <summary>The items of a sequence; none for anything else.</summary>
    public IReadOnlyList<UnityYamlValue> Items => this is UnityYamlSeq seq ? seq.Values : [];
}

/// <summary>A plain or quoted scalar.</summary>
/// <param name="Value">The text.</param>
public sealed record UnityYamlScalar(string Value) : UnityYamlValue;

/// <summary>A mapping, keeping the order its keys appear in.</summary>
/// <param name="Entries">The entries.</param>
public sealed record UnityYamlMap(IReadOnlyList<KeyValuePair<string, UnityYamlValue>> Entries) : UnityYamlValue;

/// <summary>A sequence.</summary>
/// <param name="Values">The items.</param>
public sealed record UnityYamlSeq(IReadOnlyList<UnityYamlValue> Values) : UnityYamlValue;

/// <summary>One object of a Unity asset file: <c>--- !u!&lt;class&gt; &amp;&lt;file id&gt;</c> and its body.</summary>
/// <param name="ClassId">The Unity class id, 1 for GameObject, 4 for Transform.</param>
/// <param name="FileId">The id other objects of the file refer to it by.</param>
/// <param name="TypeName">The class name the body starts with, such as <c>GameObject</c>.</param>
/// <param name="Body">The object's properties.</param>
public sealed record UnityObject(int ClassId, long FileId, string TypeName, UnityYamlValue Body);

/// <summary>
/// A reader for the YAML subset Unity writes in its text-serialized assets (scenes, prefabs, materials, metas): block
/// mappings and sequences, flow maps and lists on one line, plain and quoted scalars. Anchors, block scalars and
/// multi-line flow collections, which Unity does not write there, are not supported.
/// </summary>
public static class UnityYaml
{
    /// <summary>Reads a whole asset file into its objects. A file with no <c>---</c> header is one object of class 0.</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The objects, in file order.</returns>
    /// <exception cref="FormatException">The text is not readable as Unity YAML.</exception>
    public static IReadOnlyList<UnityObject> ParseObjects(string text)
    {
        var objects = new List<UnityObject>();
        var classId = 0;
        long fileId = 0;
        var body = new List<string>();
        var started = false;

        void Flush()
        {
            if (!started) return;
            if (Parse(string.Join('\n', body)) is UnityYamlMap { Entries.Count: > 0 } root)
            {
                var (name, value) = root.Entries[0];
                objects.Add(new UnityObject(classId, fileId, name, value));
            }

            body.Clear();
        }

        foreach (var line in text.Split('\n').Select(static l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("---", StringComparison.Ordinal))
            {
                Flush();
                started = true;
                (classId, fileId) = ParseHeader(line);
            }
            else if (line.StartsWith('%'))
            {
                continue;
            }
            else
            {
                started = true;
                body.Add(line);
            }
        }

        Flush();
        return objects;
    }

    /// <summary>Reads one YAML document body: a mapping, a sequence or a scalar.</summary>
    /// <param name="text">The text, without a <c>---</c> header.</param>
    /// <returns>The value; an empty mapping for empty text.</returns>
    /// <exception cref="FormatException">The text is not readable as Unity YAML.</exception>
    public static UnityYamlValue Parse(string text)
    {
        var lines = text.Split('\n')
            .Select(static l => l.TrimEnd('\r'))
            .Where(static l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'))
            .Select(static l => (Indent: l.Length - l.TrimStart(' ').Length, Text: l.Trim()))
            .ToList();
        if (lines.Count == 0) return new UnityYamlMap([]);

        var index = 0;
        return Block(lines, ref index, lines[0].Indent);
    }

    static (int ClassId, long FileId) ParseHeader(string line)
    {
        var classId = 0;
        long fileId = 0;
        foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            if (part.StartsWith("!u!", StringComparison.Ordinal)) _ = int.TryParse(part[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out classId);
            else if (part.StartsWith('&')) _ = long.TryParse(part[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId);
        }

        return (classId, fileId);
    }

    static UnityYamlValue Block(List<(int Indent, string Text)> lines, ref int index, int indent) =>
        lines[index].Text.StartsWith("- ", StringComparison.Ordinal) || lines[index].Text == "-"
            ? Sequence(lines, ref index, indent)
            : Mapping(lines, ref index, indent);

    static UnityYamlMap Mapping(List<(int Indent, string Text)> lines, ref int index, int indent)
    {
        var entries = new List<KeyValuePair<string, UnityYamlValue>>();
        while (index < lines.Count && lines[index].Indent == indent && !IsItem(lines[index].Text))
        {
            var text = lines[index].Text;
            var colon = KeyEnd(text);
            if (colon < 0) throw new FormatException($"Expected 'key: value', found '{text}'.");

            var key = Unquote(text[..colon]);
            var rest = text[(colon + 1)..].Trim();
            index++;

            UnityYamlValue value;
            if (rest.Length > 0)
            {
                value = Inline(rest);
            }
            else if (index < lines.Count && lines[index].Indent > indent)
            {
                value = Block(lines, ref index, lines[index].Indent);
            }
            else if (index < lines.Count && lines[index].Indent == indent && IsItem(lines[index].Text))
            {
                // Unity writes a sequence at the same indent as the key that owns it.
                value = Sequence(lines, ref index, indent);
            }
            else
            {
                value = new UnityYamlScalar(string.Empty);
            }

            entries.Add(new KeyValuePair<string, UnityYamlValue>(key, value));
        }

        return new UnityYamlMap(entries);
    }

    static UnityYamlSeq Sequence(List<(int Indent, string Text)> lines, ref int index, int indent)
    {
        var items = new List<UnityYamlValue>();
        while (index < lines.Count && lines[index].Indent == indent && IsItem(lines[index].Text))
        {
            var rest = lines[index].Text.Length > 1 ? lines[index].Text[2..].Trim() : string.Empty;
            if (rest.Length == 0)
            {
                index++;
                items.Add(index < lines.Count && lines[index].Indent > indent
                    ? Block(lines, ref index, lines[index].Indent)
                    : new UnityYamlScalar(string.Empty));
            }
            else if (!rest.StartsWith('{') && !rest.StartsWith('[') && KeyEnd(rest) >= 0)
            {
                // "- key: value" opens a mapping whose other entries line up under the key.
                lines[index] = (indent + 2, rest);
                items.Add(Mapping(lines, ref index, indent + 2));
            }
            else
            {
                index++;
                items.Add(Inline(rest));
            }
        }

        return new UnityYamlSeq(items);
    }

    static bool IsItem(string text) => text == "-" || text.StartsWith("- ", StringComparison.Ordinal);

    /// <summary>The position of the colon ending a key, or -1; colons inside quotes and braces do not count.</summary>
    static int KeyEnd(string text)
    {
        if (text.StartsWith('{') || text.StartsWith('[')) return -1;

        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0') quote = c == quote ? '\0' : quote;
            else if (c is '\'' or '"') quote = c;
            else if (c == ':' && (i == text.Length - 1 || text[i + 1] == ' ')) return i;
        }

        return -1;
    }

    static UnityYamlValue Inline(string text)
    {
        var position = 0;
        var value = Flow(text, ref position);
        return value;
    }

    static UnityYamlValue Flow(string text, ref int position)
    {
        SkipSpaces(text, ref position);
        if (position >= text.Length) return new UnityYamlScalar(string.Empty);

        switch (text[position])
        {
            case '{':
                position++;
                var entries = new List<KeyValuePair<string, UnityYamlValue>>();
                while (true)
                {
                    SkipSpaces(text, ref position);
                    if (position >= text.Length) throw new FormatException($"Unterminated '{{' in '{text}'.");
                    if (text[position] == '}') { position++; break; }
                    if (text[position] == ',') { position++; continue; }

                    var key = Scalar(text, ref position, ":");
                    SkipSpaces(text, ref position);
                    if (position < text.Length && text[position] == ':') position++;
                    entries.Add(new KeyValuePair<string, UnityYamlValue>(Unquote(key), Flow(text, ref position)));
                }

                return new UnityYamlMap(entries);
            case '[':
                position++;
                var items = new List<UnityYamlValue>();
                while (true)
                {
                    SkipSpaces(text, ref position);
                    if (position >= text.Length) throw new FormatException($"Unterminated '[' in '{text}'.");
                    if (text[position] == ']') { position++; break; }
                    if (text[position] == ',') { position++; continue; }

                    items.Add(Flow(text, ref position));
                }

                return new UnityYamlSeq(items);
            default:
                return new UnityYamlScalar(Unquote(Scalar(text, ref position, ",}]")));
        }
    }

    static string Scalar(string text, ref int position, string stops)
    {
        var start = position;
        var quote = '\0';
        for (; position < text.Length; position++)
        {
            var c = text[position];
            if (quote != '\0') quote = c == quote ? '\0' : quote;
            else if (c is '\'' or '"') quote = c;
            else if (stops.Contains(c) && (c != ':' || position + 1 >= text.Length || text[position + 1] == ' ')) break;
        }

        return text[start..position].Trim();
    }

    static void SkipSpaces(string text, ref int position)
    {
        while (position < text.Length && text[position] == ' ') position++;
    }

    static string Unquote(string text) =>
        text.Length >= 2 && ((text[0] == '\'' && text[^1] == '\'') || (text[0] == '"' && text[^1] == '"'))
            ? text[1..^1].Replace("''", "'", StringComparison.Ordinal)
            : text;
}
