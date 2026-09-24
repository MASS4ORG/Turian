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
        var language = (locale ?? string.Empty).Split('-', '_')[0].ToUpperInvariant();
        var n = Math.Abs(value);
        var integer = (long)n;
        var isInteger = n == integer;
        var mod10 = integer % 10;
        var mod100 = integer % 100;

        return language switch
        {
            // No grammatical number distinction.
            "JA" or "KO" or "ZH" or "TH" or "VI" or "ID" or "TR" => PluralCategory.Other,

            // One for 0 and 1 (0 is singular in French).
            "FR" => n is 0 or 1 ? PluralCategory.One : PluralCategory.Other,

            "RU" or "UK" or "BE" => isInteger && mod10 == 1 && mod100 != 11
                ? PluralCategory.One
                : isInteger && mod10 is >= 2 and <= 4 && (mod100 < 12 || mod100 > 14)
                    ? PluralCategory.Few
                    : isInteger && (mod10 == 0 || mod10 is >= 5 and <= 9 || mod100 is >= 11 and <= 14)
                        ? PluralCategory.Many
                        : PluralCategory.Other,

            "PL" => isInteger && integer == 1
                ? PluralCategory.One
                : isInteger && mod10 is >= 2 and <= 4 && (mod100 < 12 || mod100 > 14)
                    ? PluralCategory.Few
                    : isInteger
                        ? PluralCategory.Many
                        : PluralCategory.Other,

            "CS" or "SK" => isInteger && integer == 1
                ? PluralCategory.One
                : isInteger && integer is >= 2 and <= 4
                    ? PluralCategory.Few
                    : PluralCategory.Other,

            "AR" => n == 0 ? PluralCategory.Zero
                : n == 1 ? PluralCategory.One
                : n == 2 ? PluralCategory.Two
                : isInteger && mod100 is >= 3 and <= 10 ? PluralCategory.Few
                : isInteger && mod100 is >= 11 and <= 99 ? PluralCategory.Many
                : PluralCategory.Other,

            "HE" => isInteger && integer == 1
                ? PluralCategory.One
                : isInteger && integer == 2 ? PluralCategory.Two : PluralCategory.Other,

            // English, Portuguese, Spanish, German, Italian, Dutch and most others.
            _ => n == 1 ? PluralCategory.One : PluralCategory.Other,
        };
    }
}
