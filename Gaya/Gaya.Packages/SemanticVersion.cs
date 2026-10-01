namespace Gaya.Packages;

/// <summary>A Semantic Versioning 2.0 version: <c>major.minor.patch[-prerelease][+build]</c>.</summary>
[JsonConverter(typeof(SemanticVersionJsonConverter))]
public sealed record SemanticVersion(int Major, int Minor, int Patch, string Prerelease = "", string Build = "")
    : IComparable<SemanticVersion>
{
    /// <summary>Whether this is a prerelease, which sorts before the release it precedes.</summary>
    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>Reads a version.</summary>
    /// <param name="text">The version text.</param>
    /// <returns>The version.</returns>
    /// <exception cref="FormatException">The text is not a semantic version.</exception>
    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a semantic version.");

    /// <summary>Reads a version, reporting failure instead of throwing.</summary>
    /// <param name="text">The version text; a leading <c>v</c>, as in git tags, is accepted.</param>
    /// <param name="version">The version read.</param>
    /// <returns>True when the text is a semantic version.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.Trim();
        if (span.StartsWith('v') || span.StartsWith('V')) span = span[1..];

        var build = string.Empty;
        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            build = span[(plus + 1)..];
            span = span[..plus];
            if (!IsIdentifierList(build)) return false;
        }

        var prerelease = string.Empty;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = span[(dash + 1)..];
            span = span[..dash];
            if (!IsIdentifierList(prerelease)) return false;
        }

        var parts = span.Split('.');
        if (parts.Length != 3
            || !TryParseNumber(parts[0], out var major)
            || !TryParseNumber(parts[1], out var minor)
            || !TryParseNumber(parts[2], out var patch))
            return false;

        version = new SemanticVersion(major, minor, patch, prerelease, build);
        return true;
    }

    /// <inheritdoc/>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0) return core;
        if (IsPrerelease != other.IsPrerelease) return IsPrerelease ? -1 : 1;

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>Whether <paramref name="left"/> sorts before <paramref name="right"/>.</summary>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left"/> sorts after <paramref name="right"/>.</summary>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left"/> sorts before or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left"/> sorts after or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    /// <summary>Whether two versions have equal precedence; build metadata is ignored, as SemVer requires.</summary>
    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    /// <inheritdoc/>
    public override string ToString()
    {
        var text = $"{Major}.{Minor}.{Patch}";
        if (IsPrerelease) text += $"-{Prerelease}";
        if (Build.Length > 0) text += $"+{Build}";
        return text;
    }

    static int ComparePrerelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = int.TryParse(a[i], out var aNumber);
            var bNumeric = int.TryParse(b[i], out var bNumber);

            var order = (aNumeric, bNumeric) switch
            {
                (true, true) => aNumber.CompareTo(bNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (order != 0) return order;
        }

        return a.Length.CompareTo(b.Length);
    }

    static bool TryParseNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && (text.Length == 1 || text[0] != '0');

    static bool IsIdentifierList(string text) =>
        text.Length > 0 && text.Split('.').All(static id => id.Length > 0 && id.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'));
}

/// <summary>Writes a <see cref="SemanticVersion"/> as its text.</summary>
public sealed class SemanticVersionJsonConverter : JsonConverter<SemanticVersion>
{
    /// <inheritdoc/>
    public override SemanticVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return SemanticVersion.TryParse(text, out var version)
            ? version
            : throw new JsonException($"'{text}' is not a semantic version.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, SemanticVersion value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
