namespace Gaya.Packages;

/// <summary>
/// A set of acceptable versions in npm syntax: <c>1.2.3</c>, <c>^1.2.3</c>, <c>~1.2.3</c>, <c>1.2</c>, <c>*</c>,
/// comparators (<c>&gt;=1.0.0 &lt;2.0.0</c>, space-separated = all must hold) and alternatives (<c>||</c>).
/// A prerelease only matches a comparator set that names a prerelease of the same <c>major.minor.patch</c>.
/// </summary>
[JsonConverter(typeof(VersionRangeJsonConverter))]
public sealed class VersionRange
{
    readonly IReadOnlyList<IReadOnlyList<(string Op, SemanticVersion Version)>> sets;
    readonly string text;

    VersionRange(string text, IReadOnlyList<IReadOnlyList<(string, SemanticVersion)>> sets)
    {
        this.text = text;
        this.sets = sets;
    }

    /// <summary>Every version.</summary>
    public static VersionRange Any { get; } = Parse("*");

    /// <summary>Reads a range.</summary>
    /// <param name="text">The range text.</param>
    /// <returns>The range.</returns>
    /// <exception cref="FormatException">The text is not a version range.</exception>
    public static VersionRange Parse(string text) =>
        TryParse(text, out var range) ? range : throw new FormatException($"'{text}' is not a version range.");

    /// <summary>Reads a range, reporting failure instead of throwing.</summary>
    /// <param name="text">The range text.</param>
    /// <param name="range">The range read.</param>
    /// <returns>True when the text is a version range.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out VersionRange? range)
    {
        range = null;
        if (text is null) return false;

        var sets = new List<IReadOnlyList<(string, SemanticVersion)>>();
        foreach (var alternative in text.Split("||"))
        {
            var comparators = new List<(string, SemanticVersion)>();
            foreach (var token in alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryExpand(token, comparators)) return false;
            }

            sets.Add(comparators);
        }

        range = new VersionRange(text.Trim(), sets);
        return true;
    }

    /// <summary>Whether <paramref name="version"/> is in the range.</summary>
    /// <param name="version">The version to test.</param>
    /// <returns>True when it is acceptable.</returns>
    public bool IsSatisfiedBy(SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return sets.Any(set => set.All(c => Holds(c.Op, version, c.Version))
                               && (!version.IsPrerelease || set.Any(c => c.Version.IsPrerelease
                                   && (c.Version.Major, c.Version.Minor, c.Version.Patch)
                                   == (version.Major, version.Minor, version.Patch))));
    }

    /// <inheritdoc/>
    public override string ToString() => text;

    static bool Holds(string op, SemanticVersion version, SemanticVersion bound) => op switch
    {
        ">=" => version >= bound,
        ">" => version > bound,
        "<=" => version <= bound,
        "<" => version < bound,
        _ => version.CompareTo(bound) == 0,
    };

    static bool TryExpand(string token, List<(string, SemanticVersion)> comparators)
    {
        if (token is "*" or "x" or "X") return true;

        var op = token.StartsWith(">=") || token.StartsWith("<=") ? token[..2]
            : token[0] is '>' or '<' or '=' or '^' or '~' ? token[..1]
            : string.Empty;
        if (!TryParsePartial(token[op.Length..], out var version, out var given)) return false;

        switch (op)
        {
            case "^":
                comparators.Add((">=", version));
                comparators.Add(("<", version.Major > 0 || given == 1 ? new(version.Major + 1, 0, 0, "0")
                    : version.Minor > 0 || given == 2 ? new(0, version.Minor + 1, 0, "0")
                    : new(0, 0, version.Patch + 1, "0")));
                return true;
            case "~":
                comparators.Add((">=", version));
                comparators.Add(("<", given == 1 ? new(version.Major + 1, 0, 0, "0")
                    : new(version.Major, version.Minor + 1, 0, "0")));
                return true;
            case "" or "=" when given < 3:
                comparators.Add((">=", version));
                comparators.Add(("<", given == 1 ? new(version.Major + 1, 0, 0, "0")
                    : new(version.Major, version.Minor + 1, 0, "0")));
                return true;
            default:
                comparators.Add((op.Length == 0 ? "=" : op, version));
                return true;
        }
    }

    /// <summary>Reads a version that may leave out minor and patch (<c>1</c>, <c>1.2</c>, <c>1.2.x</c>).</summary>
    static bool TryParsePartial(string text, [NotNullWhen(true)] out SemanticVersion? version, out int given)
    {
        given = 3;
        if (SemanticVersion.TryParse(text, out version)) return true;

        var parts = text.Split('.');
        given = parts.TakeWhile(static p => p is not ("x" or "X" or "*")).Count();
        if (given is 0 or > 2 || parts.Length > 3) return false;

        var numbers = new int[3];
        for (var i = 0; i < given; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }
}

/// <summary>Writes a <see cref="VersionRange"/> as its text.</summary>
public sealed class VersionRangeJsonConverter : JsonConverter<VersionRange>
{
    /// <inheritdoc/>
    public override VersionRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return VersionRange.TryParse(text, out var range) ? range : throw new JsonException($"'{text}' is not a version range.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, VersionRange value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
