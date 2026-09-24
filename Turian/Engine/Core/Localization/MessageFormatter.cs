namespace Turian.Engine.Core;

/// <summary>
/// Formats the ICU MessageFormat subset a string table may use: plain <c>{name}</c> substitution, the
/// <c>#</c> plural placeholder, and <c>{name, plural, …}</c> / <c>{name, select, …}</c> blocks.
/// </summary>
/// <remarks>
/// Deliberately small — enough for a game's strings without pulling in a full ICU engine. Nested
/// blocks, <c>=N</c> exact matches and number/date skeletons are not supported; an unrecognised
/// construct is passed through verbatim rather than dropped, so a malformed string is visible in the
/// UI instead of silently losing text.
/// </remarks>
public static class MessageFormatter
{
    /// <summary>Formats a message with an optional plural count and named values.</summary>
    /// <param name="message">The raw message, possibly containing <c>{…}</c> blocks.</param>
    /// <param name="count">The count selecting a plural variant, or <c>null</c>.</param>
    /// <param name="locale">The locale whose plural rules apply.</param>
    /// <param name="values">Additional named substitutions, or <c>null</c>.</param>
    /// <returns>The formatted string.</returns>
    public static string Format(
        string message,
        decimal? count = null,
        string locale = "en",
        IReadOnlyDictionary<string, object?>? values = null)
    {
        if (string.IsNullOrEmpty(message)) return message;

        values ??= count is null
            ? emptyValues
            : new Dictionary<string, object?> { ["count"] = count };

        return FormatRange(message, 0, message.Length, values, count, locale);
    }

    static readonly IReadOnlyDictionary<string, object?> emptyValues =
        new Dictionary<string, object?>();

    static string FormatRange(
        string text,
        int start,
        int end,
        IReadOnlyDictionary<string, object?> values,
        decimal? pluralValue,
        string locale)
    {
        var result = new StringBuilder(end - start);

        for (var i = start; i < end;)
        {
            if (text[i] != '{')
            {
                // '#' is the plural count inside a plural block and is only meaningful with a count.
                if (text[i] == '#' && pluralValue is not null)
                    result.Append(pluralValue.Value.ToString(CultureInfo.InvariantCulture));
                else
                    result.Append(text[i]);
                i++;
                continue;
            }

            var close = FindClose(text, i, end);
            if (close < 0)
            {
                result.Append(text[i++]);
                continue;
            }

            var parts = text[(i + 1)..close].Split(',', 3, StringSplitOptions.TrimEntries);
            if (parts.Length == 1)
                result.Append(values.TryGetValue(parts[0], out var value) ? value : string.Empty);
            else if (parts.Length == 3 && parts[1].Equals("plural", StringComparison.OrdinalIgnoreCase))
                result.Append(SelectVariant(parts[0], parts[2], values, pluralValue, locale));
            else if (parts.Length == 3 && parts[1].Equals("select", StringComparison.OrdinalIgnoreCase))
                result.Append(SelectVariant(parts[0], parts[2], values, null, locale));
            else
                result.Append(text[i..(close + 1)]);

            i = close + 1;
        }

        return result.ToString();
    }

    static string SelectVariant(
        string name,
        string body,
        IReadOnlyDictionary<string, object?> values,
        decimal? pluralValue,
        string locale)
    {
        values.TryGetValue(name, out var raw);

        var number = pluralValue
            ?? (decimal.TryParse(raw?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0);

        // A select matches the raw option name; a plural matches its CLDR category (and then `other`).
        var category = pluralValue is not null
            ? PluralRules.GetCategory(locale, number) switch
            {
                PluralCategory.Zero => "zero",
                PluralCategory.One => "one",
                PluralCategory.Two => "two",
                PluralCategory.Few => "few",
                PluralCategory.Many => "many",
                _ => "other",
            }
            : null;
        var wanted = raw?.ToString() ?? "other";

        var selected = FindVariant(body, wanted)
                       ?? (category is not null ? FindVariant(body, category) : null)
                       ?? FindVariant(body, "other")
                       ?? string.Empty;

        return FormatRange(selected, 0, selected.Length, values, pluralValue ?? number, locale);
    }

    static string? FindVariant(string body, string name)
    {
        var marker = name + " {";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        var open = start + marker.Length - 1;
        var close = FindClose(body, open, body.Length);
        return close < 0 ? null : body[(open + 1)..close];
    }

    static int FindClose(string text, int open, int end)
    {
        var depth = 0;
        for (var i = open; i < end; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }

        return -1;
    }
}
