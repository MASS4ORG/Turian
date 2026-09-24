namespace Turian.Engine.Core;

/// <summary>
/// A locale-tagged collection of stable localization keys. One table is one <c>.strings</c> asset,
/// holding the entries for exactly one locale.
/// </summary>
/// <remarks>
/// Keys are matched case-sensitively on purpose: they are authored identifiers such as
/// <c>menu.file</c>, and folding case would make two keys that differ only in case collide.
/// </remarks>
public sealed class StringTable
{
    readonly Dictionary<string, StringTableEntry> entries = new(StringComparer.Ordinal);

    /// <summary>Creates an empty table for a locale.</summary>
    /// <param name="locale">A BCP-47 locale such as <c>pt-BR</c>.</param>
    public StringTable(string locale)
    {
        Locale = string.IsNullOrWhiteSpace(locale)
            ? throw new ArgumentException("A locale is required.", nameof(locale))
            : locale;
    }

    /// <summary>The locale this table holds strings for.</summary>
    public string Locale { get; }

    /// <summary>Every entry, in insertion order.</summary>
    public IReadOnlyCollection<StringTableEntry> Entries => entries.Values;

    /// <summary>Adds an entry, replacing any entry with the same key.</summary>
    /// <param name="entry">The entry to add.</param>
    public void Add(StringTableEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Key))
            throw new ArgumentException("A localization entry key is required.", nameof(entry));

        entries[entry.Key] = entry;
    }

    /// <summary>Looks up an entry by its exact key.</summary>
    /// <param name="key">The localization key.</param>
    /// <param name="entry">The entry, when found.</param>
    /// <returns><c>true</c> when the table holds the key.</returns>
    public bool TryGet(string key, out StringTableEntry entry) => entries.TryGetValue(key, out entry!);

    /// <summary>
    /// Resolves a key to its translated (or source) string, without formatting plural blocks. The
    /// count is used only to pick a plural variant; formatting is <see cref="LocaleService"/>'s job.
    /// </summary>
    /// <param name="key">The localization key.</param>
    /// <param name="count">The count a plural message selects its variant from, or <c>null</c>.</param>
    /// <returns>The raw value, or <c>null</c> when the key is absent.</returns>
    public string? Get(string key, decimal? count = null) =>
        entries.TryGetValue(key, out var entry) ? entry.Resolve(Locale, count) : null;
}

/// <summary>
/// One key of a <see cref="StringTable"/>: the English <see cref="Source"/> it was authored from, an
/// optional <see cref="Translation"/>, optional per-category <see cref="Plurals"/>, a translator
/// <see cref="Note"/> and a translation <see cref="State"/>.
/// </summary>
public sealed class StringTableEntry
{
    /// <summary>The stable identifier, e.g. <c>menu.file</c> or the English source itself.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The English text the key was authored from — also the fallback for a missing translation.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The translated text, or <c>null</c> when the entry is untranslated.</summary>
    public string? Translation { get; set; }

    /// <summary>Per-category plural variants, keyed by <see cref="PluralCategory"/>.</summary>
    public Dictionary<PluralCategory, string> Plurals { get; set; } = [];

    /// <summary>Context shown to translators, never to players.</summary>
    public string? Note { get; set; }

    /// <summary>A translation-workflow state such as <c>new</c>, <c>translated</c> or <c>fuzzy</c>.</summary>
    public string State { get; set; } = "new";

    /// <summary>
    /// The value this entry contributes for a locale and count: a matching plural variant when one
    /// exists, otherwise the translation, otherwise the English source.
    /// </summary>
    /// <param name="locale">The locale the plural rules are applied for.</param>
    /// <param name="count">The count selecting a plural variant, or <c>null</c>.</param>
    /// <returns>The raw value.</returns>
    public string Resolve(string locale, decimal? count)
    {
        if (count is not null && Plurals.TryGetValue(PluralRules.GetCategory(locale, count.Value), out var plural))
            return plural;

        return string.IsNullOrEmpty(Translation) ? Source : Translation;
    }
}
