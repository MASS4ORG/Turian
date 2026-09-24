namespace Turian.Engine.UI;

/// <summary>Transforms a bound value on its way to (and optionally back from) the UI.</summary>
public interface IValueConverter
{
    /// <summary>Source → target.</summary>
    /// <param name="value">The source value.</param>
    object? Convert(object? value);

    /// <summary>Target → source, for two-way bindings. Defaults to identity.</summary>
    /// <param name="value">The value from the UI control.</param>
    object? ConvertBack(object? value) => value;
}

/// <summary>Registry of named <see cref="IValueConverter"/>s referenced from <c>{Path | name}</c>.</summary>
public static class ValueConverters
{
    static readonly Dictionary<string, IValueConverter> map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["not"] = new NotConverter(),
            ["percent"] = new PercentConverter(),
            ["thousands"] = new ThousandsConverter(),
            ["upper"] = new CaseConverter(upper: true),
            ["lower"] = new CaseConverter(upper: false),
        };

    /// <summary>Registers (or replaces) a converter under <paramref name="name"/>.</summary>
    /// <param name="name">The name used in binding expressions.</param>
    /// <param name="converter">The converter.</param>
    public static void Register(string name, IValueConverter converter) => map[name] = converter;

    /// <summary>Gets a converter by name, or <c>null</c>.</summary>
    /// <param name="name">The converter name.</param>
    public static IValueConverter? Get(string? name) =>
        name is not null && map.TryGetValue(name, out var c) ? c : null;

    sealed class NotConverter : IValueConverter
    {
        public object? Convert(object? value) => value is bool b ? !b : value;
        public object? ConvertBack(object? value) => value is bool b ? !b : value;
    }

    sealed class PercentConverter : IValueConverter
    {
        public object? Convert(object? value)
        {
            if (value is not IConvertible c) return value;
            var d = c.ToDouble(CultureInfo.InvariantCulture);
            if (d is >= -1.0 and <= 1.0) d *= 100.0;
            return d.ToString("0", CultureInfo.InvariantCulture) + "%";
        }
    }

    sealed class ThousandsConverter : IValueConverter
    {
        public object? Convert(object? value) =>
            value is IConvertible c
                ? c.ToInt64(CultureInfo.InvariantCulture).ToString("N0", CultureInfo.InvariantCulture)
                : value;
    }

    sealed class CaseConverter(bool upper) : IValueConverter
    {
        public object? Convert(object? value)
        {
            if (value?.ToString() is not { } s) return value;
#pragma warning disable CA1308 // a "lower" converter must lower-case
            return upper ? s.ToUpperInvariant() : s.ToLowerInvariant();
#pragma warning restore CA1308
        }
    }
}
