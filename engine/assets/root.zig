pub const Mesh = @import("Mesh.zig").Mesh;
pub const Vertex = @import("Mesh.zig").Vertex;
pub const Submesh = @import("Mesh.zig").Submesh;
pub const PrimitiveMesh = @import("PrimitiveMesh.zig");
pub const Texture = @import("Texture.zig").Texture;
pub const TextureFormat = @import("Texture.zig").Format;
pub const TextureMip = @import("Texture.zig").Mip;
pub const ObjLoader = @import("ObjLoader.zig");
pub const GltfLoader = @import("GltfLoader.zig");
pub const FbxLoader = @import("FbxLoader.zig");
pub const ImageLoader = @import("ImageLoader.zig");
pub const DdsLoader = @import("DdsLoader.zig");

// ── Model info (materials/images shared by glTF/FBX loaders) ─────────────────
const model_info = @import("ModelInfo.zig");
pub const ModelInfo = model_info.ModelInfo;
pub const MaterialInfo = model_info.MaterialInfo;
pub const ImageInfo = model_info.ImageInfo;
pub const TexRef = model_info.TexRef;
pub const AlphaMode = model_info.AlphaMode;

// ── Materials & shaders ───────────────────────────────────────────────────────
pub const shader = @import("Shader.zig");
pub const ShaderDef = shader.ShaderDef;
pub const ShaderParam = shader.ShaderParam;
pub const ParamKind = shader.ParamKind;
pub const Material = @import("Material.zig").Material;

// ── UI theme ──────────────────────────────────────────────────────────────────
/// Serializable UI theme asset (`.uitheme`): colors and corner rounding only.
pub const UiTheme = @import("UiTheme.zig");
/// Built-in theme presets (Dark, Light, Dark High Contrast, Darcula, Catppuccin).
pub const ui_theme_presets = @import("ui_theme_presets.zig");

// ── Localization ──────────────────────────────────────────────────────────────
/// Translation source-of-truth asset (`.strings`), one per locale (ADR 0011).
pub const Strings = @import("Strings.zig");

// ── Input ─────────────────────────────────────────────────────────────────────
/// Data-driven input binding asset (`.inputactions`), applied to `engine.Input`.
pub const InputActions = @import("InputActions.zig").InputActions;

// ── Event channels (SOAP slice) ─────────────────────────────────────────
/// Inspector-wireable event channel: a publisher and any number of
/// subscribers reference the same asset GUID and share one live instance
/// (see `GameEventRegistry`), decoupled from each other.
pub const GameEvent = @import("GameEvent.zig").GameEvent;
pub const GameEventRegistry = @import("GameEvent.zig").GameEventRegistry;

// ── Project / game settings ─────────────────────────────────────────────────────
/// Game/project configuration asset (`.projectsettings`): metadata, graphics,
/// platform options, and the boot scene.
pub const ProjectSettings = @import("ProjectSettings.zig").ProjectSettings;

// ── Asset Provider API ──────────────────────────────────────────────────────
// Generic, swappable access to asset bytes: loose files in development, `.oap`
// packages in release builds. Compose providers with AssetServer.
pub const AssetProvider = @import("Provider.zig").Provider;
pub const AssetProviderError = @import("Provider.zig").Error;
pub const LooseFileProvider = @import("LooseFileProvider.zig");
pub const OapProvider = @import("OapProvider.zig");
pub const AssetServer = @import("AssetServer.zig");

/// Load a mesh from a file path. Supports .obj, .gltf, .glb, and .fbx formats.
pub fn loadMesh(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !Mesh {
    const ext = std.fs.path.extension(path);
    if (ascii_ieql(ext, ".obj")) return ObjLoader.load(allocator, io, path);
    if (ascii_ieql(ext, ".gltf") or ascii_ieql(ext, ".glb")) return GltfLoader.load(allocator, io, path);
    if (ascii_ieql(ext, ".fbx")) return FbxLoader.load(allocator, io, path);
    return error.UnsupportedMeshFormat;
}

/// Load a mesh from an in-memory byte buffer. Cooked canonical meshes (what the
/// importer writes to the cache, detected by magic) load directly; otherwise
/// `ext` selects a source parser (`.obj`). glTF/GLB are not parsed from memory —
/// they are cooked to the canonical format at import time.
pub fn loadMeshFromMemory(allocator: std.mem.Allocator, bytes: []const u8, ext: []const u8) !Mesh {
    if (Mesh.isCanonical(bytes)) return Mesh.fromBytes(allocator, bytes);
    if (ascii_ieql(ext, ".obj")) return ObjLoader.parse(allocator, bytes);
    return error.UnsupportedMeshFormat;
}

/// Load an image texture from a file path. Supports common formats via stb_image.
pub fn loadTexture(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !Texture {
    return ImageLoader.load(allocator, io, path);
}

/// Decode an image texture from an in-memory byte buffer (e.g. from an `.oap`
/// package). Format is detected from the bytes; no extension required.
pub fn loadTextureFromMemory(allocator: std.mem.Allocator, bytes: []const u8) !Texture {
    return ImageLoader.loadFromMemory(allocator, bytes);
}

fn ascii_ieql(a: []const u8, b: []const u8) bool {
    if (a.len != b.len) return false;
    for (a, b) |ca, cb| {
        if (std.ascii.toLower(ca) != std.ascii.toLower(cb)) return false;
    }
    return true;
}

const std = @import("std");

test {
    std.testing.refAllDecls(@This());
}
