//! Localization (i18n) runtime — ADR 0011. Ships in every game; the asset
//! pipeline (`engine/assets/Strings.zig`) and editor tooling
//! (`editor/i18n/`) build on top of this.

/// BCP-47 tag parsing, canonical casing, and locale -> language -> default
/// fallback chains.
pub const locale_id = @import("locale_id.zig");
pub const LocaleId = locale_id.LocaleId;
pub const FallbackChain = locale_id.FallbackChain;

/// CLDR cardinal plural category resolution.
pub const plurals = @import("plurals.zig");
pub const PluralCategory = plurals.PluralCategory;

/// ICU MessageFormat (subset) parser + formatter.
pub const message = @import("message.zig");
pub const Arg = message.Arg;
pub const Value = message.Value;

/// Compiled `.strtab` binary format reader/writer.
pub const StringTableMod = @import("StringTable.zig");
pub const StringTable = StringTableMod.StringTable;
pub const Unit = StringTableMod.Unit;

/// The localization service: `tr`/`trc`/`trn`/`key`, active locale, fallback
/// chain, generation counter.
pub const Locale = @import("Locale.zig");

test {
    @import("std").testing.refAllDecls(@This());
}
