//! InputActions — a data-driven, reusable input binding asset.
//!
//! Modeled on Unity's Input Actions asset, in the Zig/Turian idiom: a ZON file
//! lists named actions and the device bindings that drive them. The game loads
//! every InputActions asset in the package at startup and applies it to the live
//! `engine.Input` map — so **bindings are configuration, not code**. Designers edit
//! the asset (eventually in the studio); no script changes, reusable across projects.
//!
//! File format (`.inputactions`, ZON):
//! ```zon
//! .{
//!     .version = 1,
//!     .actions = .{
//!         .{ .name = "move", .kind = .vector,
//!            .pos = .{ .{ .device = .key, .code = "d" } },   // right
//!            .neg = .{ .{ .device = .key, .code = "a" } },   // left
//!            .up  = .{ .{ .device = .key, .code = "w" } },   // forward
//!            .down = .{ .{ .device = .key, .code = "s" } } }, // back
//!         .{ .name = "look", .kind = .button,
//!            .pos = .{ .{ .device = .mouse, .code = "right" } } },
//!     },
//! }
//! ```
//! `code` strings match the `engine.Key` / `engine.MouseButton` field names exactly.

const std = @import("std");
const input_mod = @import("../Input.zig");
const Input = input_mod.Input;
const Key = input_mod.Key;
const MouseButton = input_mod.MouseButton;
const GamepadButton = input_mod.GamepadButton;
const GamepadAxis = input_mod.GamepadAxis;
const Binding = input_mod.Binding;

pub const InputActions = struct {
    pub const CURRENT_VERSION: u32 = 1;

    /// Schema version; bump to trigger migration logic.
    version: u32 = CURRENT_VERSION,
    /// The actions defined by this asset.
    actions: []const ActionDef = &.{},

    /// Physical device a binding source reads from.
    pub const Device = enum { key, mouse, gamepad_button, gamepad_axis };

    /// Semantic action category (mirrors `engine.Input.ActionKind`).
    pub const Kind = enum { button, axis, vector };

    /// One physical source bound to an action role.
    pub const Source = struct {
        device: Device = .key,
        /// Name matching the field names of `engine.Key` / `engine.MouseButton` /
        /// `engine.GamepadButton` / `engine.GamepadAxis` for the chosen `device`.
        code: []const u8 = "",
        /// For `gamepad_axis` only: which half of the axis (e.g. stick right vs left).
        axis_positive: bool = true,
    };

    /// A named action with bindings per role. button uses `pos`; axis uses
    /// `pos`/`neg`; vector uses `pos` (right) / `neg` (left) / `up` / `down`.
    pub const ActionDef = struct {
        name: []const u8 = "",
        kind: Kind = .button,
        pos: []const Source = &.{},
        neg: []const Source = &.{},
        up: []const Source = &.{},
        down: []const Source = &.{},
    };

    /// Parse an InputActions asset from ZON bytes. Caller frees via `deinit`.
    pub fn loadFromBytes(allocator: std.mem.Allocator, bytes: []const u8) !InputActions {
        const z = try allocator.dupeZ(u8, bytes);
        defer allocator.free(z);
        return std.zon.parse.fromSliceAlloc(InputActions, allocator, z, null, .{});
    }

    /// Free slices owned by an InputActions produced via `loadFromBytes`.
    pub fn deinit(self: InputActions, allocator: std.mem.Allocator) void {
        std.zon.parse.free(allocator, self);
    }

    /// Serialize this asset as ZON into `writer`.
    pub fn serialize(self: InputActions, writer: *std.Io.Writer) !void {
        try std.zon.stringify.serialize(self, .{}, writer);
    }

    /// Write this asset to `path` as a `.inputactions` ZON file.
    pub fn save(self: InputActions, io: std.Io, path: []const u8) !void {
        var buf: [1024 * 64]u8 = undefined;
        var writer = std.Io.Writer.fixed(&buf);
        try self.serialize(&writer);
        try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = writer.buffered() });
    }

    /// Register every action into a runtime `Input` map. Unknown key/button codes
    /// are skipped (forward-compatible with codes a build doesn't recognise).
    pub fn applyTo(self: InputActions, input: *Input) void {
        for (self.actions) |act| {
            switch (act.kind) {
                .button => {
                    var b: [input_mod.MAX_BINDINGS]Binding = undefined;
                    input.defineButton(act.name, b[0..resolve(act.pos, &b)]);
                },
                .axis => {
                    var p: [input_mod.MAX_BINDINGS]Binding = undefined;
                    var n: [input_mod.MAX_BINDINGS]Binding = undefined;
                    input.defineAxis(act.name, p[0..resolve(act.pos, &p)], n[0..resolve(act.neg, &n)]);
                },
                .vector => {
                    var r: [input_mod.MAX_BINDINGS]Binding = undefined;
                    var l: [input_mod.MAX_BINDINGS]Binding = undefined;
                    var u: [input_mod.MAX_BINDINGS]Binding = undefined;
                    var d: [input_mod.MAX_BINDINGS]Binding = undefined;
                    input.defineVector(
                        act.name,
                        r[0..resolve(act.pos, &r)],
                        l[0..resolve(act.neg, &l)],
                        u[0..resolve(act.up, &u)],
                        d[0..resolve(act.down, &d)],
                    );
                },
            }
        }
    }

    fn resolve(list: []const Source, out: *[input_mod.MAX_BINDINGS]Binding) usize {
        var n: usize = 0;
        for (list) |s| {
            if (n >= out.len) break;
            const b: ?Binding = switch (s.device) {
                .key => if (std.meta.stringToEnum(Key, s.code)) |k| Binding{ .key = k } else null,
                .mouse => if (std.meta.stringToEnum(MouseButton, s.code)) |m| Binding{ .mouse = m } else null,
                .gamepad_button => if (std.meta.stringToEnum(GamepadButton, s.code)) |g| Binding{ .gamepad_button = g } else null,
                .gamepad_axis => if (std.meta.stringToEnum(GamepadAxis, s.code)) |ax|
                    Binding{ .gamepad_axis = .{ .axis = ax, .positive = s.axis_positive } }
                else
                    null,
            };
            if (b) |bb| {
                out[n] = bb;
                n += 1;
            }
        }
        return n;
    }
};

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test "parse and apply a data-driven action map" {
    const a = std.testing.allocator;
    const sample =
        \\.{
        \\    .version = 1,
        \\    .actions = .{
        \\        .{ .name = "move", .kind = .vector,
        \\           .pos = .{ .{ .device = .key, .code = "d" } },
        \\           .neg = .{ .{ .device = .key, .code = "a" } },
        \\           .up  = .{ .{ .device = .key, .code = "w" } },
        \\           .down = .{ .{ .device = .key, .code = "s" } } },
        \\        .{ .name = "jump", .kind = .button,
        \\           .pos = .{ .{ .device = .key, .code = "space" }, .{ .device = .mouse, .code = "right" } } },
        \\    },
        \\}
    ;

    var ia = try InputActions.loadFromBytes(a, sample);
    defer ia.deinit(a);
    try std.testing.expectEqual(@as(usize, 2), ia.actions.len);

    var input = Input.init();
    ia.applyTo(&input);

    // Vector action from the asset.
    input.setKey(.w, true);
    input.setKey(.d, true);
    const v = input.vector("move");
    try std.testing.expectEqual(@as(f32, 1), v.x);
    try std.testing.expectEqual(@as(f32, 1), v.y);

    // Button action with a keyboard and a mouse binding, both from the asset.
    try std.testing.expect(!input.isPressed("jump"));
    input.setMouseButton(.right, true);
    try std.testing.expect(input.isPressed("jump"));
}

test "serialize then load round-trips actions" {
    const a = std.testing.allocator;
    const original = InputActions{
        .version = 1,
        .actions = &.{
            .{ .name = "move", .kind = .vector, .pos = &.{.{ .device = .key, .code = "d" }}, .neg = &.{.{ .device = .key, .code = "a" }} },
            .{ .name = "fire", .kind = .button, .pos = &.{.{ .device = .mouse, .code = "left" }} },
        },
    };

    var buf: [4096]u8 = undefined;
    var writer = std.Io.Writer.fixed(&buf);
    try original.serialize(&writer);

    var loaded = try InputActions.loadFromBytes(a, writer.buffered());
    defer loaded.deinit(a);

    try std.testing.expectEqual(@as(usize, 2), loaded.actions.len);
    try std.testing.expectEqualStrings("move", loaded.actions[0].name);
    try std.testing.expectEqual(InputActions.Kind.vector, loaded.actions[0].kind);
    try std.testing.expectEqualStrings("d", loaded.actions[0].pos[0].code);
    try std.testing.expectEqual(InputActions.Device.mouse, loaded.actions[1].pos[0].device);
}

test "unknown device codes are skipped, not fatal" {
    const a = std.testing.allocator;
    const sample =
        \\.{ .version = 1, .actions = .{
        \\    .{ .name = "fire", .kind = .button, .pos = .{ .{ .device = .key, .code = "nonsense" } } },
        \\} }
    ;
    var ia = try InputActions.loadFromBytes(a, sample);
    defer ia.deinit(a);

    var input = Input.init();
    ia.applyTo(&input); // must not crash on the bad code
    try std.testing.expect(!input.isPressed("fire"));
}
