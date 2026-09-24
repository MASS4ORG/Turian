namespace Turian.Engine.UI;

/// <summary>Direction of a data binding.</summary>
public enum BindingMode
{
    /// <summary>Source → target only, updated whenever the source changes.</summary>
    OneWay = 0,

    /// <summary>Source ↔ target; edits on the target are written back.</summary>
    TwoWay = 1,

    /// <summary>Source → target once, when the element is first built.</summary>
    OneTime = 2,
}

/// <summary>
/// A parsed binding expression from a <c>.ui</c> document — the contents of a <c>{ … }</c>:
/// a data-context path, an optional mode, and an optional named converter.
/// Examples: <c>{Player.Health}</c>, <c>{Volume, mode=TwoWay}</c>, <c>{Score | thousands}</c>,
/// <c>{Enabled, mode=OneTime | not}</c>.
/// </summary>
public sealed record BindingExpression(string Path, BindingMode Mode = BindingMode.OneWay, string? Converter = null)
{
    /// <summary>Whether <paramref name="text"/> looks like a binding expression (<c>{ … }</c>).</summary>
    /// <param name="text">The attribute value to test.</param>
    public static bool IsBinding(string? text) =>
        text is not null && text.Length >= 2 && text[0] == '{' && text[^1] == '}';

    /// <summary>
    /// Parses a <c>{ … }</c> expression. The braces are required.
    /// </summary>
    /// <param name="text">The full expression including braces.</param>
    /// <exception cref="FormatException">The text is not a well-formed binding expression.</exception>
    public static BindingExpression Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!IsBinding(text))
            throw new FormatException($"Not a binding expression: '{text}'");

        var body = text[1..^1].Trim();

        string? converter = null;
        var pipe = body.IndexOf('|', StringComparison.Ordinal);
        if (pipe >= 0)
        {
            converter = body[(pipe + 1)..].Trim();
            body = body[..pipe].Trim();
            if (converter.Length == 0) converter = null;
        }

        var mode = BindingMode.OneWay;
        var parts = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var path = parts.Length > 0 ? parts[0] : string.Empty;

        for (var i = 1; i < parts.Length; i++)
        {
            var kv = parts[i].Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && kv[0].Equals("mode", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<BindingMode>(kv[1], ignoreCase: true, out var parsed))
            {
                mode = parsed;
            }
        }

        if (path.Length == 0)
            throw new FormatException($"Binding expression has no path: '{text}'");

        return new BindingExpression(path, mode, converter);
    }
}
