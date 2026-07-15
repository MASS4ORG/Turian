//! BCP-47 locale tag parsing, canonical casing, and fallback chains
//! (`pt-BR` -> `pt` -> default). Pure and allocation-free: every tag is
//! stored in a fixed-capacity buffer, so this can run before any allocator
//! exists (boot-time OS locale detection) and inside `comptime`.

const std = @import("std");

/// Longest tag this module stores, e.g. "zh-Hans-CN" (10 bytes). Generous
/// enough for language-script-region; extension/private-use subtags beyond
/// this are truncated rather than rejected, since a truncated-but-valid
/// language subtag is a better fallback than a hard error on a locale string
/// the OS handed us at boot.
pub const MAX_LEN = 32;

/// A parsed, canonically-cased BCP-47 tag stored inline.
pub const LocaleId = struct {
    buf: [MAX_LEN]u8 = undefined,
    len: usize = 0,

    /// Parse and canonicalize `tag`: language lowercase, script titlecase,
    /// region uppercase (alpha-2) or left as-is (numeric-3). Unrecognized
    /// subtag shapes are lowercased and kept verbatim (variants, extensions).
    pub fn parse(tag: []const u8) LocaleId {
        var self = LocaleId{};
        var out_len: usize = 0;
        var subtag_index: usize = 0;
        var it = std.mem.splitScalar(u8, tag, '-');
        while (it.next()) |subtag| {
            if (subtag.len == 0) continue;
            if (out_len + subtag.len + 1 > MAX_LEN) break;
            if (out_len > 0) {
                self.buf[out_len] = '-';
                out_len += 1;
            }
            const start = out_len;
            for (subtag) |c| {
                self.buf[out_len] = c;
                out_len += 1;
            }
            const seg = self.buf[start..out_len];
            if (subtag_index == 0) {
                toLower(seg);
            } else if (seg.len == 4 and isAlpha(seg)) {
                toTitle(seg);
            } else if (seg.len == 2 and isAlpha(seg)) {
                toUpper(seg);
            } else {
                toLower(seg);
            }
            subtag_index += 1;
        }
        self.len = out_len;
        return self;
    }

    pub fn slice(self: *const LocaleId) []const u8 {
        return self.buf[0..self.len];
    }

    /// The primary language subtag ("pt-BR" -> "pt").
    pub fn language(self: *const LocaleId) []const u8 {
        const s = self.slice();
        const dash = std.mem.indexOfScalar(u8, s, '-') orelse return s;
        return s[0..dash];
    }

    pub fn eql(self: *const LocaleId, other: *const LocaleId) bool {
        return std.mem.eql(u8, self.slice(), other.slice());
    }
};

fn isAlpha(s: []const u8) bool {
    for (s) |c| if (!std.ascii.isAlphabetic(c)) return false;
    return true;
}

fn toLower(s: []u8) void {
    for (s) |*c| c.* = std.ascii.toLower(c.*);
}

fn toUpper(s: []u8) void {
    for (s) |*c| c.* = std.ascii.toUpper(c.*);
}

fn toTitle(s: []u8) void {
    if (s.len == 0) return;
    s[0] = std.ascii.toUpper(s[0]);
    for (s[1..]) |*c| c.* = std.ascii.toLower(c.*);
}

/// Maximum number of tags in a fallback chain: requested, language-only,
/// default.
pub const MAX_CHAIN = 3;

/// The ordered list of tags to try when resolving a string: the exact
/// requested tag, its bare language (if different), then the project's
/// default locale (if not already present). Deduplicated and allocation-free.
pub const FallbackChain = struct {
    tags: [MAX_CHAIN]LocaleId = undefined,
    count: usize = 0,

    pub fn get(self: *const FallbackChain, i: usize) []const u8 {
        return self.tags[i].slice();
    }
};

/// Build the fallback chain for `requested` given the project's
/// `default_locale`. `requested == default_locale` collapses to a
/// single-entry chain.
pub fn buildFallbackChain(requested: []const u8, default_locale: []const u8) FallbackChain {
    var chain = FallbackChain{};

    const full = LocaleId.parse(requested);
    chain.tags[0] = full;
    chain.count = 1;

    const lang_str = full.language();
    if (!std.mem.eql(u8, lang_str, full.slice())) {
        chain.tags[chain.count] = LocaleId.parse(lang_str);
        chain.count += 1;
    }

    const default = LocaleId.parse(default_locale);
    var already_present = false;
    for (chain.tags[0..chain.count]) |*t| {
        if (t.eql(&default)) {
            already_present = true;
            break;
        }
    }
    if (!already_present) {
        chain.tags[chain.count] = default;
        chain.count += 1;
    }

    return chain;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test "parse normalizes casing" {
    try std.testing.expectEqualStrings("pt-BR", LocaleId.parse("pt-br").slice());
    try std.testing.expectEqualStrings("zh-Hans-CN", LocaleId.parse("ZH-hans-cn").slice());
    try std.testing.expectEqualStrings("en", LocaleId.parse("EN").slice());
}

test "language extracts primary subtag" {
    const id = LocaleId.parse("pt-BR");
    try std.testing.expectEqualStrings("pt", id.language());
    const plain = LocaleId.parse("en");
    try std.testing.expectEqualStrings("en", plain.language());
}

test "fallback chain: region falls back to language falls back to default" {
    const chain = buildFallbackChain("pt-BR", "en");
    try std.testing.expectEqual(@as(usize, 3), chain.count);
    try std.testing.expectEqualStrings("pt-BR", chain.get(0));
    try std.testing.expectEqualStrings("pt", chain.get(1));
    try std.testing.expectEqualStrings("en", chain.get(2));
}

test "fallback chain: language-only requested with no region" {
    const chain = buildFallbackChain("fr", "en");
    try std.testing.expectEqual(@as(usize, 2), chain.count);
    try std.testing.expectEqualStrings("fr", chain.get(0));
    try std.testing.expectEqualStrings("en", chain.get(1));
}

test "fallback chain: requesting the default collapses to one entry" {
    const chain = buildFallbackChain("en", "en");
    try std.testing.expectEqual(@as(usize, 1), chain.count);
    try std.testing.expectEqualStrings("en", chain.get(0));
}

test "fallback chain: language equals default, region still tried first" {
    const chain = buildFallbackChain("en-GB", "en");
    try std.testing.expectEqual(@as(usize, 2), chain.count);
    try std.testing.expectEqualStrings("en-GB", chain.get(0));
    try std.testing.expectEqualStrings("en", chain.get(1));
}
