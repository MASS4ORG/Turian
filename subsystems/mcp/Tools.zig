//! MCP tool registry for the Turian engine.
//!
//! Each tool maps to a debug protocol method. Arguments are forwarded as-is
//! since both protocols use the same JSON parameter names.

/// A single MCP tool entry.
pub const Tool = struct {
    /// MCP tool name (used in tools/list and tools/call).
    name: []const u8,
    /// Human + LLM readable description.
    description: []const u8,
    /// JSON Schema string for the tool's input (embedded as a raw fragment).
    input_schema: []const u8,
    /// Debug protocol method to call. Null = handled locally (no debug call).
    debug_method: ?[]const u8,
    /// Whether this tool mutates runtime state.
    mutates: bool = false,
};

/// Full tool catalog, ordered by category.
pub const ALL: []const Tool = &[_]Tool{
    // ── Scene ────────────────────────────────────────────────────────────────
    .{
        .name = "list_scenes",
        .description = "List all currently loaded scenes in the running game, including their load state and whether each is active.",
        .input_schema =
        \\{"type":"object","properties":{}}
        ,
        .debug_method = "scene.list",
    },
    .{
        .name = "inspect_scene",
        .description = "List every entity in a loaded scene with name, index, active state, and component types. Omit 'name' to inspect the active scene.",
        .input_schema =
        \\{"type":"object","properties":{"name":{"type":"string","description":"Scene name or ID. Omit for the active scene."}}}
        ,
        .debug_method = "scene.inspect",
    },
    .{
        .name = "find_entities",
        .description = "Find entities in the active scene. Filter by 'name' (substring match) or 'component' type name. Returns all entities when no filter is given.",
        .input_schema =
        \\{"type":"object","properties":{"name":{"type":"string","description":"Substring of entity name"},"component":{"type":"string","description":"Component type tag, e.g. \"Camera\" or \"Light\""}}}
        ,
        .debug_method = "entity.find",
    },
    .{
        .name = "scene_summary",
        .description = "Compact JSON snapshot of all loaded scenes and entities for quick LLM orientation. Use inspect_entity for per-entity detail.",
        .input_schema =
        \\{"type":"object","properties":{}}
        ,
        .debug_method = "snapshot",
    },
    // ── Entity ───────────────────────────────────────────────────────────────
    .{
        .name = "inspect_entity",
        .description = "Inspect an entity in detail: transform (position/rotation/scale) and all attached components with their fields. Identify by 'name' or 'index'.",
        .input_schema =
        \\{"type":"object","properties":{"name":{"type":"string","description":"Entity name"},"index":{"type":"integer","description":"Entity index in scene"}},"oneOf":[{"required":["name"]},{"required":["index"]}]}
        ,
        .debug_method = "entity.inspect",
    },
    .{
        .name = "get_component",
        .description = "Read the current field values of a specific component on an entity.",
        .input_schema =
        \\{"type":"object","properties":{"entity":{"type":"string","description":"Entity name"},"component":{"type":"string","description":"Component type tag"}},"required":["entity","component"]}
        ,
        .debug_method = "component.get",
    },
    .{
        .name = "modify_component",
        .description = "Set a component field on an entity at runtime (e.g. Light.intensity). The edit is applied on the engine's main thread and is undoable in the Studio. Requires the debug server in read-write mode (--rw / allow_write).",
        .input_schema =
        \\{"type":"object","properties":{"entity":{"type":"string"},"component":{"type":"string"},"field":{"type":"string"},"value":{}},"required":["entity","component","field","value"]}
        ,
        .debug_method = "component.set",
        .mutates = true,
    },
    .{
        .name = "set_transform",
        .description = "Set an entity's transform channel (position, rotation, or scale) to a [x,y,z] vector. Undoable; requires read-write mode.",
        .input_schema =
        \\{"type":"object","properties":{"entity":{"type":"string"},"channel":{"type":"string","enum":["position","rotation","scale"]},"value":{"type":"array","items":{"type":"number"},"minItems":3,"maxItems":3}},"required":["entity","channel","value"]}
        ,
        .debug_method = "transform.set",
        .mutates = true,
    },
    .{
        .name = "spawn_entity",
        .description = "Create a new, empty entity with the given name in the active scene. Undoable; requires read-write mode.",
        .input_schema =
        \\{"type":"object","properties":{"name":{"type":"string","description":"Name for the new entity"}},"required":["name"]}
        ,
        .debug_method = "entity.spawn",
        .mutates = true,
    },
    .{
        .name = "destroy_entity",
        .description = "Remove an entity (by name) from the active scene. Undoable; requires read-write mode.",
        .input_schema =
        \\{"type":"object","properties":{"entity":{"type":"string","description":"Name of the entity to destroy"}},"required":["entity"]}
        ,
        .debug_method = "entity.destroy",
        .mutates = true,
    },
    // ── Diagnostics ──────────────────────────────────────────────────────────
    .{
        .name = "get_metrics",
        .description = "Runtime performance metrics: FPS, frame time, memory bytes, draw calls, triangle count, GPU time, and ECS entity/component counts.",
        .input_schema =
        \\{"type":"object","properties":{}}
        ,
        .debug_method = "metrics",
    },
    .{
        .name = "get_schema",
        .description = "Schema of all built-in component types: their tag names, display names, and field definitions (name, type, default value). Use this to understand what components exist before querying entities.",
        .input_schema =
        \\{"type":"object","properties":{}}
        ,
        .debug_method = "schema",
    },
    .{
        .name = "capture_profiler",
        .description = "Capture the latest profiler frame: frame timing, render counters (draw calls, triangles), and each thread's CPU zones with durations.",
        .input_schema =
        \\{"type":"object","properties":{}}
        ,
        .debug_method = "profiler.capture",
    },
    .{
        .name = "inspect_memory",
        .description = "Report tracked allocator memory usage: bytes allocated and live allocation count.",
        .input_schema =
        \\{"type":"object","properties":{}}
        ,
        .debug_method = "memory",
    },
    .{
        .name = "list_errors",
        .description = "List recently captured engine warnings and errors (newest first) from the diagnostic log ring buffer.",
        .input_schema =
        \\{"type":"object","properties":{}}
        ,
        .debug_method = "errors",
    },
    // ── Assets ───────────────────────────────────────────────────────────────
    .{
        .name = "list_assets",
        .description = "List the project's assets (meshes, materials, textures, shaders, audio, scenes) with their GUID, project-relative path, and type.",
        .input_schema =
        \\{"type":"object","properties":{}}
        ,
        .debug_method = "asset.list",
    },
    .{
        .name = "inspect_material",
        .description = "Inspect a single asset (e.g. a material) by GUID, returning its path and type. Use list_assets first to find GUIDs.",
        .input_schema =
        \\{"type":"object","properties":{"guid":{"type":"string","description":"Asset GUID"}},"required":["guid"]}
        ,
        .debug_method = "asset.inspect",
    },
    .{
        .name = "reload_asset",
        .description = "Hot-reload an asset by GUID (re-import + evict caches). Requires read-write mode.",
        .input_schema =
        \\{"type":"object","properties":{"guid":{"type":"string","description":"Asset GUID to reload"}},"required":["guid"]}
        ,
        .debug_method = "asset.reload",
        .mutates = true,
    },
};

/// Find a tool by name. Returns null if not registered.
pub fn find(name: []const u8) ?*const Tool {
    for (ALL) |*t| {
        if (std.mem.eql(u8, t.name, name)) return t;
    }
    return null;
}

const std = @import("std");
