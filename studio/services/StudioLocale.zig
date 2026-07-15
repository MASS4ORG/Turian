//! Studio-wide localization singleton — the text counterpart to
//! `ActiveTheme.zig`: one place owning the active `engine.i18n.Locale` so
//! the ~350 dvui call sites across Studio's panels can localize without
//! threading a context struct through every draw function (dvui itself
//! follows the same current-window-singleton shape via `gui.currentWindow()`,
//! which every `tr`/`trc`/`trn` call below relies on for its per-frame
//! scratch allocator — call these only after `win.begin()`, i.e. from inside
//! panel draw functions, never at Studio boot).
//!
//! Studio is not a project: its catalogs are `@embedFile`d rather than going
//! through the AssetDatabase (see `docs/plans/localization.md` T3.3), so
//! there is no load-time I/O to fail — only `pt-BR` ships today.
//!
//! Source-keyed only (`tr`/`trc`/`trn`): Studio's own UI has no designer
//! content, so `Locale.key` has no Studio-side counterpart here.
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");

/// One selectable Studio display language.
pub const Language = struct {
    /// BCP-47 tag, also the key used with `engine.i18n.StringTable`.
    tag: []const u8,
    /// Shown in the Settings language dropdown, in the language itself.
    display_name: []const u8,
};

pub const available_languages = [_]Language{
    .{ .tag = "en", .display_name = "English" },
    .{ .tag = "pt-BR", .display_name = "Português (Brasil)" },
};

const pt_br_strtab = @embedFile("../i18n/pt-BR.strtab");

var locale: engine.i18n.Locale = engine.i18n.Locale.init("en");
var loaded = false;

/// Load Studio's embedded catalogs. Idempotent; call once during boot
/// (`Main.zig`), before or after `win.begin()` — this doesn't touch
/// `gui.currentWindow()`.
pub fn init() void {
    if (loaded) return;
    loaded = true;
    locale.loadTable(pt_br_strtab) catch |err| {
        std.log.scoped(.studio_locale).warn("failed to load pt-BR catalog: {t}", .{err});
    };
}

/// Switch Studio's active display language (e.g. "pt-BR"). Takes effect
/// immediately — the next frame's `tr()` calls re-resolve from the new
/// table (immediate-mode UI, no refresh event needed).
pub fn setLanguage(tag: []const u8) void {
    locale.setLocale(tag);
}

/// The active language tag.
pub fn language() []const u8 {
    return locale.active_locale.slice();
}

/// Source-keyed UI text in the active language, formatted into the current
/// frame's dvui arena. Falls back to `msg` itself on any error (missing
/// arena/table never happens once `win.begin()` has run, but this is always
/// safe to call regardless).
pub fn tr(comptime msg: []const u8) []const u8 {
    return trArgs(msg, &.{});
}

pub fn trArgs(comptime msg: []const u8, args: []const engine.i18n.Arg) []const u8 {
    return locale.tr(gui.currentWindow().arena(), msg, args) catch msg;
}

/// Context-disambiguated UI text (two identical English strings meaning
/// different things — see `engine.i18n.Locale.trc`).
pub fn trc(comptime ctx: []const u8, comptime msg: []const u8, args: []const engine.i18n.Arg) []const u8 {
    return locale.trc(gui.currentWindow().arena(), ctx, msg, args) catch msg;
}

/// Pluralized UI text — `one`/`other` are the English forms, `n` the count.
pub fn trn(comptime one: []const u8, comptime other: []const u8, n: u64, args: []const engine.i18n.Arg) []const u8 {
    const fallback = if (n == 1) one else other;
    return locale.trn(gui.currentWindow().arena(), one, other, n, args) catch fallback;
}
