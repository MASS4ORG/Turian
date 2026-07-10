/// Describes UI hints for a component field (range, widget type, visibility, grouping).
pub const FieldHint = struct {
    /// UI widget type to use for this field.
    pub const Widget = enum { default, slider, slider_entry };

    /// Minimum value (for numeric fields).
    min: ?f64 = null,
    /// Maximum value (for numeric fields).
    max: ?f64 = null,
    /// Step / drag interval (for numeric fields). Null means continuous.
    step: ?f64 = null,
    /// Preferred widget type.
    widget: Widget = .default,
    /// When true, hide this field from the inspector entirely.
    hidden: bool = false,
    /// When true, render the field but prevent editing.
    read_only: bool = false,
    /// When true, render a multi-line text editor instead of a single-line entry.
    multiline: bool = false,
    /// When true, treat a Vector4 / [4]f32 field as an RGBA color (shows color swatch).
    is_color: bool = false,
    /// Tooltip shown on hover. Null means no tooltip.
    tooltip: ?[]const u8 = null,
    /// Optional group label. Fields sharing the same group are drawn under one expander.
    group: ?[]const u8 = null,
    /// Explicit display label. Overrides the auto-generated one (the field
    /// name title-cased — see `studio.PropDraw.displayLabel`).
    label: ?[]const u8 = null,
};
