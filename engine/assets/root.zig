pub const Mesh = @import("Mesh.zig").Mesh;
pub const Vertex = @import("Mesh.zig").Vertex;
pub const Texture = @import("Texture.zig").Texture;
pub const ObjLoader = @import("ObjLoader.zig");
pub const GltfLoader = @import("GltfLoader.zig");
pub const ImageLoader = @import("ImageLoader.zig");

// ── Materials & shaders ───────────────────────────────────────────────────────
pub const shader = @import("Shader.zig");
pub const ShaderDef = shader.ShaderDef;
pub const ShaderParam = shader.ShaderParam;
pub const ParamKind = shader.ParamKind;
pub const Material = @import("Material.zig").Material;

// ── Input ─────────────────────────────────────────────────────────────────────
/// Data-driven input binding asset (`.inputactions`), applied to `engine.Input`.
pub const InputActions = @import("InputActions.zig").InputActions;

// ── Asset Provider API ──────────────────────────────────────────────────────
// Generic, swappable access to asset bytes: loose files in development, `.oap`
// packages in release builds. Compose providers with AssetServer.
pub const AssetProvider = @import("Provider.zig").Provider;
pub const AssetProviderError = @import("Provider.zig").Error;
pub const LooseFileProvider = @import("LooseFileProvider.zig");
pub const OapProvider = @import("OapProvider.zig");
pub const AssetServer = @import("AssetServer.zig");

/// Load a mesh from a file path. Supports .obj, .gltf, and .glb formats.
pub fn loadMesh(allocator: std.mem.Allocator, io: std.Io, path: []const u8) !Mesh {
    const ext = std.fs.path.extension(path);
    if (ascii_ieql(ext, ".obj")) return ObjLoader.load(allocator, io, path);
    if (ascii_ieql(ext, ".gltf") or ascii_ieql(ext, ".glb")) return GltfLoader.load(allocator, io, path);
    return error.UnsupportedMeshFormat;
}

/// Load a mesh from an in-memory byte buffer, dispatching on `ext` (e.g. ".obj").
/// Used to load assets supplied by an `AssetProvider` / `.oap` package instead
/// of from a file path. glTF-from-memory is not yet supported.
pub fn loadMeshFromMemory(allocator: std.mem.Allocator, bytes: []const u8, ext: []const u8) !Mesh {
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
