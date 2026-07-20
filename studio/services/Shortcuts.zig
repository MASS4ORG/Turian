//! Bridges `editor.shortcuts.Registry` to dvui key events: decentralized
//! registration (mirrors `studio/main-window/Panels.zig`'s `registerCustom`)
//! and two dispatch paths:
//!   - Contextual commands (rename, delete, transform mode, ...) are matched
//!     by the panel that owns the events, via `eventMatches` — inherently
//!     focus-scoped, since only the panel currently drawing checks its own.
//!   - Global commands (undo, save, play toggle, ...) register a `Handler`
//!     and are dispatched in one pass by `dispatchGlobal`, called once per
//!     frame *after* every panel has drawn — so a contextual shortcut (e.g.
//!     Ctrl+C while renaming) wins over an identically-bound global one.
const std = @import("std");
const gui = @import("gui");
const editor = @import("editor");

pub const Binding = editor.shortcuts.Binding;
pub const Stroke = editor.shortcuts.Stroke;
pub const Key = editor.shortcuts.Key;
pub const Context = editor.shortcuts.Context;
pub const CommandDesc = editor.shortcuts.CommandDesc;
pub const Handler = *const fn () void;

const HandlerEntry = struct {
    id: []const u8,
    handler: Handler,
};

var g_registry: editor.shortcuts.Registry = undefined;
var g_handlers: std.ArrayList(HandlerEntry) = .empty;
var g_inited = false;
var g_overrides_loaded = false;

fn ensureInit() void {
    if (g_inited) return;
    g_inited = true;
    g_registry = editor.shortcuts.Registry.init(std.heap.page_allocator);
}

/// Registers commands, replacing the description of any id already present
/// (an in-progress rebind or override survives) — safe to call once per
/// feature module at startup. `handlers[i]` pairs positionally with
/// `descs[i]`; pass `null` for contextual commands, which panels match
/// locally via `eventMatches` instead of a registered handler.
pub fn register(descs: []const CommandDesc, handlers: []const ?Handler) void {
    ensureInit();
    g_registry.register(descs) catch return;
    for (descs, handlers) |desc, maybe_handler| {
        const handler = maybe_handler orelse continue;
        for (g_handlers.items) |*entry| {
            if (std.mem.eql(u8, entry.id, desc.id)) {
                entry.handler = handler;
                break;
            }
        } else {
            g_handlers.append(std.heap.page_allocator, .{ .id = desc.id, .handler = handler }) catch {};
        }
    }
}

/// Loads user overrides from the settings store. No-op after the first
/// successful call, mirroring `MenuBar.zig`'s `syncFpsFromSettings` —
/// settings aren't necessarily ready yet when modules register their
/// commands at startup.
pub fn ensureOverridesLoaded(settings: *const editor.Settings) void {
    ensureInit();
    if (g_overrides_loaded) return;
    g_registry.loadOverrides(settings);
    g_overrides_loaded = true;
}

/// Persists every command's override state. Does not call `Settings.save` —
/// caller persists to disk explicitly, matching `StudioSettings.applyToSettings`.
pub fn saveOverrides(settings: *editor.Settings) !void {
    ensureInit();
    try g_registry.saveOverrides(settings);
}

/// The live registry, for `ShortcutsEditor`'s listing/search/conflict UI and
/// for rebinding. Callers must not hold the pointer across a frame boundary
/// where `register` might run (rescan hooks) — re-fetch each frame.
pub fn registry() *editor.shortcuts.Registry {
    ensureInit();
    return &g_registry;
}

/// True if this key event matches `command_id`'s effective binding: an
/// unhandled key-down whose code+modifiers equal the bound stroke. Chord
/// bindings never match here (not dispatchable yet, see `Binding.second`).
///
/// `engaged` is the caller's own "is my panel the one the user is actually
/// interacting with" — dvui focus, or hover for a non-focusable surface like
/// the 3D viewport. Only consulted when the command's `requires_focus` is set.
pub fn eventMatches(e: *const gui.Event, command_id: []const u8, engaged: bool) bool {
    if (e.handled) return false;
    if (e.evt != .key) return false;
    const ke = e.evt.key;
    if (ke.action != .down) return false;

    ensureInit();
    const entry = g_registry.find(command_id) orelse return false;
    if (entry.desc.requires_focus and !engaged) return false;

    const stroke = strokeFromEvent(ke);
    if (textInputActive() and !(stroke.ctrl or stroke.cmd)) return false;

    for (g_registry.effectiveBindings(command_id)) |b| {
        if (b.second != null) continue;
        if (b.first.eql(stroke)) return true;
    }
    return false;
}

/// True if this frame carries a `.text` event, generated only while a
/// focused text-entry widget is consuming keystrokes as content — those
/// reach it via `.text`, not `e.handle()` on the raw `.key` event, so the
/// same keystroke stays visible, unhandled, to every other panel. Used to
/// suppress modifier-less/shift-only bindings during text entry.
fn textInputActive() bool {
    for (gui.events()) |*e| {
        if (e.evt == .text) return true;
    }
    return false;
}

/// Dispatches every registered global-context command against this frame's
/// still-unhandled key-down events. Call once per frame, after every panel
/// has drawn (so contextual `eventMatches` checks in panels run — and mark
/// events handled — first).
pub fn dispatchGlobal(root_wd: *const gui.WidgetData) void {
    ensureInit();
    const suppress_bare = textInputActive();
    for (gui.events()) |*e| {
        if (e.handled) continue;
        if (e.evt != .key) continue;
        const ke = e.evt.key;
        if (ke.action != .down) continue;

        const stroke = strokeFromEvent(ke);
        if (suppress_bare and !(stroke.ctrl or stroke.cmd)) continue;
        for (g_handlers.items) |he| {
            const entry = g_registry.find(he.id) orelse continue;
            if (entry.desc.context != .global) continue;

            for (g_registry.effectiveBindings(he.id)) |b| {
                if (b.second != null) continue;
                if (!b.first.eql(stroke)) continue;
                e.handle(@src(), root_wd);
                he.handler();
                break;
            }
        }
    }
}

/// Display label for a menu item or tooltip, e.g. "Ctrl+Shift+S". Empty
/// string when unbound. Allocated from the current frame's arena.
pub fn label(command_id: []const u8) []const u8 {
    ensureInit();
    const bindings = g_registry.effectiveBindings(command_id);
    if (bindings.len == 0) return "";
    const arena = gui.currentWindow().arena();
    const text = bindings[0].formatAlloc(arena) catch return "";
    return titleCase(text);
}

/// In-place "ctrl+shift+s" -> "Ctrl+Shift+S": capitalizes the first letter
/// after the start, a '+' (modifier/key separator), or a ' ' (chord
/// separator). Digits and already-non-letter separators pass through.
pub fn titleCase(text: []u8) []const u8 {
    var cap_next = true;
    for (text) |*c| {
        if (cap_next) c.* = std.ascii.toUpper(c.*);
        cap_next = (c.* == '+' or c.* == ' ');
    }
    return text;
}

/// Converts a raw dvui key event into a `Stroke` — exposed for
/// `ShortcutsEditor`'s rebind-capture dialog, which needs the same
/// event-to-stroke conversion `eventMatches`/`dispatchGlobal` use internally.
pub fn strokeFromEvent(ke: gui.Event.Key) Stroke {
    return .{
        .key = toEditorKey(ke.code),
        .ctrl = ke.mod.control(),
        .shift = ke.mod.shift(),
        .alt = ke.mod.alt(),
        .cmd = ke.mod.command(),
    };
}

/// Field-for-field mirror of `dvui.enums.Key` — exhaustive so a future dvui
/// key addition fails this switch at compile time instead of silently
/// falling through to `.unknown`.
fn toEditorKey(k: gui.enums.Key) Key {
    return switch (k) {
        .a => .a,
        .b => .b,
        .c => .c,
        .d => .d,
        .e => .e,
        .f => .f,
        .g => .g,
        .h => .h,
        .i => .i,
        .j => .j,
        .k => .k,
        .l => .l,
        .m => .m,
        .n => .n,
        .o => .o,
        .p => .p,
        .q => .q,
        .r => .r,
        .s => .s,
        .t => .t,
        .u => .u,
        .v => .v,
        .w => .w,
        .x => .x,
        .y => .y,
        .z => .z,

        .zero => .zero,
        .one => .one,
        .two => .two,
        .three => .three,
        .four => .four,
        .five => .five,
        .six => .six,
        .seven => .seven,
        .eight => .eight,
        .nine => .nine,

        .f1 => .f1,
        .f2 => .f2,
        .f3 => .f3,
        .f4 => .f4,
        .f5 => .f5,
        .f6 => .f6,
        .f7 => .f7,
        .f8 => .f8,
        .f9 => .f9,
        .f10 => .f10,
        .f11 => .f11,
        .f12 => .f12,
        .f13 => .f13,
        .f14 => .f14,
        .f15 => .f15,
        .f16 => .f16,
        .f17 => .f17,
        .f18 => .f18,
        .f19 => .f19,
        .f20 => .f20,
        .f21 => .f21,
        .f22 => .f22,
        .f23 => .f23,
        .f24 => .f24,
        .f25 => .f25,

        .kp_divide => .kp_divide,
        .kp_multiply => .kp_multiply,
        .kp_subtract => .kp_subtract,
        .kp_add => .kp_add,
        .kp_0 => .kp_0,
        .kp_1 => .kp_1,
        .kp_2 => .kp_2,
        .kp_3 => .kp_3,
        .kp_4 => .kp_4,
        .kp_5 => .kp_5,
        .kp_6 => .kp_6,
        .kp_7 => .kp_7,
        .kp_8 => .kp_8,
        .kp_9 => .kp_9,
        .kp_decimal => .kp_decimal,
        .kp_equal => .kp_equal,
        .kp_enter => .kp_enter,

        .enter => .enter,
        .escape => .escape,
        .tab => .tab,
        .left_shift => .left_shift,
        .right_shift => .right_shift,
        .left_control => .left_control,
        .right_control => .right_control,
        .left_alt => .left_alt,
        .right_alt => .right_alt,
        .left_command => .left_command,
        .right_command => .right_command,
        .menu => .menu,
        .num_lock => .num_lock,
        .caps_lock => .caps_lock,
        .print => .print,
        .scroll_lock => .scroll_lock,
        .pause => .pause,
        .delete => .delete,
        .home => .home,
        .end => .end,
        .page_up => .page_up,
        .page_down => .page_down,
        .insert => .insert,
        .left => .left,
        .right => .right,
        .up => .up,
        .down => .down,
        .backspace => .backspace,
        .space => .space,
        .minus => .minus,
        .equal => .equal,
        .left_bracket => .left_bracket,
        .right_bracket => .right_bracket,
        .backslash => .backslash,
        .semicolon => .semicolon,
        .apostrophe => .apostrophe,
        .comma => .comma,
        .period => .period,
        .slash => .slash,
        .grave => .grave,

        .unknown => .unknown,
    };
}
