//! CLDR cardinal plural rules, restricted to non-negative integer counts
//! (the `n: u64` that `Locale.trn` and `{n, plural, ...}` operate on — no
//! fractional-count rules, since the engine never pluralizes on a decimal).
//!
//! Each language's rule is a direct transcription of the "integer" branch of
//! its CLDR `cardinal` rule set (the `i`/`v`/`f`/`t` operands collapse to
//! `i = n, v = 0` when the count has no decimal part). Rules are grouped by
//! CLDR rule-set family — most of the world's languages share one of a
//! handful of shapes — so adding a language is picking the matching family,
//! not deriving a new formula.

const std = @import("std");

/// CLDR plural category. Not every language uses every category; unused
/// ones for a given language are simply never returned by `category`.
pub const PluralCategory = enum { zero, one, two, few, many, other };

/// Resolve the CLDR cardinal category for `n` in `language` (a bare
/// primary-language subtag, e.g. `"pt"`, lowercase — see `locale_id.language()`).
/// Unrecognized languages fall back to the universal two-form rule
/// (`one` for `n == 1`, `other` otherwise), which is correct for the large
/// majority of languages not listed explicitly below.
pub fn category(language: []const u8, n: u64) PluralCategory {
    // Family: no plural distinction at all (East/Southeast Asian and a few
    // others: ja, zh, ko, th, vi, id, ms, my, lo, ...).
    if (isOneOf(language, &.{ "ja", "zh", "ko", "th", "vi", "id", "ms", "my", "lo" })) {
        return .other;
    }

    // Family: "one" is i=0 or i=1 (French/Portuguese/Brazilian-Portuguese/
    // Armenian/Filipino-style; for pure integers this is n==0 or n==1).
    if (isOneOf(language, &.{ "fr", "pt", "hy", "fil", "tl" })) {
        return if (n <= 1) .one else .other;
    }

    // Family: strict two-form, "one" only for exactly n==1 (English/German/
    // Dutch/Swedish/Italian/Spanish/Greek/Finnish/Hungarian/Danish/
    // Norwegian/Turkish/Hebrew/... — the default CLDR "one: i = 1 and
    // v = 0" shape).
    if (isOneOf(language, &.{ "en", "de", "nl", "sv", "it", "es", "el", "fi", "hu", "da", "no", "nb", "nn", "tr", "he" })) {
        return if (n == 1) .one else .other;
    }

    if (std.mem.eql(u8, language, "ru") or std.mem.eql(u8, language, "uk") or std.mem.eql(u8, language, "sr") or std.mem.eql(u8, language, "hr") or std.mem.eql(u8, language, "bs")) {
        return slavicEastCategory(n);
    }

    if (std.mem.eql(u8, language, "pl")) return polishCategory(n);

    if (std.mem.eql(u8, language, "cs") or std.mem.eql(u8, language, "sk")) {
        return if (n == 1) .one else if (n >= 2 and n <= 4) .few else .other;
    }

    if (std.mem.eql(u8, language, "lt")) return lithuanianCategory(n);

    if (std.mem.eql(u8, language, "ar")) return arabicCategory(n);

    // Universal default: two-form, one for n==1.
    return if (n == 1) .one else .other;
}

fn isOneOf(language: []const u8, options: []const []const u8) bool {
    for (options) |o| if (std.mem.eql(u8, language, o)) return true;
    return false;
}

/// Russian/Ukrainian/Serbian/Croatian/Bosnian: CLDR "west/east Slavic" family.
fn slavicEastCategory(n: u64) PluralCategory {
    const mod10 = n % 10;
    const mod100 = n % 100;
    if (mod10 == 1 and mod100 != 11) return .one;
    if (mod10 >= 2 and mod10 <= 4 and !(mod100 >= 12 and mod100 <= 14)) return .few;
    if (mod10 == 0 or (mod10 >= 5 and mod10 <= 9) or (mod100 >= 11 and mod100 <= 14)) return .many;
    return .other;
}

fn polishCategory(n: u64) PluralCategory {
    const mod10 = n % 10;
    const mod100 = n % 100;
    if (n == 1) return .one;
    if (mod10 >= 2 and mod10 <= 4 and !(mod100 >= 12 and mod100 <= 14)) return .few;
    if ((mod10 <= 1) or (mod10 >= 5 and mod10 <= 9) or (mod100 >= 12 and mod100 <= 14)) return .many;
    return .other;
}

fn lithuanianCategory(n: u64) PluralCategory {
    const mod10 = n % 10;
    const mod100 = n % 100;
    const in_11_19 = mod100 >= 11 and mod100 <= 19;
    if (mod10 == 1 and !in_11_19) return .one;
    if (mod10 >= 2 and mod10 <= 9 and !in_11_19) return .few;
    return .other;
}

fn arabicCategory(n: u64) PluralCategory {
    if (n == 0) return .zero;
    if (n == 1) return .one;
    if (n == 2) return .two;
    const mod100 = n % 100;
    if (mod100 >= 3 and mod100 <= 10) return .few;
    if (mod100 >= 11 and mod100 <= 99) return .many;
    return .other;
}

// ---------------------------------------------------------------------------
// Tests — CLDR sample-data conformance for the languages the plan names.
// ---------------------------------------------------------------------------

const expectEqual = std.testing.expectEqual;

test "en: one/other" {
    try expectEqual(PluralCategory.other, category("en", 0));
    try expectEqual(PluralCategory.one, category("en", 1));
    try expectEqual(PluralCategory.other, category("en", 2));
    try expectEqual(PluralCategory.other, category("en", 100));
}

test "pt: one covers 0 and 1" {
    try expectEqual(PluralCategory.one, category("pt", 0));
    try expectEqual(PluralCategory.one, category("pt", 1));
    try expectEqual(PluralCategory.other, category("pt", 2));
}

test "fr: one covers 0 and 1" {
    try expectEqual(PluralCategory.one, category("fr", 0));
    try expectEqual(PluralCategory.one, category("fr", 1));
    try expectEqual(PluralCategory.other, category("fr", 2));
}

test "ru: one/few/many" {
    try expectEqual(PluralCategory.one, category("ru", 1));
    try expectEqual(PluralCategory.few, category("ru", 2));
    try expectEqual(PluralCategory.few, category("ru", 3));
    try expectEqual(PluralCategory.many, category("ru", 5));
    try expectEqual(PluralCategory.many, category("ru", 11));
    try expectEqual(PluralCategory.one, category("ru", 21));
    try expectEqual(PluralCategory.few, category("ru", 22));
    try expectEqual(PluralCategory.many, category("ru", 25));
    try expectEqual(PluralCategory.many, category("ru", 100));
    try expectEqual(PluralCategory.one, category("ru", 101));
}

test "pl: one/few/many" {
    try expectEqual(PluralCategory.one, category("pl", 1));
    try expectEqual(PluralCategory.few, category("pl", 2));
    try expectEqual(PluralCategory.many, category("pl", 5));
    try expectEqual(PluralCategory.many, category("pl", 12));
    try expectEqual(PluralCategory.few, category("pl", 22));
    try expectEqual(PluralCategory.many, category("pl", 25));
}

test "ar: zero/one/two/few/many/other" {
    try expectEqual(PluralCategory.zero, category("ar", 0));
    try expectEqual(PluralCategory.one, category("ar", 1));
    try expectEqual(PluralCategory.two, category("ar", 2));
    try expectEqual(PluralCategory.few, category("ar", 3));
    try expectEqual(PluralCategory.few, category("ar", 10));
    try expectEqual(PluralCategory.many, category("ar", 11));
    try expectEqual(PluralCategory.many, category("ar", 99));
    try expectEqual(PluralCategory.other, category("ar", 100));
    try expectEqual(PluralCategory.other, category("ar", 102));
}

test "ja/zh: always other" {
    try expectEqual(PluralCategory.other, category("ja", 0));
    try expectEqual(PluralCategory.other, category("ja", 1));
    try expectEqual(PluralCategory.other, category("ja", 2));
    try expectEqual(PluralCategory.other, category("zh", 1));
}

test "cs: one/few/other" {
    try expectEqual(PluralCategory.one, category("cs", 1));
    try expectEqual(PluralCategory.few, category("cs", 2));
    try expectEqual(PluralCategory.few, category("cs", 4));
    try expectEqual(PluralCategory.other, category("cs", 5));
    try expectEqual(PluralCategory.other, category("cs", 0));
}

test "lt: one/few/other" {
    try expectEqual(PluralCategory.one, category("lt", 1));
    try expectEqual(PluralCategory.one, category("lt", 21));
    try expectEqual(PluralCategory.few, category("lt", 2));
    try expectEqual(PluralCategory.few, category("lt", 9));
    try expectEqual(PluralCategory.other, category("lt", 11));
    try expectEqual(PluralCategory.other, category("lt", 19));
    try expectEqual(PluralCategory.other, category("lt", 10));
}

test "unrecognized language falls back to universal one/other" {
    try expectEqual(PluralCategory.one, category("xx", 1));
    try expectEqual(PluralCategory.other, category("xx", 2));
    try expectEqual(PluralCategory.other, category("xx", 0));
}
