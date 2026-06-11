const std = @import("std");
const UserScriptRef = @import("UserScriptRef.zig").UserScriptRef;
const CameraComponent = @import("../components/CameraComponent.zig").CameraComponent;
const LightComponent = @import("../components/LightComponent.zig").LightComponent;
const MeshRendererComponent = @import("../components/MeshRendererComponent.zig").MeshRendererComponent;
const RigidBodyComponent = @import("../components/RigidBodyComponent.zig").RigidBodyComponent;
const ColliderComponent = @import("../components/ColliderComponent.zig").ColliderComponent;
const AudioSourceComponent = @import("../components/AudioSourceComponent.zig").AudioSourceComponent;
const AnimatorComponent = @import("../components/AnimatorComponent.zig").AnimatorComponent;

/// Tagged union over all builtin and user script component types.
pub const Component = union(enum) {
    camera: CameraComponent,
    light: LightComponent,
    mesh_renderer: MeshRendererComponent,
    rigid_body: RigidBodyComponent,
    collider: ColliderComponent,
    audio_source: AudioSourceComponent,
    animator: AnimatorComponent,
    user_script: UserScriptRef,

    /// Returns the human-readable display name for this component.
    /// Takes a pointer so that the user_script case can return a slice into
    /// the caller-owned storage rather than a dangling slice of a stack copy.
    pub fn displayName(self: *const Component) []const u8 {
        return switch (self.*) {
            .camera => "Camera",
            .light => "Light",
            .mesh_renderer => "Mesh Renderer",
            .rigid_body => "Rigid Body",
            .collider => "Collider",
            .audio_source => "Audio Source",
            .animator => "Animator",
            .user_script => self.user_script.type_name[0..self.user_script.type_name_len],
        };
    }

    /// Creates a component from its Zig type name string, or null if unknown.
    pub fn fromTypeName(name: []const u8) ?Component {
        if (std.mem.eql(u8, name, "CameraComponent")) return .{ .camera = .{} };
        if (std.mem.eql(u8, name, "LightComponent")) return .{ .light = .{} };
        if (std.mem.eql(u8, name, "MeshRendererComponent")) return .{ .mesh_renderer = .{} };
        if (std.mem.eql(u8, name, "RigidBodyComponent")) return .{ .rigid_body = .{} };
        if (std.mem.eql(u8, name, "ColliderComponent")) return .{ .collider = .{} };
        if (std.mem.eql(u8, name, "AudioSourceComponent")) return .{ .audio_source = .{} };
        if (std.mem.eql(u8, name, "AnimatorComponent")) return .{ .animator = .{} };
        return null;
    }
};
