const engine = @import("engine");
const SceneUserScript = @import("SceneUserScript.zig").SceneUserScript;
const SceneMeshRenderer = @import("SceneMeshRenderer.zig").SceneMeshRenderer;
const SceneUiDocument = @import("SceneUiDocument.zig").SceneUiDocument;

/// Serialisable scene component (matches engine.Component layout for JSON persistence).
pub const SceneComponent = union(enum) {
    camera: engine.CameraComponent,
    light: engine.LightComponent,
    mesh_renderer: SceneMeshRenderer,
    rigid_body: engine.RigidBodyComponent,
    collider: engine.ColliderComponent,
    audio_source: engine.AudioSourceComponent,
    animator: engine.AnimatorComponent,
    ui_document: SceneUiDocument,
    environment: engine.EnvironmentComponent,
    post_process_volume: engine.PostProcessVolumeComponent,
    reflection_probe: engine.ReflectionProbeComponent,
    user_script: SceneUserScript,
};
