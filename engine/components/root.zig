pub const CameraComponent = @import("CameraComponent.zig").CameraComponent;
pub const LightComponent = @import("LightComponent.zig").LightComponent;
pub const MeshRendererComponent = @import("MeshRendererComponent.zig").MeshRendererComponent;
pub const RigidBodyComponent = @import("RigidBodyComponent.zig").RigidBodyComponent;
pub const ColliderComponent = @import("ColliderComponent.zig").ColliderComponent;
pub const AudioSourceComponent = @import("AudioSourceComponent.zig").AudioSourceComponent;
pub const AnimatorComponent = @import("AnimatorComponent.zig").AnimatorComponent;
pub const UiDocumentComponent = @import("UiDocumentComponent.zig").UiDocumentComponent;
pub const BuiltinEntry = @import("BuiltinEntry.zig").BuiltinEntry;

pub const BUILTIN_COMPONENTS = [_]BuiltinEntry{
    .{ .type_name = "CameraComponent", .display_name = "Camera" },
    .{ .type_name = "LightComponent", .display_name = "Light" },
    .{ .type_name = "MeshRendererComponent", .display_name = "Mesh Renderer" },
    .{ .type_name = "RigidBodyComponent", .display_name = "Rigid Body" },
    .{ .type_name = "ColliderComponent", .display_name = "Collider" },
    .{ .type_name = "AudioSourceComponent", .display_name = "Audio Source" },
    .{ .type_name = "AnimatorComponent", .display_name = "Animator" },
    .{ .type_name = "UiDocumentComponent", .display_name = "UI Document" },
};
