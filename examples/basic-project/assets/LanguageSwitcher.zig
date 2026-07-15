const std = @import("std");
const engine = @import("engine");

const pt_br_strtab = @embedFile("i18n/pt-BR.strtab");

/// #36 demo: registers `engine.Locale` as a service and switches the active
/// language at runtime (press L) with no scene reload. `HealthHud`-style
/// direct UI instance write — text is re-fetched from the table every frame,
/// so the label updates the instant the locale switches.
var g_locale: engine.Locale = engine.Locale.init("en");
var g_loaded = false;

pub const LanguageSwitcher = struct {
    pub const is_component = true;

    /// The scene node carrying the `ui_document` component to update.
    hud: engine.GameObjectRef = .{},
    _label: ?usize = null,
    _buf: [256]u8 = undefined,

    pub fn awake(self: *@This(), frame: engine.Frame) void {
        _ = self;
        if (!g_loaded) {
            g_loaded = true;
            g_locale.loadTable(pt_br_strtab) catch {};
        }
        frame.services.register(engine.Locale, &g_locale);
    }

    fn resolveHud(self: *@This(), frame: engine.Frame) ?*engine.ui.UiInstance {
        const guid = self.hud.slice();
        if (guid.len == 0) return null;
        for (frame.objects) |*obj| {
            if (!std.mem.eql(u8, obj.guidSlice(), guid)) continue;
            return frame.uiDocument(obj);
        }
        return null;
    }

    pub fn update(self: *@This(), frame: engine.Frame) void {
        const inst = self.resolveHud(frame) orelse return;
        if (self._label == null) self._label = inst.find("LanguageLabel");
        const label = self._label orelse return;

        if (frame.input.wasKeyPressed(.l)) {
            const next = if (std.mem.eql(u8, g_locale.active_locale.slice(), "en")) "pt-BR" else "en";
            g_locale.setLocale(next);
        }

        var fba = std.heap.FixedBufferAllocator.init(&self._buf);
        const text = g_locale.tr(fba.allocator(), "Press L to switch language", &.{}) catch "Press L to switch language";
        inst.setText(label, text);
    }
};
