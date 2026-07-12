const std = @import("std");

const cli_build = @import("CliBuild.zig");
const cli_debug = @import("CliDebug.zig");
const cli_mcp = @import("CliMcp.zig");
const cli_docs = @import("CliDocs.zig");
const cli_package = @import("CliPackage.zig");

fn printUsage() void {
    std.debug.print(
        \\turian-cli — Turian Engine headless editor
        \\
        \\Commands:
        \\  new-project <path> [name]   Create a new project at the given path
        \\  info        <project-path>  Print project metadata and component list
        \\  import      <project-path>  Import all assets (reports task progress)
        \\  build       <project-path>  Compile the project into a game executable
        \\  play-build  <project-path>  Compile the in-editor Play-mode library
        \\  debug       <subcommand>    Connect to a running Turian debug server
        \\  mcp                         Start an MCP server (stdio) backed by the debug server
        \\  docs        <subcommand>    Generate AI context or documentation
        \\  package     <subcommand>    Manage project packages
        \\
        \\Env-var overrides for 'build' (optional; build-time paths used by default):
        \\  TURIAN_ENGINE_ROOT    Path to engine/root.zig
        \\  TURIAN_EDITOR_ROOT    Path to editor/root.zig
        \\  TURIAN_CGLTF_WRAP_C   Path to engine/vendor/cgltf_wrap.c
        \\  TURIAN_VENDOR_INCLUDE Path to engine/vendor/ directory
        \\  TURIAN_BUILD_ROOT     Repository root path
        \\  TURIAN_SDL3_LIB       Path to libSDL3 (optional)
        \\  TURIAN_MATH3D_ROOT    Path to math3d/src/root.zig
        \\
    , .{});
}

pub fn main(init: std.process.Init) !void {
    const io = init.io;
    const gpa = init.gpa;

    var args = try std.process.Args.Iterator.initAllocator(init.minimal.args, gpa);
    defer args.deinit();
    _ = args.next();

    const cmd = args.next() orelse return printUsage();

    if (std.mem.eql(u8, cmd, "new-project")) {
        const path = args.next() orelse return printUsage();
        const proj_name = args.next() orelse std.fs.path.basename(path);
        return cli_build.cmdNewProject(io, path, if (proj_name.len > 0) proj_name else "New Project");
    } else if (std.mem.eql(u8, cmd, "info")) {
        const path = args.next() orelse return printUsage();
        return cli_build.cmdInfo(io, gpa, path);
    } else if (std.mem.eql(u8, cmd, "import")) {
        const path = args.next() orelse return printUsage();
        return cli_build.cmdImport(io, gpa, path);
    } else if (std.mem.eql(u8, cmd, "build")) {
        const path = args.next() orelse return printUsage();
        return cli_build.cmdBuild(io, gpa, path, init.environ_map);
    } else if (std.mem.eql(u8, cmd, "play-build")) {
        const path = args.next() orelse return printUsage();
        return cli_build.cmdPlayBuild(io, gpa, path, init.environ_map);
    } else if (std.mem.eql(u8, cmd, "debug")) {
        const sub = args.next() orelse return cli_debug.printUsageDebug();
        return cli_debug.cmdDebug(io, gpa, sub, &args);
    } else if (std.mem.eql(u8, cmd, "mcp")) {
        return cli_mcp.cmdMcp(io, gpa, &args);
    } else if (std.mem.eql(u8, cmd, "docs")) {
        const sub = args.next() orelse return cli_docs.printUsageDocs();
        return cli_docs.cmdDocs(io, gpa, sub, &args);
    } else if (std.mem.eql(u8, cmd, "package")) {
        const sub = args.next() orelse return cli_package.printUsagePackage();
        return cli_package.cmdPackage(io, gpa, sub, &args, init.environ_map);
    } else {
        printUsage();
        return error.UnknownCommand;
    }
}
