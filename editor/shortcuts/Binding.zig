//! A key binding: one physical key plus modifiers, optionally followed by a
//! second stroke to form a chord (e.g. "ctrl+k ctrl+s"). Bindings are stored
//! and compared in normalized form so modifier order in user-typed text
//! ("shift+ctrl+s" vs "ctrl+shift+s") never affects matching or persistence.
//!
//! `Key` mirrors `dvui.enums.Key` field-for-field so `studio/services/Shortcuts.zig`
//! can convert between the two with a single switch — this module stays free
//! of GUI dependencies per the engine/editor/studio split.
const std = @import("std");

pub const Key = enum {
    a,
    b,
    c,
    d,
    e,
    f,
    g,
    h,
    i,
    j,
    k,
    l,
    m,
    n,
    o,
    p,
    q,
    r,
    s,
    t,
    u,
    v,
    w,
    x,
    y,
    z,

    zero,
    one,
    two,
    three,
    four,
    five,
    six,
    seven,
    eight,
    nine,

    f1,
    f2,
    f3,
    f4,
    f5,
    f6,
    f7,
    f8,
    f9,
    f10,
    f11,
    f12,
    f13,
    f14,
    f15,
    f16,
    f17,
    f18,
    f19,
    f20,
    f21,
    f22,
    f23,
    f24,
    f25,

    kp_divide,
    kp_multiply,
    kp_subtract,
    kp_add,
    kp_0,
    kp_1,
    kp_2,
    kp_3,
    kp_4,
    kp_5,
    kp_6,
    kp_7,
    kp_8,
    kp_9,
    kp_decimal,
    kp_equal,
    kp_enter,

    enter,
    escape,
    tab,
    left_shift,
    right_shift,
    left_control,
    right_control,
    left_alt,
    right_alt,
    left_command,
    right_command,
    menu,
    num_lock,
    caps_lock,
    print,
    scroll_lock,
    pause,
    delete,
    home,
    end,
    page_up,
    page_down,
    insert,
    left,
    right,
    up,
    down,
    backspace,
    space,
    minus,
    equal,
    left_bracket,
    right_bracket,
    backslash,
    semicolon,
    apostrophe,
    comma,
    period,
    slash,
    grave,

    unknown,
};

/// One key press with its modifier state, compared structurally (not by
/// source text) so modifier order never affects equality.
pub const Stroke = struct {
    key: Key,
    ctrl: bool = false,
    shift: bool = false,
    alt: bool = false,
    cmd: bool = false,

    pub fn eql(a: Stroke, b: Stroke) bool {
        return a.key == b.key and a.ctrl == b.ctrl and a.shift == b.shift and
            a.alt == b.alt and a.cmd == b.cmd;
    }
};

pub const Binding = struct {
    first: Stroke,
    /// Set for a two-stroke chord ("ctrl+k ctrl+s"). Dispatch doesn't match
    /// chords yet; the data model and persisted format already support them.
    second: ?Stroke = null,

    pub fn single(stroke: Stroke) Binding {
        return .{ .first = stroke };
    }

    pub fn eql(a: Binding, b: Binding) bool {
        if (!a.first.eql(b.first)) return false;
        if ((a.second == null) != (b.second == null)) return false;
        if (a.second) |as| if (b.second.?.eql(as) == false) return false;
        return true;
    }

    pub const ParseError = error{Invalid};

    /// Parses "ctrl+shift+s" or a two-token chord "ctrl+k ctrl+s".
    /// Modifier order and case don't matter. Fails on empty input, an
    /// unrecognized key or modifier name, or more than two chord strokes.
    pub fn parse(text: []const u8) ParseError!Binding {
        var strokes: [2]?Stroke = .{ null, null };
        var n: usize = 0;
        var it = std.mem.tokenizeScalar(u8, text, ' ');
        while (it.next()) |token| {
            if (n >= strokes.len) return error.Invalid;
            strokes[n] = try parseStroke(token);
            n += 1;
        }
        if (n == 0) return error.Invalid;
        return .{ .first = strokes[0].?, .second = strokes[1] };
    }

    fn parseStroke(token: []const u8) ParseError!Stroke {
        var parts: [8][]const u8 = undefined;
        var n: usize = 0;
        var it = std.mem.tokenizeScalar(u8, token, '+');
        while (it.next()) |part| {
            if (n >= parts.len) return error.Invalid;
            parts[n] = part;
            n += 1;
        }
        if (n == 0) return error.Invalid;

        var stroke = Stroke{ .key = keyFromName(parts[n - 1]) orelse return error.Invalid };
        for (parts[0 .. n - 1]) |mod| {
            if (eqlIgnoreCase(mod, "ctrl") or eqlIgnoreCase(mod, "control")) {
                stroke.ctrl = true;
            } else if (eqlIgnoreCase(mod, "shift")) {
                stroke.shift = true;
            } else if (eqlIgnoreCase(mod, "alt")) {
                stroke.alt = true;
            } else if (eqlIgnoreCase(mod, "cmd") or eqlIgnoreCase(mod, "command") or
                eqlIgnoreCase(mod, "super") or eqlIgnoreCase(mod, "meta"))
            {
                stroke.cmd = true;
            } else {
                return error.Invalid;
            }
        }
        return stroke;
    }

    fn keyFromName(name: []const u8) ?Key {
        var buf: [16]u8 = undefined;
        if (name.len == 0 or name.len > buf.len) return null;
        const lower = std.ascii.lowerString(buf[0..name.len], name);
        if (lower.len == 1 and lower[0] >= '0' and lower[0] <= '9') {
            return switch (lower[0]) {
                '0' => .zero,
                '1' => .one,
                '2' => .two,
                '3' => .three,
                '4' => .four,
                '5' => .five,
                '6' => .six,
                '7' => .seven,
                '8' => .eight,
                '9' => .nine,
                else => unreachable,
            };
        }
        return std.meta.stringToEnum(Key, lower);
    }

    /// Canonical text form, modifier order fixed (ctrl, alt, shift, cmd) so
    /// output is stable regardless of how the binding was parsed or built.
    /// The inverse of `parse` — round-trips for any value `parse` can produce.
    pub fn format(self: Binding, writer: *std.Io.Writer) std.Io.Writer.Error!void {
        try formatStroke(self.first, writer);
        if (self.second) |second| {
            try writer.writeByte(' ');
            try formatStroke(second, writer);
        }
    }

    fn formatStroke(stroke: Stroke, writer: *std.Io.Writer) std.Io.Writer.Error!void {
        var wrote = false;
        if (stroke.ctrl) {
            try writer.writeAll("ctrl");
            wrote = true;
        }
        if (stroke.alt) {
            if (wrote) try writer.writeByte('+');
            try writer.writeAll("alt");
            wrote = true;
        }
        if (stroke.shift) {
            if (wrote) try writer.writeByte('+');
            try writer.writeAll("shift");
            wrote = true;
        }
        if (stroke.cmd) {
            if (wrote) try writer.writeByte('+');
            try writer.writeAll("cmd");
            wrote = true;
        }
        if (wrote) try writer.writeByte('+');
        try writer.writeAll(@tagName(stroke.key));
    }

    /// Allocating convenience wrapper over `format`, for display labels and
    /// persisted settings values.
    pub fn formatAlloc(self: Binding, allocator: std.mem.Allocator) std.mem.Allocator.Error![]u8 {
        var buf: std.Io.Writer.Allocating = .init(allocator);
        defer buf.deinit();
        self.format(&buf.writer) catch |err| switch (err) {
            error.WriteFailed => return error.OutOfMemory,
        };
        return buf.toOwnedSlice();
    }
};

fn eqlIgnoreCase(a: []const u8, b: []const u8) bool {
    return std.ascii.eqlIgnoreCase(a, b);
}

test "parse and format round-trip" {
    const b = try Binding.parse("ctrl+shift+s");
    try std.testing.expect(b.first.ctrl);
    try std.testing.expect(b.first.shift);
    try std.testing.expectEqual(Key.s, b.first.key);
    try std.testing.expect(b.second == null);

    const text = try b.formatAlloc(std.testing.allocator);
    defer std.testing.allocator.free(text);
    try std.testing.expectEqualStrings("ctrl+shift+s", text);
}

test "modifier order does not affect parsed value" {
    const a = try Binding.parse("ctrl+shift+s");
    const b = try Binding.parse("shift+ctrl+s");
    try std.testing.expect(a.eql(b));
}

test "case insensitive" {
    const a = try Binding.parse("Ctrl+S");
    const b = try Binding.parse("ctrl+s");
    try std.testing.expect(a.eql(b));
}

test "single key with no modifiers" {
    const b = try Binding.parse("f2");
    try std.testing.expectEqual(Key.f2, b.first.key);
    try std.testing.expect(!b.first.ctrl and !b.first.shift and !b.first.alt and !b.first.cmd);
}

test "digit key aliases" {
    const b = try Binding.parse("ctrl+1");
    try std.testing.expectEqual(Key.one, b.first.key);
}

test "chord parses two strokes" {
    const b = try Binding.parse("ctrl+k ctrl+s");
    try std.testing.expectEqual(Key.k, b.first.key);
    try std.testing.expect(b.first.ctrl);
    try std.testing.expect(b.second != null);
    try std.testing.expectEqual(Key.s, b.second.?.key);
    try std.testing.expect(b.second.?.ctrl);

    const text = try b.formatAlloc(std.testing.allocator);
    defer std.testing.allocator.free(text);
    try std.testing.expectEqualStrings("ctrl+k ctrl+s", text);
}

test "invalid inputs are rejected" {
    try std.testing.expectError(error.Invalid, Binding.parse(""));
    try std.testing.expectError(error.Invalid, Binding.parse("   "));
    try std.testing.expectError(error.Invalid, Binding.parse("ctrl+"));
    try std.testing.expectError(error.Invalid, Binding.parse("ctrl+notakey"));
    try std.testing.expectError(error.Invalid, Binding.parse("frobnicate+s"));
    try std.testing.expectError(error.Invalid, Binding.parse("ctrl+k ctrl+s ctrl+x"));
}

test "eql ignores chord absence mismatch" {
    const a = try Binding.parse("ctrl+s");
    const b = try Binding.parse("ctrl+k ctrl+s");
    try std.testing.expect(!a.eql(b));
}

test {
    std.testing.refAllDecls(@This());
}
