namespace Turian.Engine.Core;

/// <summary>
/// Small, allocation-free CLDR cardinal plural rule set covering the locales a game is likely to
/// ship. Only the cardinal categories are modelled; ordinals are not.
/// </summary>
/// <remarks>
/// The rules are distilled from CLDR's plurals.xml rather than read from it, so a locale whose rules
/// are absent falls back to the Germanic one/other split, which is at worst an imprecise translation
/// and never a crash.
/// </remarks>
public static class PluralRules
{
    /// <summary>The CLDR cardinal category a count falls into for a locale.</summary>
    /// <param name="locale">A BCP-47 locale such as <c>pt-BR</c>, or its language such as <c>pt</c>.</param>
    /// <param name="value">The count the message formats.</param>
    /// <returns>The category whose variant the message should use.</returns>
    public static PluralCategory GetCategory(string? locale, decimal value)
    {
        var language = (locale ?? string.Empty).Split('-', '_')[0];
        var n = Math.Abs(value);
        return Rules.TryGetValue(language, out var rule) ? rule(new Operands(n)) : Germanic(new Operands(n));
    }

    static readonly Dictionary<string, Func<Operands, PluralCategory>> Rules =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // No grammatical number distinction.
            ["ja"] = NoPlural,
            ["ko"] = NoPlural,
            ["zh"] = NoPlural,
            ["th"] = NoPlural,
            ["vi"] = NoPlural,
            ["id"] = NoPlural,
            ["tr"] = NoPlural,
            ["fr"] = French,
            ["ru"] = EastSlavic,
            ["uk"] = EastSlavic,
            ["be"] = EastSlavic,
            ["pl"] = Polish,
            ["cs"] = WestSlavic,
            ["sk"] = WestSlavic,
            ["ar"] = Arabic,
            ["he"] = Hebrew,
        };

    readonly record struct Operands(decimal N)
    {
        public long Integer => (long)N;
        public bool IsInteger => N == Integer;
        public long Mod10 => Integer % 10;
        public long Mod100 => Integer % 100;
        public bool IsTeen => Mod100 is >= 12 and <= 14;
    }

    // English, Portuguese, Spanish, German, Italian, Dutch and most others.
    static PluralCategory Germanic(Operands o) => o.N == 1 ? PluralCategory.One : PluralCategory.Other;

    static PluralCategory NoPlural(Operands _) => PluralCategory.Other;

    // 0 is singular in French.
    static PluralCategory French(Operands o) => o.N is 0 or 1 ? PluralCategory.One : PluralCategory.Other;

    static PluralCategory EastSlavic(Operands o)
    {
        if (!o.IsInteger) return PluralCategory.Other;
        if (o.Mod10 == 1 && o.Mod100 != 11) return PluralCategory.One;
        return o.Mod10 is >= 2 and <= 4 && !o.IsTeen ? PluralCategory.Few : PluralCategory.Many;
    }

    static PluralCategory Polish(Operands o)
    {
        if (!o.IsInteger) return PluralCategory.Other;
        if (o.Integer == 1) return PluralCategory.One;
        return o.Mod10 is >= 2 and <= 4 && !o.IsTeen ? PluralCategory.Few : PluralCategory.Many;
    }

    static PluralCategory WestSlavic(Operands o) => o switch
    {
        { IsInteger: true, Integer: 1 } => PluralCategory.One,
        { IsInteger: true, Integer: >= 2 and <= 4 } => PluralCategory.Few,
        _ => PluralCategory.Other,
    };

    static PluralCategory Arabic(Operands o) => o.N switch
    {
        0 => PluralCategory.Zero,
        1 => PluralCategory.One,
        2 => PluralCategory.Two,
        _ when o is { IsInteger: true, Mod100: >= 3 and <= 10 } => PluralCategory.Few,
        _ when o is { IsInteger: true, Mod100: >= 11 and <= 99 } => PluralCategory.Many,
        _ => PluralCategory.Other,
    };

    static PluralCategory Hebrew(Operands o) => o switch
    {
        { IsInteger: true, Integer: 1 } => PluralCategory.One,
        { IsInteger: true, Integer: 2 } => PluralCategory.Two,
        _ => PluralCategory.Other,
    };
}
