/// How an asset type is opened by the editor.
pub const OpenMode = enum {
    /// Asset opens in a built-in Turian editor panel (scene, material, animation).
    internal_editor,
    /// Asset opens via the OS default external application (scripts, shaders, text).
    external_editor,
    /// Asset has no interactive open action (compiled/binary assets).
    none,
};

/// Visual hint for the asset browser icon.
pub const IconHint = enum {
    document,
    code,
    image,
    sound,
    model,
    material,
    data,
    font,
};

/// Describes the editor behaviour and appearance for an asset type.
pub const AssetDescriptor = struct {
    /// Human-readable type name.
    name: []const u8,
    /// File extensions associated with this type.
    extensions: []const []const u8,
    /// How this asset type is opened.
    open_mode: OpenMode = .none,
    /// Icon hint for the asset browser.
    icon_hint: IconHint = .document,
};
