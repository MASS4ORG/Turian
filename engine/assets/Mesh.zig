const std = @import("std");

/// A single vertex with position, normal, and UV data.
pub const Vertex = extern struct {
    px: f32,
    py: f32,
    pz: f32,
    nx: f32,
    ny: f32,
    nz: f32,
    u: f32,
    v: f32,
};

/// Mesh loaded from OBJ/GLTF. Owned by the caller; call deinit() to free.
pub const Mesh = struct {
    /// Vertex array.
    vertices: []Vertex,
    /// Triangle index array.
    indices: []u32,
    /// Allocator used for vertices and indices.
    allocator: std.mem.Allocator,

    /// Axis-aligned bounding box minimum corner.
    min: [3]f32 = .{ 0, 0, 0 },
    /// Axis-aligned bounding box maximum corner.
    max: [3]f32 = .{ 0, 0, 0 },

    /// Frees the vertex and index arrays.
    pub fn deinit(self: *@This()) void {
        self.allocator.free(self.vertices);
        self.allocator.free(self.indices);
    }

    /// Computes the axis-aligned bounding box from vertex positions.
    pub fn computeBounds(self: *@This()) void {
        if (self.vertices.len == 0) return;
        var mn = [3]f32{ self.vertices[0].px, self.vertices[0].py, self.vertices[0].pz };
        var mx = mn;
        for (self.vertices[1..]) |v| {
            mn[0] = @min(mn[0], v.px);
            mn[1] = @min(mn[1], v.py);
            mn[2] = @min(mn[2], v.pz);
            mx[0] = @max(mx[0], v.px);
            mx[1] = @max(mx[1], v.py);
            mx[2] = @max(mx[2], v.pz);
        }
        self.min = mn;
        self.max = mx;
    }
};
