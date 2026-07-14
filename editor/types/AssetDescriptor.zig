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
    theme,
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
    /// Cascaded Create-menu path, e.g. `"UI/Document"`.
    /// Null for types that aren't creatable from the asset browser's Create
    /// menu (imported types like Image/Model/Script/Audio/Font). Declared
    /// here, next to the rest of the type's editor behaviour, rather than in
    /// a separate list — the same "attribute lives with the type" idea
    /// `menu_path` gives user components (`Scanner.MENU_PATH_MARKER`), just
    /// expressed as a table field since builtin types have no source file
    /// for the scanner to read a marker from.
    create_menu_path: ?[]const u8 = null,
};
