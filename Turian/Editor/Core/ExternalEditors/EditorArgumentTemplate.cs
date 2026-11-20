namespace Turian.Editor.Core;

/// <summary>
/// Expands a command-line template such as <c>--goto "{file}:{line}:{column}"</c> into separate arguments. The template
/// is split first and the tokens are filled in afterwards, so a path with spaces, quotes or non-ASCII characters
/// always stays one argument on every operating system.
/// </summary>
public static partial class EditorArgumentTemplate
{
    /// <summary>The tokens a template may use.</summary>
    public static readonly IReadOnlyList<string> Tokens = ["{projectDir}", "{solution}", "{file}", "{line}", "{column}"];

    /// <summary>
    /// Splits <paramref name="template"/> on whitespace outside double quotes, then replaces the tokens in each
    /// argument. An argument naming a token that has no value, such as <c>{file}</c> when only the project is
    /// opened, is left out together with the switch just before it (<c>--goto</c> in <c>--goto {file}</c>); the line
    /// and column fall back to 1.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <param name="context">The values for the tokens.</param>
    /// <returns>The arguments to start the program with.</returns>
    public static IReadOnlyList<string> Expand(string? template, EditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["{projectDir}"] = context.ProjectDirectory,
            ["{solution}"] = context.Solution,
            ["{file}"] = context.File,
            ["{line}"] = context.EffectiveLine.ToString(CultureInfo.InvariantCulture),
            ["{column}"] = context.EffectiveColumn.ToString(CultureInfo.InvariantCulture),
        };

        var parts = Split(template);
        var expanded = parts.Select(part => Substitute(part, values)).ToList();
        var arguments = new List<string>();
        for (var index = 0; index < parts.Count; index++)
        {
            var leadsMissingValue = IsSwitch(parts[index]) && index + 1 < expanded.Count && expanded[index + 1] is null;
            if (expanded[index] is { } argument && !leadsMissingValue)
            {
                arguments.Add(argument);
            }
        }

        return arguments;
    }

    static bool IsSwitch(string part) => part.StartsWith('-') && !part.Contains('{') && !part.Contains('=');

    /// <summary>Whether every <c>{...}</c> in the template is a known token.</summary>
    /// <param name="template">The template to check.</param>
    /// <param name="unknown">The first unknown token, or <c>null</c>.</param>
    /// <returns><c>true</c> when the template only uses known tokens.</returns>
    public static bool IsValid(string? template, out string? unknown)
    {
        unknown = TokenPattern().Matches(template ?? string.Empty).Select(static match => match.Value)
            .FirstOrDefault(static token => !Tokens.Contains(token));
        return unknown is null;
    }

    static string? Substitute(string part, Dictionary<string, string?> values)
    {
        var missing = false;
        var result = TokenPattern().Replace(part, match =>
        {
            if (!values.TryGetValue(match.Value, out var value))
            {
                return match.Value;
            }

            missing |= value is null;
            return value ?? string.Empty;
        });
        return missing ? null : result;
    }

    static List<string> Split(string? template)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var character in template ?? string.Empty)
        {
            if (character == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                Flush();
            }
            else
            {
                current.Append(character);
                started = true;
            }
        }

        Flush();
        return parts;

        void Flush()
        {
            if (started)
            {
                parts.Add(current.ToString());
            }

            current.Clear();
            started = false;
        }
    }

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex TokenPattern();
}
