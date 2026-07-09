//! Runtime introspection layer — the single source of truth for
//! debugging, editor tooling, CLI automation, testing, and AI integration.
//!
//! The inspector is *pure*: it never reaches for globals. The host (composition
//! root) hands it a `World` — read-only views into the live scenes plus the
//! shared `Metrics` — and the inspector turns that into structured JSON. The
//! Remote Debug Protocol builds the `World` from the live `SceneManager`
//! and forwards these calls; the MCP server sits on top of that. Keeping
//! every consumer on one authoritative representation is what lets external
//! tools query real engine state instead of inferring it from source.
//!
//! Reflection is automatic where possible: built-in components are plain Zig
//! structs, so their fields, types, and values are emitted via `@typeInfo`
//! without any hand-written per-component code. User script components carry
//! their fields as runtime `ScriptFieldValue`s and are handled alongside.

const std = @import("std");
const Component = @import("../scene/Component.zig").Component;
const SceneNode = @import("../scene/SceneNode.zig").SceneNode;
const Transform = @import("../scene/Transform.zig").Transform;
const UserScriptRef = @import("../scene/UserScriptRef.zig").UserScriptRef;
const Metrics = @import("Metrics.zig").Metrics;
const engine_name = @import("../root.zig").name;
const engine_version = @import("../root.zig").version;

const Stringify = std.json.Stringify;
const Error = Stringify.Error;

// ── World model ──────────────────────────────────────────────────────────────

/// A read-only view of one loaded scene. The host fills this from whatever
/// owns the nodes (e.g. `SceneManager.nodes(handle)` or `EditorState.objects`).
pub const SceneView = struct {
    /// Stable scene id (GUID string), or empty if unknown.
    id: []const u8 = "",
    /// Human-readable scene name.
    name: []const u8 = "",
    /// Whether this is the active scene.
    active: bool = false,
    /// The scene's nodes.
    nodes: []const SceneNode,
};

/// A read-only view of one project asset. The host fills this from whatever
/// owns the asset registry (e.g. the editor's `AssetDatabase`). `engine.introspect`
/// stays free of any editor/render dependency — assets are supplied, not resolved.
pub const AssetView = struct {
    /// Stable asset GUID string.
    guid: []const u8 = "",
    /// Project-relative path.
    path: []const u8 = "",
    /// Asset category (e.g. "material", "model", "image").
    type: []const u8 = "",
};

/// Everything the inspector can see at one instant. All views are borrowed;
/// the inspector never frees them.
/// Result of the last whole-window screenshot capture (see
/// `studio/Screenshots.captureWindow`), for the `screenshot.last` debug-RPC
/// query — polled after a `screenshot.capture` mutation since the actual
/// capture completes on a later frame, not synchronously with the RPC call.
pub const ScreenshotView = struct {
    ok: bool = false,
    path: []const u8 = "",
};

pub const World = struct {
    scenes: []const SceneView = &.{},
    metrics: ?*const Metrics = null,
    assets: []const AssetView = &.{},
    last_screenshot: ?ScreenshotView = null,
};

/// A typed value used when mutating a field by name (see `setComponentField`).
pub const Value = union(enum) {
    number: f64,
    boolean: bool,
    text: []const u8,
    vec3: [3]f32,
};

// ── Type helpers (compile-time) ──────────────────────────────────────────────

fn canHaveDecls(comptime T: type) bool {
    return switch (@typeInfo(T)) {
        .@"struct", .@"union", .@"enum", .@"opaque" => true,
        else => false,
    };
}

/// True for `AssetRef` / `GameObjectRef` / `ComponentRef` / `TypedAssetRef`,
/// which expose a `.slice()` GUID and a marker decl.
fn isRefType(comptime T: type) bool {
    return canHaveDecls(T) and @hasDecl(T, "_turian_ref_kind");
}

/// True for `{ x: f32, y: f32, z: f32 }`-shaped structs (e.g. `Vector3`).
fn isVec3(comptime T: type) bool {
    if (@typeInfo(T) != .@"struct") return false;
    const fs = std.meta.fields(T);
    return fs.len == 3 and fs[0].type == f32 and fs[1].type == f32 and fs[2].type == f32;
}

/// Stable, machine-readable name for a Zig field type. Used by the schema.
fn fieldTypeName(comptime T: type) []const u8 {
    if (comptime isRefType(T)) return @tagName(T._turian_ref_kind);
    if (comptime isVec3(T)) return "vec3";
    return switch (@typeInfo(T)) {
        .float => |f| if (f.bits >= 64) "f64" else "f32",
        .int => |i| if (i.signedness == .unsigned) "u32" else (if (i.bits >= 64) "i64" else "i32"),
        .bool => "bool",
        .@"enum" => "enum",
        .@"struct" => "struct",
        else => "unknown",
    };
}

// ── Value serialization ──────────────────────────────────────────────────────

fn writeVecArr(jw: *Stringify, comps: []const f32) Error!void {
    try jw.beginArray();
    for (comps) |c| try jw.write(c);
    try jw.endArray();
}

/// Writes one component-field value. Refs collapse to their GUID string,
/// vec3-shaped structs to `[x,y,z]`; everything else uses std.json defaults
/// (enums become their tag name, scalars/bools as-is).
fn writeAuto(jw: *Stringify, comptime T: type, val: T) Error!void {
    if (comptime isRefType(T)) {
        try jw.write(val.slice());
        return;
    }
    if (comptime isVec3(T)) {
        try writeVecArr(jw, &.{ val.x, val.y, val.z });
        return;
    }
    try jw.write(val);
}

fn writeStructFieldsInner(jw: *Stringify, comptime T: type, payload: *const T) Error!void {
    inline for (std.meta.fields(T)) |f| {
        if (f.is_comptime) continue;
        try jw.objectField(f.name);
        try writeAuto(jw, f.type, @field(payload.*, f.name));
    }
}

fn writeScriptValue(jw: *Stringify, fv: *const @import("../scene/ScriptFieldValue.zig").ScriptFieldValue) Error!void {
    switch (fv.kind) {
        .f32 => try jw.write(fv.as_f32),
        .f64 => try jw.write(fv.as_f64),
        .i32 => try jw.write(fv.as_i32),
        .i64 => try jw.write(fv.as_i64),
        .u32 => try jw.write(fv.as_u32),
        .bool => try jw.write(fv.as_bool),
        .vec2 => try writeVecArr(jw, &.{ fv.as_vec2_x, fv.as_vec2_y }),
        .vec3 => try writeVecArr(jw, &.{ fv.as_vec3_x, fv.as_vec3_y, fv.as_vec3_z }),
        .vec4 => try writeVecArr(jw, &.{ fv.as_vec4_x, fv.as_vec4_y, fv.as_vec4_z, fv.as_vec4_w }),
        .game_object_ref, .component_ref, .asset_ref => try jw.write(fv.refSlice()),
        .string => try jw.write(fv.stringSlice()),
    }
}

fn writeScriptFieldsInner(jw: *Stringify, u: *const UserScriptRef) Error!void {
    for (u.field_values[0..u.field_count]) |*fv| {
        try jw.objectField(fv.nameSlice());
        try writeScriptValue(jw, fv);
    }
}

/// Returns the authoritative type name of a component (the user script's own
/// type name for script components, the Zig type name for built-ins).
pub fn componentTypeName(c: *const Component) []const u8 {
    return switch (c.*) {
        .user_script => |*u| u.typeName(),
        inline else => |*payload| @typeName(@TypeOf(payload.*)),
    };
}

/// Writes a full component object: `{ type, type_name, fields }`.
pub fn writeComponent(jw: *Stringify, c: *const Component) Error!void {
    try jw.beginObject();
    try jw.objectField("type");
    try jw.write(c.displayName());
    try jw.objectField("type_name");
    try jw.write(componentTypeName(c));
    try jw.objectField("fields");
    try jw.beginObject();
    switch (c.*) {
        .user_script => |*u| try writeScriptFieldsInner(jw, u),
        inline else => |*payload| try writeStructFieldsInner(jw, @TypeOf(payload.*), payload),
    }
    try jw.endObject();
    try jw.endObject();
}

pub fn writeTransform(jw: *Stringify, t: *const Transform) Error!void {
    try jw.beginObject();
    try jw.objectField("position");
    try writeVecArr(jw, &.{ t.position.x, t.position.y, t.position.z });
    try jw.objectField("rotation");
    try writeVecArr(jw, &.{ t.rotation.x, t.rotation.y, t.rotation.z });
    try jw.objectField("scale");
    try writeVecArr(jw, &.{ t.scale.x, t.scale.y, t.scale.z });
    try jw.endObject();
}

fn writeEntityCommon(jw: *Stringify, node: *const SceneNode, index: usize) Error!void {
    try jw.objectField("index");
    try jw.write(index);
    try jw.objectField("name");
    try jw.write(node.nameSlice());
    try jw.objectField("guid");
    try jw.write(node.guidSlice());
    try jw.objectField("parent");
    try jw.write(node.parent);
    try jw.objectField("active");
    try jw.write(node.active);
}

/// Compact entity record: identity plus the list of component display names.
pub fn writeEntitySummary(jw: *Stringify, node: *const SceneNode, index: usize) Error!void {
    try jw.beginObject();
    try writeEntityCommon(jw, node, index);
    try jw.objectField("components");
    try jw.beginArray();
    for (node.components[0..node.component_count]) |*c| try jw.write(c.displayName());
    try jw.endArray();
    try jw.endObject();
}

/// Full entity record: identity, transform, and every component with all fields.
pub fn writeEntityDetail(jw: *Stringify, node: *const SceneNode, index: usize) Error!void {
    try jw.beginObject();
    try writeEntityCommon(jw, node, index);
    try jw.objectField("transform");
    try writeTransform(jw, &node.transform);
    try jw.objectField("components");
    try jw.beginArray();
    for (node.components[0..node.component_count]) |*c| try writeComponent(jw, c);
    try jw.endArray();
    if (node.isPartOfPrefab()) {
        try jw.objectField("prefab");
        try jw.beginObject();
        try jw.objectField("source");
        try jw.write(node.prefabSourceSlice());
        try jw.objectField("template_node");
        try jw.write(node.prefabNodeSlice());
        try jw.objectField("instance_root");
        try jw.write(node.isPrefabInstanceRoot());
        try jw.endObject();
    }
    try jw.endObject();
}

/// Writes one scene. `detail = false` emits entity summaries; `true` emits the
/// full per-entity component breakdown.
pub fn writeScene(jw: *Stringify, view: SceneView, detail: bool) Error!void {
    try jw.beginObject();
    try jw.objectField("id");
    try jw.write(view.id);
    try jw.objectField("name");
    try jw.write(view.name);
    try jw.objectField("active");
    try jw.write(view.active);
    try jw.objectField("entity_count");
    try jw.write(view.nodes.len);
    try jw.objectField("entities");
    try jw.beginArray();
    for (view.nodes, 0..) |*n, i| {
        if (detail) try writeEntityDetail(jw, n, i) else try writeEntitySummary(jw, n, i);
    }
    try jw.endArray();
    try jw.endObject();
}

/// Writes the scene catalog only (no entities) — a cheap overview.
pub fn writeSceneList(jw: *Stringify, world: World) Error!void {
    try jw.beginArray();
    for (world.scenes) |view| {
        try jw.beginObject();
        try jw.objectField("id");
        try jw.write(view.id);
        try jw.objectField("name");
        try jw.write(view.name);
        try jw.objectField("active");
        try jw.write(view.active);
        try jw.objectField("entity_count");
        try jw.write(view.nodes.len);
        try jw.endObject();
    }
    try jw.endArray();
}

/// Writes one asset record: `{ guid, path, type }`.
pub fn writeAsset(jw: *Stringify, view: AssetView) Error!void {
    try jw.beginObject();
    try jw.objectField("guid");
    try jw.write(view.guid);
    try jw.objectField("path");
    try jw.write(view.path);
    try jw.objectField("type");
    try jw.write(view.type);
    try jw.endObject();
}

/// Writes the asset catalog: every asset the host exposed via `World.assets`.
pub fn writeAssetList(jw: *Stringify, world: World) Error!void {
    try jw.beginArray();
    for (world.assets) |view| try writeAsset(jw, view);
    try jw.endArray();
}

fn writeEngineInfo(jw: *Stringify) Error!void {
    try jw.beginObject();
    try jw.objectField("name");
    try jw.write(engine_name);
    try jw.objectField("version");
    try jw.print("\"{d}.{d}.{d}\"", .{ engine_version.major, engine_version.minor, engine_version.patch });
    try jw.endObject();
}

/// Writes a complete runtime snapshot: engine info, metrics, and every scene
/// with full detail. This is the serialisable "everything" form
/// ("Snapshots can be serialized to JSON").
pub fn writeSnapshot(jw: *Stringify, world: World) Error!void {
    try jw.beginObject();
    try jw.objectField("engine");
    try writeEngineInfo(jw);
    try jw.objectField("metrics");
    if (world.metrics) |m| try jw.write(m.*) else try jw.write(null);
    try jw.objectField("scenes");
    try jw.beginArray();
    for (world.scenes) |view| try writeScene(jw, view, true);
    try jw.endArray();
    try jw.endObject();
}

// ── Schema / discovery ───────────────────────────────────────────────────────

fn writeComponentSchema(jw: *Stringify, tag_name: []const u8, comptime T: type) Error!void {
    try jw.beginObject();
    try jw.objectField("tag");
    try jw.write(tag_name);
    try jw.objectField("type_name");
    try jw.write(@typeName(T));
    try jw.objectField("fields");
    try jw.beginArray();
    inline for (std.meta.fields(T)) |f| {
        if (f.is_comptime) continue;
        try jw.beginObject();
        try jw.objectField("name");
        try jw.write(f.name);
        try jw.objectField("type");
        try jw.write(comptime fieldTypeName(f.type));
        if (@typeInfo(f.type) == .@"enum") {
            try jw.objectField("values");
            try jw.beginArray();
            inline for (std.meta.fields(f.type)) |ef| try jw.write(ef.name);
            try jw.endArray();
        }
        try jw.objectField("mutable");
        try jw.write(true);
        try jw.endObject();
    }
    try jw.endArray();
    try jw.endObject();
}

/// Writes the component-type catalog: every built-in component, its fields,
/// field types, and enum value sets. Agents and tools use this to
/// discover what exists without hardcoded knowledge.
pub fn writeSchema(jw: *Stringify) Error!void {
    try jw.beginObject();
    try jw.objectField("components");
    try jw.beginArray();
    inline for (std.meta.fields(Component)) |uf| {
        if (comptime std.mem.eql(u8, uf.name, "user_script")) continue;
        try writeComponentSchema(jw, uf.name, uf.type);
    }
    try jw.endArray();
    try jw.endObject();
}

// ── Allocation convenience wrappers ──────────────────────────────────────────

fn finish(allocator: std.mem.Allocator, out: *std.Io.Writer.Allocating) ![]u8 {
    return allocator.dupe(u8, out.written());
}

fn makeWriter(out: *std.Io.Writer.Allocating, pretty: bool) Stringify {
    return .{
        .writer = &out.writer,
        .options = .{ .whitespace = if (pretty) .indent_2 else .minified },
    };
}

/// Serialises a full snapshot to a heap-allocated JSON string (caller frees).
pub fn snapshotJsonAlloc(allocator: std.mem.Allocator, world: World, pretty: bool) ![]u8 {
    var out: std.Io.Writer.Allocating = .init(allocator);
    defer out.deinit();
    var jw = makeWriter(&out, pretty);
    try writeSnapshot(&jw, world);
    return finish(allocator, &out);
}

/// Serialises the component schema to a heap-allocated JSON string (caller frees).
pub fn schemaJsonAlloc(allocator: std.mem.Allocator, pretty: bool) ![]u8 {
    var out: std.Io.Writer.Allocating = .init(allocator);
    defer out.deinit();
    var jw = makeWriter(&out, pretty);
    try writeSchema(&jw);
    return finish(allocator, &out);
}

/// Serialises a single entity's detail to a heap-allocated JSON string.
pub fn entityJsonAlloc(allocator: std.mem.Allocator, node: *const SceneNode, index: usize, pretty: bool) ![]u8 {
    var out: std.Io.Writer.Allocating = .init(allocator);
    defer out.deinit();
    var jw = makeWriter(&out, pretty);
    try writeEntityDetail(&jw, node, index);
    return finish(allocator, &out);
}

// ── Queries (pure, allocation-free) ──────────────────────────────────────────

/// Index of the first component whose display name matches, or null.
pub fn componentIndex(node: *const SceneNode, display_name: []const u8) ?usize {
    for (node.components[0..node.component_count], 0..) |*c, i| {
        if (std.mem.eql(u8, c.displayName(), display_name)) return i;
    }
    return null;
}

pub fn hasComponent(node: *const SceneNode, display_name: []const u8) bool {
    return componentIndex(node, display_name) != null;
}

/// Fills `out` with the indices of nodes carrying a component named
/// `display_name`. Returns the count written (capped at `out.len`).
pub fn findByComponent(nodes: []const SceneNode, display_name: []const u8, out: []usize) usize {
    var n: usize = 0;
    for (nodes, 0..) |*node, i| {
        if (n >= out.len) break;
        if (hasComponent(node, display_name)) {
            out[n] = i;
            n += 1;
        }
    }
    return n;
}

/// Fills `out` with the indices of nodes whose name contains `substring`.
pub fn findByName(nodes: []const SceneNode, substring: []const u8, out: []usize) usize {
    var n: usize = 0;
    for (nodes, 0..) |*node, i| {
        if (n >= out.len) break;
        if (std.mem.indexOf(u8, node.nameSlice(), substring) != null) {
            out[n] = i;
            n += 1;
        }
    }
    return n;
}

/// Fills `out` with the indices of nodes within `radius` of `point` (by local
/// transform position). Returns the count written.
pub fn findNear(nodes: []const SceneNode, point: [3]f32, radius: f32, out: []usize) usize {
    const r2 = radius * radius;
    var n: usize = 0;
    for (nodes, 0..) |*node, i| {
        if (n >= out.len) break;
        const p = node.transform.position;
        const dx = p.x - point[0];
        const dy = p.y - point[1];
        const dz = p.z - point[2];
        if (dx * dx + dy * dy + dz * dz <= r2) {
            out[n] = i;
            n += 1;
        }
    }
    return n;
}

/// Convenience: nodes carrying an active Camera component.
pub fn activeCameras(nodes: []const SceneNode, out: []usize) usize {
    var n: usize = 0;
    for (nodes, 0..) |*node, i| {
        if (n >= out.len) break;
        if (node.active and hasComponent(node, "Camera")) {
            out[n] = i;
            n += 1;
        }
    }
    return n;
}

/// Convenience: nodes carrying a Light component.
pub fn lights(nodes: []const SceneNode, out: []usize) usize {
    return findByComponent(nodes, "Light", out);
}

// ── Mutation ─────────────────────────────────────────────────────────────────

fn assignField(comptime F: type, dst: *F, v: Value) bool {
    if (comptime isRefType(F)) {
        if (v == .text) {
            dst.set(v.text);
            return true;
        }
        return false;
    }
    if (comptime isVec3(F)) {
        if (v == .vec3) {
            dst.x = v.vec3[0];
            dst.y = v.vec3[1];
            dst.z = v.vec3[2];
            return true;
        }
        return false;
    }
    switch (@typeInfo(F)) {
        .float => if (v == .number) {
            dst.* = @floatCast(v.number);
            return true;
        },
        .int => if (v == .number) {
            dst.* = @intFromFloat(v.number);
            return true;
        },
        .bool => if (v == .boolean) {
            dst.* = v.boolean;
            return true;
        },
        .@"enum" => if (v == .text) {
            if (std.meta.stringToEnum(F, v.text)) |e| {
                dst.* = e;
                return true;
            }
        },
        else => {},
    }
    return false;
}

fn setBuiltinField(comptime T: type, payload: *T, name: []const u8, v: Value) bool {
    inline for (std.meta.fields(T)) |f| {
        if (f.is_comptime) continue;
        if (std.mem.eql(u8, f.name, name)) {
            return assignField(f.type, &@field(payload.*, f.name), v);
        }
    }
    return false;
}

fn setScriptField(u: *UserScriptRef, name: []const u8, v: Value) bool {
    for (u.field_values[0..u.field_count]) |*fv| {
        if (!std.mem.eql(u8, fv.nameSlice(), name)) continue;
        switch (fv.kind) {
            .f32 => if (v == .number) {
                fv.as_f32 = @floatCast(v.number);
                return true;
            },
            .f64 => if (v == .number) {
                fv.as_f64 = v.number;
                return true;
            },
            .i32 => if (v == .number) {
                fv.as_i32 = @intFromFloat(v.number);
                return true;
            },
            .i64 => if (v == .number) {
                fv.as_i64 = @intFromFloat(v.number);
                return true;
            },
            .u32 => if (v == .number) {
                fv.as_u32 = @intFromFloat(v.number);
                return true;
            },
            .bool => if (v == .boolean) {
                fv.as_bool = v.boolean;
                return true;
            },
            .vec2 => if (v == .vec3) {
                fv.as_vec2_x = v.vec3[0];
                fv.as_vec2_y = v.vec3[1];
                return true;
            },
            .vec3 => if (v == .vec3) {
                fv.as_vec3_x = v.vec3[0];
                fv.as_vec3_y = v.vec3[1];
                fv.as_vec3_z = v.vec3[2];
                return true;
            },
            .vec4 => if (v == .vec3) {
                fv.as_vec4_x = v.vec3[0];
                fv.as_vec4_y = v.vec3[1];
                fv.as_vec4_z = v.vec3[2];
                return true;
            },
            .game_object_ref, .component_ref, .asset_ref => if (v == .text) {
                fv.setRef(v.text);
                return true;
            },
            .string => if (v == .text) {
                fv.setString(v.text);
                return true;
            },
        }
        return false;
    }
    return false;
}

/// Sets a component field by name. Works for both built-in components (via
/// compile-time field reflection) and user script components (via their runtime
/// field values). Returns false if the field is unknown or the value type is
/// incompatible with the field type.
pub fn setComponentField(c: *Component, field_name: []const u8, v: Value) bool {
    switch (c.*) {
        .user_script => |*u| return setScriptField(u, field_name, v),
        inline else => |*payload| return setBuiltinField(@TypeOf(payload.*), payload, field_name, v),
    }
}

/// Sets a transform component (`position` / `rotation` / `scale`) from a vec3.
/// Returns false for an unknown channel.
pub fn setTransformField(node: *SceneNode, channel: []const u8, value: [3]f32) bool {
    const target: *@import("../root.zig").Vector3 =
        if (std.mem.eql(u8, channel, "position")) &node.transform.position else if (std.mem.eql(u8, channel, "rotation")) &node.transform.rotation else if (std.mem.eql(u8, channel, "scale")) &node.transform.scale else return false;
    target.x = value[0];
    target.y = value[1];
    target.z = value[2];
    return true;
}

/// Appends a new, empty entity to a mutable scene array. Returns a pointer to
/// the new node, or null if the array is at capacity.
pub fn spawnEntity(nodes: []SceneNode, count: *usize, name: []const u8) ?*SceneNode {
    if (count.* >= nodes.len) return null;
    const node = &nodes[count.*];
    node.* = .{};
    node.setName(name);
    count.* += 1;
    return node;
}

/// Removes the entity at `index` from a mutable scene array, shifting the rest
/// down. Returns false if the index is out of range.
pub fn destroyEntity(nodes: []SceneNode, count: *usize, index: usize) bool {
    if (index >= count.*) return false;
    var i = index;
    while (i + 1 < count.*) : (i += 1) nodes[i] = nodes[i + 1];
    count.* -= 1;
    return true;
}

// ── Tests ────────────────────────────────────────────────────────────────────

const testing = std.testing;
const LightComponent = @import("../components/LightComponent.zig").LightComponent;
const CameraComponent = @import("../components/CameraComponent.zig").CameraComponent;

fn makeNode(name: []const u8) SceneNode {
    var n = SceneNode{};
    n.setName(name);
    return n;
}

test "writeSchema lists built-in components with fields and enum values" {
    const json = try schemaJsonAlloc(testing.allocator, false);
    defer testing.allocator.free(json);

    // Every built-in is present, plus reflected fields and enum value sets.
    try testing.expect(std.mem.indexOf(u8, json, "\"tag\":\"light\"") != null);
    try testing.expect(std.mem.indexOf(u8, json, "\"name\":\"intensity\"") != null);
    try testing.expect(std.mem.indexOf(u8, json, "\"type\":\"f32\"") != null);
    // LightComponent.kind is an enum → its values are enumerated.
    try testing.expect(std.mem.indexOf(u8, json, "\"directional\"") != null);
    try testing.expect(std.mem.indexOf(u8, json, "\"asset_ref\"") != null); // MeshRenderer.mesh
    // user_script is excluded from the static schema.
    try testing.expect(std.mem.indexOf(u8, json, "user_script") == null);
}

test "writeEntityDetail reflects built-in component fields automatically" {
    var node = makeNode("Sun");
    node.setGuidStr("11111111-1111-4111-8111-111111111111");
    node.transform.position = .{ .x = 10, .y = 5, .z = 0 };
    var light = LightComponent{ .kind = .point, .intensity = 3.5 };
    _ = node.addComponent(.{ .light = light });
    _ = &light;

    const json = try entityJsonAlloc(testing.allocator, &node, 0, false);
    defer testing.allocator.free(json);

    try testing.expect(std.mem.indexOf(u8, json, "\"name\":\"Sun\"") != null);
    try testing.expect(std.mem.indexOf(u8, json, "\"position\":[10,5,0]") != null);
    try testing.expect(std.mem.indexOf(u8, json, "\"type\":\"Light\"") != null);
    try testing.expect(std.mem.indexOf(u8, json, "\"kind\":\"point\"") != null);
    try testing.expect(std.mem.indexOf(u8, json, "\"intensity\":3.5") != null);
}

test "snapshot includes engine info, metrics and scenes" {
    var nodes = [_]SceneNode{makeNode("Camera")};
    _ = nodes[0].addComponent(.{ .camera = CameraComponent{} });
    var metrics = Metrics{ .fps = 60, .entity_count = 1 };
    const views = [_]SceneView{.{ .name = "Main", .active = true, .nodes = &nodes }};
    const world = World{ .scenes = &views, .metrics = &metrics };

    const json = try snapshotJsonAlloc(testing.allocator, world, false);
    defer testing.allocator.free(json);

    try testing.expect(std.mem.indexOf(u8, json, "Turian Engine") != null);
    try testing.expect(std.mem.indexOf(u8, json, "\"fps\":60") != null);
    try testing.expect(std.mem.indexOf(u8, json, "\"name\":\"Main\"") != null);
    try testing.expect(std.mem.indexOf(u8, json, "\"type\":\"Camera\"") != null);
}

test "queries: by component, by name, near, cameras, lights" {
    var nodes = [_]SceneNode{ makeNode("Player"), makeNode("Enemy"), makeNode("MainCamera") };
    nodes[0].transform.position = .{ .x = 0, .y = 0, .z = 0 };
    nodes[1].transform.position = .{ .x = 100, .y = 0, .z = 0 };
    nodes[2].transform.position = .{ .x = 1, .y = 0, .z = 0 };
    _ = nodes[0].addComponent(.{ .light = LightComponent{} });
    _ = nodes[2].addComponent(.{ .camera = CameraComponent{} });

    var buf: [8]usize = undefined;

    try testing.expectEqual(@as(usize, 1), findByComponent(&nodes, "Light", &buf));
    try testing.expectEqual(@as(usize, 0), buf[0]);

    try testing.expectEqual(@as(usize, 2), findByName(&nodes, "a", &buf)); // Player, MainCamera
    try testing.expectEqual(@as(usize, 1), activeCameras(&nodes, &buf));
    try testing.expectEqual(@as(usize, 2), buf[0]);

    // Within radius 2 of origin: Player (0) and MainCamera (1 unit away).
    try testing.expectEqual(@as(usize, 2), findNear(&nodes, .{ 0, 0, 0 }, 2.0, &buf));
}

test "mutation: set built-in scalar, enum, bool, and reject type mismatch" {
    var c = Component{ .light = LightComponent{} };
    try testing.expect(setComponentField(&c, "intensity", .{ .number = 4.0 }));
    try testing.expectApproxEqAbs(@as(f32, 4.0), c.light.intensity, 0.0001);

    try testing.expect(setComponentField(&c, "kind", .{ .text = "spot" }));
    try testing.expectEqual(LightComponent.Kind.spot, c.light.kind);

    try testing.expect(setComponentField(&c, "cast_shadows", .{ .boolean = false }));
    try testing.expectEqual(false, c.light.cast_shadows);

    // Wrong value type and unknown field both fail without mutating.
    try testing.expect(!setComponentField(&c, "intensity", .{ .boolean = true }));
    try testing.expect(!setComponentField(&c, "nonexistent", .{ .number = 1.0 }));
}

test "mutation: set asset_ref field on built-in component" {
    var c = Component{ .mesh_renderer = .{} };
    try testing.expect(setComponentField(&c, "mesh", .{ .text = "abcd-guid" }));
    try testing.expectEqualStrings("abcd-guid", c.mesh_renderer.mesh.slice());
}

test "spawn and destroy entities in a mutable scene array" {
    var nodes: [4]SceneNode = undefined;
    var count: usize = 0;

    const a = spawnEntity(&nodes, &count, "A") orelse return error.SpawnFailed;
    _ = spawnEntity(&nodes, &count, "B") orelse return error.SpawnFailed;
    _ = spawnEntity(&nodes, &count, "C") orelse return error.SpawnFailed;
    try testing.expectEqual(@as(usize, 3), count);
    try testing.expectEqualStrings("A", a.nameSlice());

    try testing.expect(destroyEntity(&nodes, &count, 1)); // remove B
    try testing.expectEqual(@as(usize, 2), count);
    try testing.expectEqualStrings("A", nodes[0].nameSlice());
    try testing.expectEqualStrings("C", nodes[1].nameSlice());

    try testing.expect(!destroyEntity(&nodes, &count, 9)); // out of range
}

test "setTransformField updates the requested channel" {
    var node = makeNode("X");
    try testing.expect(setTransformField(&node, "position", .{ 1, 2, 3 }));
    try testing.expectApproxEqAbs(@as(f32, 2.0), node.transform.position.y, 0.0001);
    try testing.expect(!setTransformField(&node, "bogus", .{ 0, 0, 0 }));
}
