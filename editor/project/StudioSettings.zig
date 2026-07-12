//! Typed schema for Studio-wide (not per-project) editor configuration
//! (issue #88). Bridges to the generic key/value `Settings` store
//! (`editor/Settings.zig`) so existing scattered readers keep working
//! unmodified while `studio/SettingsEditor.zig` gains a single reflected
//! object to draw via `PropDraw` (mirrors `engine`'s component `turian_hints`
//! convention, e.g. `engine/components/CameraComponent.zig`).
const std = @import("std");
const FieldHint = @import("engine").FieldHint;
const Settings = @import("Settings.zig").Settings;

/// One entry per top-level category field of `StudioSettings`, used by the
/// Settings editor's sidebar and search (title/description metadata that
/// doesn't fit naturally on the fields themselves).
pub const CategoryMeta = struct {
    /// Must match a field name of `StudioSettings`.
    field: []const u8,
    title: []const u8,
    description: []const u8,
};

pub const categories = [_]CategoryMeta{
    .{ .field = "general", .title = "General", .description = "Editor-wide display and UI behavior." },
    .{ .field = "camera", .title = "Editor Camera", .description = "Free-look viewport camera movement and feel." },
    .{ .field = "asset_browser", .title = "Asset Browser", .description = "Grid tile filename display." },
};

pub const General = struct {
    /// Show the Studio UI's own frame rate next to the play controls.
    show_editor_fps: bool = false,
    /// Max characters shown in a document tab title before truncating with an ellipsis.
    tab_title_max: i64 = 18,

    pub const turian_hints = struct {
        pub const tab_title_max = FieldHint{ .min = 6, .max = 60, .label = "Tab max length", .tooltip = "Maximum characters shown in a tab title before truncating." };
    };
};

pub const Camera = struct {
    /// World units per second (WASDQE free-look movement).
    move_speed: f32 = 4.0,
    /// Degrees per pixel of mouse movement while looking (RMB drag).
    look_sensitivity: f32 = 0.18,
    /// World units per mouse-wheel notch (dolly).
    zoom_speed: f32 = 0.6,

    pub const turian_hints = struct {
        pub const move_speed = FieldHint{ .min = 0.1, .max = 50.0, .widget = .slider_entry, .tooltip = "Free-look movement speed, in world units per second." };
        pub const look_sensitivity = FieldHint{ .min = 0.01, .max = 2.0, .widget = .slider_entry, .tooltip = "Mouse-look sensitivity, in degrees per pixel." };
        pub const zoom_speed = FieldHint{ .min = 0.1, .max = 10.0, .widget = .slider_entry, .tooltip = "Scroll-wheel dolly speed, in world units per notch." };
    };
};

pub const AssetBrowser = struct {
    /// Max characters of a filename shown in a tile label before truncating
    /// with an ellipsis (issue #84) — independent of the tile zoom slider
    /// (`studio/asset-browser/AssetBrowser.zig`'s header "Size" slider, which
    /// only affects icon/tile pixel size, not how much of the name shows).
    name_char_length: i64 = 12,
    /// Hide file extensions in tile labels, e.g. show "MyTexture" instead of
    /// "MyTexture.png".
    hide_extensions: bool = true,

    pub const turian_hints = struct {
        pub const name_char_length = FieldHint{ .min = 4, .max = 40, .widget = .slider_entry, .label = "Name Length", .tooltip = "Max characters of a filename shown in a tile before truncating with an ellipsis." };
    };
};

pub const StudioSettings = struct {
    general: General = .{},
    camera: Camera = .{},
    asset_browser: AssetBrowser = .{},

    // Key strings match the pre-existing keys each panel already reads/writes
    // directly (`MenuBar.zig`'s `FPS_SETTING_KEY`, `Documents.zig`'s
    // `TITLE_MAX_KEY`, `SceneViewport.zig`'s `CAM_*_KEY`,
    // `AssetGridView.zig`'s `HIDE_EXT_SETTING_KEY`) so this editor becomes
    // another reader/writer of the same values rather than a competing copy.
    const KEY_SHOW_FPS = "editor.show_fps";
    const KEY_TAB_TITLE_MAX = "editor.tab_title_max";
    const KEY_CAM_MOVE_SPEED = "editor.camera.move_speed";
    const KEY_CAM_LOOK_SENSITIVITY = "editor.camera.look_sensitivity";
    const KEY_CAM_ZOOM_SPEED = "editor.camera.zoom_speed";
    const KEY_NAME_CHAR_LENGTH = "asset_browser.name_char_length";
    const KEY_HIDE_EXTENSIONS = "asset_browser.hide_extensions";

    /// Populate from the on-disk/in-memory KV store, falling back to each
    /// field's default when the key is missing or malformed.
    pub fn fromSettings(s: *const Settings) StudioSettings {
        var self = StudioSettings{};
        self.general.show_editor_fps = s.getBool(KEY_SHOW_FPS, self.general.show_editor_fps);
        self.general.tab_title_max = s.getInt(KEY_TAB_TITLE_MAX, self.general.tab_title_max);
        self.camera.move_speed = @floatCast(s.getFloat(KEY_CAM_MOVE_SPEED, self.camera.move_speed));
        self.camera.look_sensitivity = @floatCast(s.getFloat(KEY_CAM_LOOK_SENSITIVITY, self.camera.look_sensitivity));
        self.camera.zoom_speed = @floatCast(s.getFloat(KEY_CAM_ZOOM_SPEED, self.camera.zoom_speed));
        self.asset_browser.name_char_length = s.getInt(KEY_NAME_CHAR_LENGTH, self.asset_browser.name_char_length);
        self.asset_browser.hide_extensions = s.getBool(KEY_HIDE_EXTENSIONS, self.asset_browser.hide_extensions);
        return self;
    }

    /// Write every field back to the KV store. Does not call `Settings.save`
    /// — callers persist to disk explicitly (see `SettingsEditor.save`).
    pub fn applyToSettings(self: *const StudioSettings, s: *Settings) !void {
        try s.setBool(KEY_SHOW_FPS, self.general.show_editor_fps);
        try s.setInt(KEY_TAB_TITLE_MAX, self.general.tab_title_max);
        try s.setFloat(KEY_CAM_MOVE_SPEED, self.camera.move_speed);
        try s.setFloat(KEY_CAM_LOOK_SENSITIVITY, self.camera.look_sensitivity);
        try s.setFloat(KEY_CAM_ZOOM_SPEED, self.camera.zoom_speed);
        try s.setInt(KEY_NAME_CHAR_LENGTH, self.asset_browser.name_char_length);
        try s.setBool(KEY_HIDE_EXTENSIONS, self.asset_browser.hide_extensions);
    }
};
