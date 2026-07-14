//! In-game GUI data + logic. Zero dvui imports (D7) — the tree-walk
//! that turns this data into dvui calls lives in `subsystems/ui_render/`.

pub const UiDocument = @import("UiDocument.zig").UiDocument;
pub const UiNode = @import("UiDocument.zig").UiNode;
pub const UiComponent = @import("UiDocument.zig").UiComponent;
pub const ImageComponent = @import("UiDocument.zig").ImageComponent;
pub const TextComponent = @import("UiDocument.zig").TextComponent;
pub const LayoutComponent = @import("UiDocument.zig").LayoutComponent;
pub const ButtonComponent = @import("UiDocument.zig").ButtonComponent;
pub const LayoutItem = @import("UiDocument.zig").LayoutItem;
pub const LayoutMode = @import("UiDocument.zig").LayoutMode;
pub const Expand = @import("UiDocument.zig").Expand;
pub const ScaleMode = @import("UiDocument.zig").ScaleMode;
pub const StyleBlock = @import("UiDocument.zig").StyleBlock;
pub const StyleClass = @import("UiDocument.zig").StyleClass;
pub const TextAlign = @import("UiDocument.zig").TextAlign;
pub const EventBinding = @import("UiDocument.zig").EventBinding;
pub const Warning = @import("UiDocument.zig").Warning;
pub const WarningKind = @import("UiDocument.zig").WarningKind;

pub const UiEvents = @import("UiEvents.zig").UiEvents;
pub const EventId = @import("UiEvents.zig").EventId;
pub const INVALID_EVENT_ID = @import("UiEvents.zig").INVALID_EVENT_ID;

pub const UiInstance = @import("UiInstance.zig").UiInstance;
pub const UiRuntime = @import("UiInstance.zig").UiRuntime;

test {
    // Forces full resolution of every file re-exported above, so their
    // `test` blocks are actually discovered by `zig build test` — a type
    // only referenced by pointer elsewhere (e.g. `Frame.uiDocument`'s
    // `?*UiInstance`) can otherwise stay lazily unresolved and silently
    // drop its tests from the run.
    @import("std").testing.refAllDecls(@This());
}
