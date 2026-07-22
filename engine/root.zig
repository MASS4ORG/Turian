const std = @import("std");

/// Engine display name.
pub const name = "Turian Engine";
/// Engine semantic version.
pub const version = std.SemanticVersion{ .major = 0, .minor = 1, .patch = 0 };

/// Standalone math library — Vector2/3/4, Matrix4, Quaternion, integer vectors.
pub const math = @import("math");
/// 2-component float vector.
pub const Vector2 = math.Vector2;
/// 3-component float vector.
pub const Vector3 = math.Vector3;
/// 4-component float vector.
pub const Vector4 = math.Vector4;
/// 2-component integer vector.
pub const Vector2i = math.Vector2i;
/// 3-component integer vector.
pub const Vector3i = math.Vector3i;
/// 4-component integer vector.
pub const Vector4i = math.Vector4i;
/// Unit quaternion representing a 3D rotation.
pub const Quaternion = math.Quaternion;
/// 4x4 float matrix.
pub const Matrix4 = math.Matrix4;

/// Describes UI widget preferences for a component field.
pub const FieldHint = @import("core/FieldHint.zig").FieldHint;
/// Frame timing data (delta, elapsed, frame count).
pub const Time = @import("core/Time.zig").Time;
/// Project metadata (name, version).
pub const Project = @import("core/Project.zig").Project;

/// Device-agnostic input snapshot + semantic action map.
pub const Input = @import("Input.zig").Input;
/// Keyboard key identifier.
pub const Key = @import("Input.zig").Key;
/// Mouse button identifier.
pub const MouseButton = @import("Input.zig").MouseButton;
/// Gamepad button identifier.
pub const GamepadButton = @import("Input.zig").GamepadButton;
/// Gamepad analog axis identifier.
pub const GamepadAxis = @import("Input.zig").GamepadAxis;
/// A physical input source bound to an action.
pub const Binding = @import("Input.zig").Binding;
/// Per-update context object bundling engine services for scripts (ADR 0001).
pub const Frame = @import("Frame.zig").Frame;
/// Immediate-mode debug/editor draw API: lines, boxes, spheres, labels.
pub const Gizmos = @import("Gizmos.zig").Gizmos;
/// Type-keyed registry for engine + user-defined services (ADR 0001).
pub const Services = @import("Services.zig").Services;
/// Cross-cutting application control (quit, ...) reachable from scripts and
/// UI button handlers alike via `frame.service(engine.Application)`.
pub const Application = @import("Application.zig").Application;
/// In-engine performance profiler: scoped CPU zones, per-thread timeline, and
/// render counters for the Studio panel and built-game overlay.
pub const Profiler = @import("Profiler.zig");
/// Diagnostic log ring buffer: captures recent std.log warn/err for the Remote
/// Debug Protocol's `errors` method / MCP `list_errors` tool.
pub const DiagLog = @import("DiagLog.zig");

/// Localization (i18n): `tr`/`trc`/`trn`/`key`, string tables, CLDR plurals,
/// ICU-subset message formatting (ADR 0011).
pub const i18n = @import("i18n/root.zig");
/// Localization service — see `engine.i18n` for the message/table types.
pub const Locale = i18n.Locale;

/// Asset loading subsystem (meshes, textures).
pub const assets = @import("assets/root.zig");
/// Triangle mesh type.
pub const Mesh = assets.Mesh;
/// Procedural built-in meshes (cube/sphere) used to preview materials without a
/// model asset.
pub const PrimitiveMesh = assets.PrimitiveMesh;
/// RGBA8 image type.
pub const Texture = assets.Texture;
/// Material asset — shader parameter values and resource bindings.
pub const Material = assets.Material;
/// Data-driven input binding asset (`.inputactions`).
pub const InputActions = assets.InputActions;
/// Inspector-wireable event channel — see `engine.ui.EventBinding.channel`.
pub const GameEvent = assets.GameEvent;
pub const GameEventRegistry = assets.GameEventRegistry;
/// Game/project configuration asset (`.projectsettings`): metadata, graphics,
/// platform options, and the boot scene.
pub const ProjectSettings = assets.ProjectSettings;
/// Shader metadata (exposed parameters) driving materials and inspector UI.
pub const shader = assets.shader;
/// Shader parameter metadata descriptor.
pub const ShaderDef = assets.ShaderDef;
/// Serializable UI theme asset (`.uitheme`): colors and corner rounding only.
pub const UiTheme = assets.UiTheme;
/// Translation source-of-truth asset (`.strings`), one per locale (ADR 0011).
pub const Strings = assets.Strings;
/// Built-in theme presets (Dark, Light, Dark High Contrast, Darcula, Catppuccin).
pub const ui_theme_presets = assets.ui_theme_presets;

/// Generic, swappable asset access interface (loose files vs `.oap` packages).
pub const AssetProvider = assets.AssetProvider;
/// Reads loose files from a directory tree (development builds).
pub const LooseFileProvider = assets.LooseFileProvider;
/// Reads assets from an `.oap` package (release builds, DLC, patches, mods).
pub const OapProvider = assets.OapProvider;
/// Mounts a prioritised stack of providers; later mounts override earlier ones.
pub const AssetServer = assets.AssetServer;
/// Software rasterizer for game builds.
pub const software_renderer = @import("SoftwareRenderer.zig");

/// In-game GUI data + logic: `.uidoc` document model, event registry.
/// Zero dvui imports — see `subsystems/ui_render/` for the draw walk.
pub const ui = @import("ui/root.zig");
/// UI document asset (`.uidoc`) — a flat, parent-indexed tree of `UiNode`s.
pub const UiDocument = ui.UiDocument;
/// Typed UI event registry (strings at rest, handles at runtime).
pub const UiEvents = ui.UiEvents;

/// World->viewport projection (C5/C8): screen-anchored UI elements (enemy
/// health bars, name plates) computed from a script without the `gpu`/
/// `render` modules.
pub const Projection = @import("Projection.zig");

/// C-ABI types for user script reflection.
pub const api = @import("api/root.zig");

/// Runtime introspection layer: structured, JSON-serialisable views
/// of live engine state, plus queries and mutation. The single source of truth
/// for the editor, CLI, Remote Debug Protocol, and MCP server.
pub const introspect = @import("introspect/root.zig");
/// Engine-wide runtime metrics (FPS, memory, draw calls, ECS counts).
pub const Metrics = introspect.Metrics;

/// Built-in component types.
pub const components = @import("components/root.zig");
/// Camera component type.
pub const CameraComponent = components.CameraComponent;
/// Light component type.
pub const LightComponent = components.LightComponent;
/// Mesh renderer component type.
pub const MeshRendererComponent = components.MeshRendererComponent;
/// Rigid body component type.
pub const RigidBodyComponent = components.RigidBodyComponent;
/// Collider component type.
pub const ColliderComponent = components.ColliderComponent;
/// Audio source component type.
pub const AudioSourceComponent = components.AudioSourceComponent;
/// Animator component type.
pub const AnimatorComponent = components.AnimatorComponent;
/// UI document component type — instantiates a `.uidoc` asset into the scene.
pub const UiDocumentComponent = components.UiDocumentComponent;
/// Image-based environment lighting (skybox + ambient) component type.
pub const EnvironmentComponent = components.EnvironmentComponent;
/// List of builtin component metadata entries.
pub const BUILTIN_COMPONENTS = components.BUILTIN_COMPONENTS;

/// Scene transform (position, rotation, scale).
pub const Transform = @import("scene/Transform.zig").Transform;
/// Serialisable field value for user script components.
pub const ScriptFieldValue = @import("scene/ScriptFieldValue.zig").ScriptFieldValue;
/// Reference to a user-defined script component.
pub const UserScriptRef = @import("scene/UserScriptRef.zig").UserScriptRef;
/// Tagged union over all component types.
pub const Component = @import("scene/Component.zig").Component;
/// A scene node with transform and component list.
pub const SceneNode = @import("scene/SceneNode.zig").SceneNode;

/// Formal scene-management API: async/additive/persistent scene load & unload,
/// lifecycle events, and active-scene tracking.
pub const SceneManager = @import("scene/SceneManager.zig").SceneManager;
/// Runtime prefab spawner: deferred Instantiate/Destroy.
pub const Spawner = @import("scene/Spawner.zig").Spawner;
pub const SceneHandle = @import("scene/SceneManager.zig").SceneHandle;
pub const SceneLoadMode = @import("scene/SceneManager.zig").LoadMode;
pub const SceneEvent = @import("scene/SceneManager.zig").Event;
pub const SceneLoader = @import("scene/SceneManager.zig").Loader;
/// Maximum number of concurrently loaded scenes the SceneManager supports.
pub const SCENE_MANAGER_MAX_SCENES = @import("scene/SceneManager.zig").MAX_SCENES;

/// Scene-wide constants.
pub const scene = struct {
    /// Default/initial scene node storage capacity — not a hard ceiling; see
    /// `GROWTH_CEILING` and `SceneNode.zig`'s doc comment.
    pub const MAX_OBJECTS = @import("scene/SceneNode.zig").MAX_OBJECTS;
    /// Hard ceiling on scene node count regardless of growth.
    pub const GROWTH_CEILING = @import("scene/SceneNode.zig").GROWTH_CEILING;
    /// Maximum number of components per scene node.
    pub const MAX_COMPONENTS = @import("scene/SceneNode.zig").MAX_COMPONENTS;
    /// Maximum length of a scene node name.
    pub const NAME_MAX = @import("scene/SceneNode.zig").NAME_MAX;
    /// Prefab override group.
    pub const OverrideGroup = @import("scene/SceneNode.zig").OverrideGroup;
};

/// Weak reference to a scene node (by name/path).
pub const GameObjectRef = api.GameObjectRef;
/// Weak reference to a component (by name/path).
pub const ComponentRef = api.ComponentRef;
/// Weak reference to an asset (by path).
pub const AssetRef = api.AssetRef;
/// Asset type filter for drag-drop.
pub const AssetFilter = api.AssetFilter;
/// AssetRef with a compile-time asset type filter.
pub const TypedAssetRef = api.TypedAssetRef;

test {
    // Force every re-exported module to be analysed so their `test` blocks are
    // collected by the test runner. Without this, `addTest` on the engine module
    // discovers zero tests (the root file has no direct tests; everything is
    // reached only through `pub const X = @import(...)`).
    @import("std").testing.refAllDecls(@This());
}
