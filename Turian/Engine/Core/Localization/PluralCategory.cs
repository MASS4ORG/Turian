namespace Turian.Engine.Core;

/// <summary>CLDR cardinal plural categories used by <see cref="PluralRules"/> and message plural blocks.</summary>
public enum PluralCategory
{
    /// <summary>Used by a few locales (such as Arabic) for a count of zero.</summary>
    Zero,

    /// <summary>The singular form, used for exactly one in most locales.</summary>
    One,

    /// <summary>The dual form, used by Arabic for exactly two.</summary>
    Two,

    /// <summary>The paucal form, used by Slavic locales for a small few.</summary>
    Few,

    /// <summary>The greater plural form, used by Slavic and Arabic locales for larger counts.</summary>
    Many,

    /// <summary>The catch-all form, and the only one English and CJK locales use.</summary>
    Other,
}
