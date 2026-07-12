/// Compile user .zig component files to shared libraries and load field
/// metadata via dlopen/getRegistry.  Pure logic — no GUI dependency.
const std = @import("std");
const engine = @import("engine");
const scanner = @import("assets/Scanner.zig");
const GameBuild = @import("build/GameBuild.zig");
const codegen = @import("build/GameCodegen.zig");
const Progress = @import("Progress.zig").Progress;

const api = engine.api;
const ComponentDef = scanner.ComponentDef;
const MAX_COMP_FIELDS = scanner.MAX_COMP_FIELDS;

/// Configuration for building a reflection dynamic library.
pub const ReflectionConfig = struct {
    /// Absolute path to engine/Reflection.zig.
    reflection_zig: []const u8,
    /// Full engine build config: carries all module paths needed to compile
    /// user scripts that @import("engine") (math, oap, serde, ktx2, …).
    build_config: GameBuild.BuildConfig,
};

const lib_ext = if (@import("builtin").os.tag == .windows) ".dll" else if (@import("builtin").os.tag == .macos) ".dylib" else ".so";
const lib_prefix = if (@import("builtin").os.tag == .windows) "" else "lib";

const GetRegistryFn = *const fn () callconv(.c) api.Registry;

/// Compile user script components to a shared library and populate their
/// FieldDef metadata. Reports 0..1 completion (one step per distinct source
/// file) and stops early if `progress` reports a cancellation request —
/// callers should run this off the UI thread (see `studio/state/EditorState.zig`'s
/// background reflect job) since each group spawns a `zig build`.
pub fn loadFieldInfo(
    io: std.Io,
    components: []ComponentDef,
    count: usize,
    config: ReflectionConfig,
    progress: Progress,
) void {
    var processed = std.mem.zeroes([scanner.MAX_COMPONENTS]bool);
    const denom: f32 = @floatFromInt(@max(count, 1));

    for (0..count) |i| {
        if (progress.cancelled()) return;
        if (components[i].is_builtin or processed[i]) continue;

        const src_file = components[i].sourceFile();

        var type_names: [32][]const u8 = undefined;
        var type_count: usize = 0;
        var def_indices: [32]usize = undefined;

        for (0..count) |j| {
            if (!components[j].is_builtin and
                std.mem.eql(u8, components[j].sourceFile(), src_file) and
                !processed[j])
            {
                type_names[type_count] = components[j].typeName();
                def_indices[type_count] = j;
                type_count += 1;
                processed[j] = true;
            }
        }

        if (type_count == 0) continue;
        progress.report(@as(f32, @floatFromInt(i)) / denom, src_file);
        compileAndPopulate(io, src_file, type_names[0..type_count], components, def_indices[0..type_count], config);
    }
    progress.report(1, "");
}

fn getTempDir(a: std.mem.Allocator) []const u8 {
    _ = a;
    return comptime switch (@import("builtin").os.tag) {
        .windows => "C:\\Windows\\Temp",
        else => "/tmp",
    };
}

fn compileAndPopulate(
    io: std.Io,
    source_file: []const u8,
    type_names: []const []const u8,
    components: []ComponentDef,
    def_indices: []const usize,
    config: ReflectionConfig,
) void {
    // Dynamic library reflection requires dlopen (POSIX only).
    if (comptime @import("builtin").os.tag == .windows or
        @import("builtin").os.tag == .wasi) return;

    var arena = std.heap.ArenaAllocator.init(std.heap.page_allocator);
    defer arena.deinit();
    const a = arena.allocator();

    const tmp_dir = getTempDir(a);
    const sep = std.fs.path.sep_str;

    var h = std.hash.Wyhash.init(0);
    h.update(source_file);
    const hash = h.final();

    const lib_name = std.fmt.allocPrint(a, "turian_ref_{x}", .{hash}) catch return;
    // Build dir is a stable temp subdir; reused across hot-reloads of the same file.
    const build_dir = std.fmt.allocPrint(a, "{s}{s}turian_ref_{x}", .{ tmp_dir, sep, hash }) catch return;
    const wrapper_path = std.fmt.allocPrint(a, "{s}{s}turian_wrapper_{x}.zig", .{ tmp_dir, sep, hash }) catch return;

    // Absolutize source_file so paths embedded in the generated build.zig
    // resolve correctly when `zig build` runs from build_dir (a /tmp subdir).
    const abs_source = if (std.fs.path.isAbsolute(source_file))
        source_file
    else
        std.fmt.allocPrint(a, "{s}/{s}", .{ config.build_config.build_root, source_file }) catch source_file;

    // Write the generated wrapper and the build.zig.
    const wrapper_content = generateWrapper(a, type_names) catch return;
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = wrapper_path, .data = wrapper_content }) catch return;

    std.Io.Dir.cwd().createDirPath(io, build_dir) catch {};
    const build_src = codegen.generateReflectionBuildZig(
        a,
        config.build_config,
        config.reflection_zig,
        abs_source,
        wrapper_path,
        lib_name,
    ) catch return;
    const build_zig_path = std.fmt.allocPrint(a, "{s}{s}build.zig", .{ build_dir, sep }) catch return;
    std.Io.Dir.cwd().writeFile(io, .{ .sub_path = build_zig_path, .data = build_src }) catch return;

    // Compile via `zig build` (identical pattern to PlayBuild): no build.zig.zon
    // needed because every path is absolute (.cwd_relative with absolute strings).
    const argv = [_][]const u8{ "zig", "build", "-Doptimize=Debug" };
    GameBuild.spawnAndWaitIn(io, a, &argv, build_dir) catch return;

    // The produced library lands at the standard zig build output path.
    const lib_path = std.fmt.allocPrint(
        a,
        "{s}{s}zig-out{s}lib{s}{s}{s}{s}",
        .{ build_dir, sep, sep, sep, lib_prefix, lib_name, lib_ext },
    ) catch return;

    var lib = std.DynLib.open(lib_path) catch return;
    defer lib.close();

    const getRegistry = lib.lookup(GetRegistryFn, "getRegistry") orelse return;
    const reg = getRegistry();

    for (0..type_names.len) |ti| {
        const short_name = type_names[ti];
        for (0..reg.count) |ri| {
            const ci = reg.components[ri];
            const full_name = std.mem.span(ci.name);
            const bare_name = if (std.mem.lastIndexOfScalar(u8, full_name, '.')) |dot|
                full_name[dot + 1 ..]
            else
                full_name;
            if (std.mem.eql(u8, bare_name, short_name)) {
                populateFields(&components[def_indices[ti]], ci);
                break;
            }
        }
    }
}

fn populateFields(def: *ComponentDef, ci: api.ComponentInfo) void {
    def.field_count = 0;
    const max = @min(ci.field_count, MAX_COMP_FIELDS);
    for (0..max) |fi| {
        const field = ci.fields[fi];
        var fd = &def.fields[def.field_count];
        fd.* = .{};
        fd.setName(std.mem.span(field.name));
        fd.kind = field.field_type;
        fd.asset_filter = field.asset_filter;
        switch (field.field_type) {
            .f32 => fd.default_f32 = field.default_value.as_f32,
            .f64 => fd.default_f64 = field.default_value.as_f64,
            .i32 => fd.default_i32 = field.default_value.as_i32,
            .i64 => fd.default_i64 = field.default_value.as_i64,
            .u32 => fd.default_u32 = field.default_value.as_u32,
            .bool => fd.default_bool = field.default_value.as_bool,
            .vec2 => {
                fd.default_vec2_x = field.default_value.as_vec2.x;
                fd.default_vec2_y = field.default_value.as_vec2.y;
            },
            .vec3 => {
                fd.default_vec3_x = field.default_value.as_vec3.x;
                fd.default_vec3_y = field.default_value.as_vec3.y;
                fd.default_vec3_z = field.default_value.as_vec3.z;
            },
            .vec4 => {
                fd.default_vec4_x = field.default_value.as_vec4.x;
                fd.default_vec4_y = field.default_value.as_vec4.y;
                fd.default_vec4_z = field.default_value.as_vec4.z;
                fd.default_vec4_w = field.default_value.as_vec4.w;
            },
            .game_object_ref, .component_ref, .asset_ref, .string => {},
        }
        def.field_count += 1;
    }
}

fn generateWrapper(allocator: std.mem.Allocator, type_names: []const []const u8) ![]u8 {
    if (type_names.len == 0) {
        return std.fmt.allocPrint(
            allocator,
            "const engine_m = @import(\"engine\");\n" ++
                "const api = engine_m.api;\n" ++
                "export fn getRegistry() callconv(.c) api.Registry {{\n" ++
                "    return .{{ .components = undefined, .count = 0 }};\n" ++
                "}}\n",
            .{},
        );
    }

    var tlen: usize = 0;
    for (type_names, 0..) |name, i| {
        if (i > 0) tlen += 2;
        tlen += "user_module.".len + name.len;
    }
    const types_str = try allocator.alloc(u8, tlen);
    var p: usize = 0;
    for (type_names, 0..) |name, i| {
        if (i > 0) {
            types_str[p] = ',';
            types_str[p + 1] = ' ';
            p += 2;
        }
        const pfx = "user_module.";
        @memcpy(types_str[p..][0..pfx.len], pfx);
        p += pfx.len;
        @memcpy(types_str[p..][0..name.len], name);
        p += name.len;
    }

    return std.fmt.allocPrint(
        allocator,
        "const engine_m = @import(\"engine\");\n" ++
            "const api = engine_m.api;\n" ++
            "const user_module = @import(\"user_module\");\n" ++
            "const reflection = @import(\"reflection\");\n" ++
            "const std = @import(\"std\");\n\n" ++
            "const gpa = std.heap.page_allocator;\n\n" ++
            "export fn getRegistry() callconv(.c) api.Registry {{\n" ++
            "    const types = .{{ {s} }};\n" ++
            "    const N = @typeInfo(@TypeOf(types)).@\"struct\".fields.len;\n" ++
            "    const comps = gpa.alloc(api.ComponentInfo, N) catch\n" ++
            "        return .{{ .components = undefined, .count = 0 }};\n" ++
            "    var count: usize = 0;\n" ++
            "    inline for (types) |T| {{\n" ++
            "        if (reflection.buildReflectedInfo(T, gpa)) |info| {{\n" ++
            "            comps[count] = info;\n" ++
            "            count += 1;\n" ++
            "        }} else |_| {{}}\n" ++
            "    }}\n" ++
            "    return .{{ .components = comps.ptr, .count = count }};\n" ++
            "}}\n",
        .{types_str},
    );
}
