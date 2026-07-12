//! Inspector panel for `.material` assets.
//!
//! The UI is generated entirely from the material's shader metadata: each
//! parameter the shader exposes draws the widget matching its kind (scalar
//! slider, colour picker, vector row, or texture slot). This is what lets a
//! future custom shader (e.g. a shader-node-builder output) get a working
//! inspector for free — add parameters to its `ShaderDef` and they appear here.
//!
//! Editing mutates an in-memory copy held in fixed buffers (parallel to the
//! shader's parameter list); "Save" writes it back to the `.material` file and
//! reimports so the cached artifact stays in sync.
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const render = @import("render");
const EditorState = @import("../../services/EditorState.zig");
const PropDraw = @import("../PropDraw.zig");
const Preview3D = @import("../../asset-browser/preview/Preview3D.zig");
const PreviewSystem = @import("../../asset-browser/preview/PreviewSystem.zig");

const shader = engine.shader;
const Material = engine.Material;

const MAX_VALS = Material.MAX_PARAMS;
const GUID_LEN = 36;

/// Reserved GUID this editor renders the live in-memory material under (see
/// `render.setMaterialOverride`) — distinct from the built-in preset GUIDs
/// (…0100-0104) and primitive-mesh GUIDs (…0200-0201).
const preview_material_guid = "00000000-0000-4000-8000-000000000210";
const PREVIEW_SIZE: u32 = 220;

/// One parameter's editable value. Only the field matching the parameter's kind
/// is meaningful; they are stored together to keep one array parallel to the
/// shader's parameter list.
const ParamValue = struct {
    scalar: f32 = 0,
    vec: [4]f32 = .{ 0, 0, 0, 1 },
    tex: [GUID_LEN]u8 = .{0} ** GUID_LEN,
    tex_len: usize = 0,

    fn texSlice(self: *const ParamValue) []const u8 {
        return self.tex[0..self.tex_len];
    }
    fn setTex(self: *ParamValue, s: []const u8) void {
        const l = @min(s.len, GUID_LEN);
        @memcpy(self.tex[0..l], s[0..l]);
        self.tex_len = l;
    }
};

// ── Loaded-material state (persists across frames) ─────────────────────────────

var loaded_path_buf: [1024]u8 = undefined;
var loaded_path_len: usize = 0;

var sh: shader.ShaderDef = undefined;
var vals: [MAX_VALS]ParamValue = undefined;
var val_count: usize = 0;
var render_state: Material.RenderState = .{};
var dirty: bool = false;

// ── Live interactive preview (mesh swap + orbit) ────────────────────────────
var preview_panel: Preview3D.Panel = .{};
var preview_shape: enum { sphere, cube } = .sphere;

fn loadedPath() []const u8 {
    return loaded_path_buf[0..loaded_path_len];
}

/// Draw the material editor for the material at `asset_path`. Loads (or reloads)
/// the file when the selection changes.
pub fn draw(asset_path: []const u8) void {
    if (!std.mem.eql(u8, asset_path, loadedPath())) load(asset_path);

    {
        var info = gui.box(@src(), .{}, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 } });
        defer info.deinit();
        gui.label(@src(), "Shader:  {s}", .{sh.name}, .{ .gravity_y = 0.5 });
    }

    _ = gui.separator(@src(), .{ .expand = .horizontal });

    // One widget per shader parameter, in declaration order.
    for (sh.params[0..val_count], 0..) |param, i| {
        switch (param.kind) {
            .scalar => drawScalar(param, &vals[i], i),
            .color => drawColor(param, &vals[i], i),
            .vec2, .vec3, .vec4 => drawVector(param, &vals[i], i),
            .texture => drawTexture(param, &vals[i], i),
        }
    }

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 7001 });
    drawRenderState();

    _ = gui.separator(@src(), .{ .expand = .horizontal, .id_extra = 7002 });
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .all(6) });
        defer row.deinit();

        if (dirty) {
            gui.label(@src(), "Unsaved changes", .{}, .{ .gravity_y = 0.5, .expand = .horizontal });
        } else {
            gui.label(@src(), "Saved", .{}, .{ .gravity_y = 0.5, .expand = .horizontal });
        }

        if (gui.button(@src(), "Save", .{}, .{ .gravity_y = 0.5, .style = if (dirty) .highlight else .control })) {
            save();
        }
    }
}

// ── Live interactive preview ────────────────────────────────────────────────
// Renders the in-memory (possibly unsaved) material onto a swappable stand-in
// mesh — embedded cube or sphere primitives, no model asset required — under
// an orbital camera the user can drag/scroll. Unity-style "swap preview mesh
// + orbit" for materials specifically, per the issue's ask; static thumbnails
// elsewhere (Asset Browser grid, other asset types) always use the sphere at a
// fixed angle — see `PreviewSystem`. Registered as `.material`'s
// `PreviewSystem.LiveDrawFn` (matches its `(asset_path, guid)` signature; both
// unused here — the shape swap needs only the in-memory edits already loaded
// into this file's own module state by `draw()`, which always runs first in
// the same frame).

pub fn drawPreview(asset_path: []const u8, guid: []const u8) void {
    _ = asset_path;
    _ = guid;
    {
        var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 } });
        defer row.deinit();
        if (gui.button(@src(), "Sphere", .{}, .{ .gravity_y = 0.5, .style = if (preview_shape == .sphere) .highlight else .control })) {
            preview_shape = .sphere;
        }
        if (gui.button(@src(), "Cube", .{}, .{ .gravity_y = 0.5, .id_extra = 1, .style = if (preview_shape == .cube) .highlight else .control })) {
            preview_shape = .cube;
        }
    }

    var mat_buf: [1024 * 8]u8 = undefined;
    const mat_bytes = serializeLive(&mat_buf);
    render.setMaterialOverride(preview_material_guid, mat_bytes);
    defer render.clearMaterialOverride();

    // Both primitives are framed to the same bounding radius (see
    // `Preview3D.primitive_frame_radius`), so swapping shape never needs a
    // re-frame — `ensureFramed`'s key only needs to change on asset switch.
    preview_panel.ensureFramed(loadedPath(), .{}, Preview3D.primitive_frame_radius);

    const mesh_guid = if (preview_shape == .sphere) engine.PrimitiveMesh.sphere_guid else engine.PrimitiveMesh.cube_guid;
    const lights = Preview3D.keyFillLights();
    const nodes = [_]engine.SceneNode{ lights[0], lights[1], Preview3D.meshNode(mesh_guid, preview_material_guid) };
    preview_panel.draw(&nodes, PREVIEW_SIZE);
}

/// Serialize the current in-memory (possibly unsaved) edits into `buf` and
/// return the written slice. Mirrors `save()`'s construction but writes to a
/// caller buffer instead of a file — the backing param arrays only need to
/// outlive the `serialize` call itself, so they stay function-local.
fn serializeLive(buf: []u8) []const u8 {
    var scalars: [MAX_VALS]Material.ScalarParam = undefined;
    var vectors: [MAX_VALS]Material.VectorParam = undefined;
    var textures: [MAX_VALS]Material.TextureParam = undefined;
    var ns: usize = 0;
    var nv: usize = 0;
    var nt: usize = 0;

    for (sh.params[0..val_count], 0..) |param, i| {
        switch (param.kind) {
            .scalar => {
                scalars[ns] = .{ .name = param.name, .value = vals[i].scalar };
                ns += 1;
            },
            .texture => {
                textures[nt] = .{ .name = param.name, .texture = vals[i].texSlice() };
                nt += 1;
            },
            .vec2, .vec3, .vec4, .color => {
                vectors[nv] = .{ .name = param.name, .value = vals[i].vec };
                nv += 1;
            },
        }
    }

    const mat = Material{
        .shader = sh.guid,
        .scalars = scalars[0..ns],
        .vectors = vectors[0..nv],
        .textures = textures[0..nt],
        .render = render_state,
    };
    var writer = std.Io.Writer.fixed(buf);
    mat.serialize(&writer) catch return "";
    return writer.buffered();
}

// ── Widgets ────────────────────────────────────────────────────────────────────

fn drawScalar(param: shader.ShaderParam, v: *ParamValue, i: usize) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 }, .id_extra = i });
    defer row.deinit();

    gui.label(@src(), "{s}", .{param.label}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 130 }, .id_extra = i });

    if (param.ranged) {
        if (gui.sliderEntry(@src(), "{d:0.3}", .{ .value = &v.scalar, .min = param.min, .max = param.max, .interval = (param.max - param.min) / 1000.0 }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = i })) {
            dirty = true;
        }
    } else {
        const r = gui.textEntryNumber(@src(), f32, .{ .value = &v.scalar }, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = i });
        if (r.changed) dirty = true;
    }
}

fn drawVector(param: shader.ShaderParam, v: *ParamValue, i: usize) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 }, .id_extra = i });
    defer row.deinit();

    gui.label(@src(), "{s}", .{param.label}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 130 }, .id_extra = i });

    var vec3 = engine.Vector3{ .x = v.vec[0], .y = v.vec[1], .z = v.vec[2] };
    if (PropDraw.drawVec3Row(@src(), &vec3)) {
        v.vec[0] = vec3.x;
        v.vec[1] = vec3.y;
        v.vec[2] = vec3.z;
        dirty = true;
    }
}

fn drawColor(param: shader.ShaderParam, v: *ParamValue, i: usize) void {
    if (gui.expander(@src(), param.label, .{}, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 }, .id_extra = i })) {
        var body = gui.box(@src(), .{}, .{ .expand = .horizontal, .padding = .{ .x = 12, .y = 2 }, .id_extra = i });
        defer body.deinit();

        var hsv = gui.Color.HSV.fromColor(vecToColor(v.vec));
        if (gui.colorPicker(@src(), .{ .hsv = &hsv, .alpha = true, .sliders = .rgb }, .{ .expand = .horizontal, .id_extra = i })) {
            v.vec = colorToVec(hsv.toColor());
            dirty = true;
        }
    }
}

fn drawTexture(param: shader.ShaderParam, v: *ParamValue, i: usize) void {
    var row = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 }, .id_extra = i });
    defer row.deinit();

    gui.label(@src(), "{s}", .{param.label}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 130 }, .id_extra = i });

    if (PropDraw.drawRefDropZone(@src(), .asset_ref, v.texSlice(), i)) |new_guid| {
        v.setTex(new_guid);
        dirty = true;
    }

    const picker_id = gui.parentGet().extendId(@src(), i);
    if (gui.button(@src(), "...", .{}, .{
        .gravity_y = 0.5,
        .min_size_content = .{ .w = 24 },
        .id_extra = i,
    })) {
        gui.dataSet(null, picker_id, "tex_open", true);
    }

    if (gui.dataGet(null, picker_id, "tex_open", bool) orelse false) {
        var fw = gui.floatingMenu(@src(), .{ .from = row.data().rectScale().r.toNatural() }, .{ .id_extra = i });
        defer fw.deinit();

        if (pickerTexture(v, fw)) {
            dirty = true;
            gui.dataSet(null, picker_id, "tex_open", false);
        }
        if (gui.minSizeGet(fw.data().id) != null and fw.data().id != gui.focusedSubwindowId()) {
            gui.dataSet(null, picker_id, "tex_open", false);
        }
    }
}

fn pickerTexture(v: *ParamValue, fw: *gui.FloatingMenuWidget) bool {
    if (gui.menuItemLabel(@src(), "(none)", .{}, .{ .expand = .horizontal }) != null) {
        v.setTex("");
        fw.close();
        return true;
    }

    if (!EditorState.assetDbReady()) {
        gui.label(@src(), "(no project open)", .{}, .{});
        return false;
    }

    var any_shown = false;
    var idx: usize = 1; // 0 is reserved for "(none)" above
    var map_it = EditorState.asset_db.by_guid.valueIterator();
    while (map_it.next()) |info| {
        if (info.asset_type != .image) continue;
        any_shown = true;
        const basename = if (std.mem.lastIndexOfScalar(u8, info.path, '/')) |sep|
            info.path[sep + 1 ..]
        else
            info.path;
        var guid_buf: [36]u8 = undefined;
        const guid_str = info.guid.toString(&guid_buf);
        if (gui.menuItemLabel(@src(), basename, .{}, .{ .expand = .horizontal, .id_extra = idx }) != null) {
            v.setTex(guid_str);
            fw.close();
            return true;
        }
        idx += 1;
    }
    if (!any_shown) gui.label(@src(), "(no textures in project)", .{}, .{});
    return false;
}

fn drawRenderState() void {
    if (!gui.expander(@src(), "Render State", .{}, .{ .expand = .horizontal, .padding = .{ .x = 8, .y = 2 } })) return;

    var body = gui.box(@src(), .{}, .{ .expand = .horizontal, .padding = .{ .x = 12, .y = 2 } });
    defer body.deinit();

    {
        var r = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal });
        defer r.deinit();
        gui.label(@src(), "Blend", .{}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 110 } });
        if (gui.dropdownEnum(@src(), Material.BlendMode, .{ .choice = &render_state.blend }, .{}, .{ .expand = .horizontal, .gravity_y = 0.5 })) dirty = true;
    }
    {
        var r = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = 1 });
        defer r.deinit();
        gui.label(@src(), "Cull", .{}, .{ .gravity_y = 0.5, .min_size_content = .{ .w = 110 }, .id_extra = 1 });
        if (gui.dropdownEnum(@src(), Material.CullMode, .{ .choice = &render_state.cull }, .{}, .{ .expand = .horizontal, .gravity_y = 0.5, .id_extra = 1 })) dirty = true;
    }
    {
        var r = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = 2 });
        defer r.deinit();
        const before_w = render_state.depth_write;
        _ = gui.checkbox(@src(), &render_state.depth_write, "Depth Write", .{ .gravity_y = 0.5, .id_extra = 2 });
        if (render_state.depth_write != before_w) dirty = true;
    }
    {
        var r = gui.box(@src(), .{ .dir = .horizontal }, .{ .expand = .horizontal, .id_extra = 3 });
        defer r.deinit();
        const before_t = render_state.depth_test;
        _ = gui.checkbox(@src(), &render_state.depth_test, "Depth Test", .{ .gravity_y = 0.5, .id_extra = 3 });
        if (render_state.depth_test != before_t) dirty = true;
    }
}

// ── Load / Save ────────────────────────────────────────────────────────────────

fn load(asset_path: []const u8) void {
    const n = @min(asset_path.len, loaded_path_buf.len);
    @memcpy(loaded_path_buf[0..n], asset_path[0..n]);
    loaded_path_len = n;
    dirty = false;
    // `drawPreview`'s `ensureFramed(loadedPath(), ...)` re-frames the camera
    // once it notices the path changed — nothing to do here.

    var arena_state = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena_state.deinit();
    const arena = arena_state.allocator();

    // Default to the built-in shader; replace if the file specifies one.
    sh = shader.default();

    const mat: ?Material = Material.load(arena, gui.io, asset_path) catch null;

    if (mat) |m| sh = m.shaderDef();

    val_count = @min(sh.params.len, MAX_VALS);
    for (sh.params[0..val_count], 0..) |param, i| {
        vals[i] = .{};
        switch (param.kind) {
            .scalar => vals[i].scalar = if (mat) |m| m.scalar(param.name, param.default_scalar) else param.default_scalar,
            .texture => {
                const g = if (mat) |m| m.texture(param.name) else "";
                vals[i].setTex(g);
            },
            .vec2, .vec3, .vec4, .color => vals[i].vec = if (mat) |m| m.vector(param.name, param.default_vec) else param.default_vec,
        }
    }
    render_state = if (mat) |m| m.render else .{};
}

fn save() void {
    var scalars: [MAX_VALS]Material.ScalarParam = undefined;
    var vectors: [MAX_VALS]Material.VectorParam = undefined;
    var textures: [MAX_VALS]Material.TextureParam = undefined;
    var ns: usize = 0;
    var nv: usize = 0;
    var nt: usize = 0;

    for (sh.params[0..val_count], 0..) |param, i| {
        switch (param.kind) {
            .scalar => {
                scalars[ns] = .{ .name = param.name, .value = vals[i].scalar };
                ns += 1;
            },
            .texture => {
                textures[nt] = .{ .name = param.name, .texture = vals[i].texSlice() };
                nt += 1;
            },
            .vec2, .vec3, .vec4, .color => {
                vectors[nv] = .{ .name = param.name, .value = vals[i].vec };
                nv += 1;
            },
        }
    }

    const mat = Material{
        .shader = sh.guid,
        .scalars = scalars[0..ns],
        .vectors = vectors[0..nv],
        .textures = textures[0..nt],
        .render = render_state,
    };

    const path = loadedPath();
    mat.save(gui.io, path) catch return;
    dirty = false;

    // Keep the cached artifact in sync with the freshly written source.
    if (EditorState.project_path) |proj| {
        editor.asset_importer.importAssetForce(gui.io, gui.currentWindow().arena(), proj, path);
    }

    // Drop the cached static thumbnail so the Asset Browser/Inspector show the
    // new result immediately, instead of waiting for the next asset-watcher
    // poll to lazily notice the source changed.
    if (EditorState.assetDbReady()) {
        if (EditorState.asset_db.findByPath(path)) |info| {
            var guid_buf: [36]u8 = undefined;
            PreviewSystem.invalidate(info.guid.toString(&guid_buf));
        }
    }
}

// ── Colour conversion (material stores 0..1 floats; dvui uses 0..255 u8) ────────

fn vecToColor(v: [4]f32) gui.Color {
    return .{
        .r = chan(v[0]),
        .g = chan(v[1]),
        .b = chan(v[2]),
        .a = chan(v[3]),
    };
}

fn colorToVec(c: gui.Color) [4]f32 {
    return .{
        @as(f32, @floatFromInt(c.r)) / 255.0,
        @as(f32, @floatFromInt(c.g)) / 255.0,
        @as(f32, @floatFromInt(c.b)) / 255.0,
        @as(f32, @floatFromInt(c.a)) / 255.0,
    };
}

fn chan(x: f32) u8 {
    const clamped = std.math.clamp(x, 0.0, 1.0);
    return @intFromFloat(clamped * 255.0 + 0.5);
}
