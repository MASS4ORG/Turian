const std = @import("std");
const editor = @import("editor");
const build_options = @import("turian_build_options");

pub fn printUsagePackage() void {
    std.debug.print(
        \\turian-cli package — Manage project packages
        \\
        \\Usage:  turian-cli package <subcommand> [--project <path>] [args]
        \\
        \\Subcommands:
        \\  install <source>    Install a package (local path or git URL)
        \\  remove  <name>      Remove an installed package by name
        \\  update  [name]      Update a package (or all packages)
        \\  list                List installed packages
        \\  info    <name>      Show full manifest for an installed package
        \\  search  <query>     Search the package registry
        \\
        \\Flags:
        \\  --project <path>    Project root directory (default: current directory)
        \\  --vendored          Install into <project>/packages/ instead of the
        \\                      shared store (for committing/offline use)
        \\
        \\By default packages install into the central store
        \\($TURIAN_PACKAGE_HOME, else ~/.cache/turian/packages), shared across
        \\projects, and are recorded in project.json.
        \\
    , .{});
}

pub fn cmdPackage(
    io: std.Io,
    gpa: std.mem.Allocator,
    sub: []const u8,
    args: *std.process.Args.Iterator,
    environ: *const std.process.Environ.Map,
) !void {
    var project_buf: [512]u8 = std.mem.zeroes([512]u8);
    project_buf[0] = '.';
    var project_len: usize = 1;
    var arg1: []const u8 = "";
    var vendored = false;

    while (args.next()) |a| {
        if (std.mem.eql(u8, a, "--project")) {
            const v = args.next() orelse return error.MissingArg;
            const n = @min(v.len, project_buf.len - 1);
            @memcpy(project_buf[0..n], v[0..n]);
            project_len = n;
        } else if (std.mem.eql(u8, a, "--vendored") or std.mem.eql(u8, a, "--local")) {
            vendored = true;
        } else if (arg1.len == 0) {
            arg1 = a;
        }
    }
    const project_path = project_buf[0..project_len];

    const store_root = editor.package_store.resolveRoot(gpa, environ) catch "";
    defer if (store_root.len > 0) gpa.free(store_root);

    if (std.mem.eql(u8, sub, "install")) {
        if (arg1.len == 0) return printUsagePackage();
        return cmdPackageInstall(io, gpa, project_path, arg1, store_root, vendored);
    } else if (std.mem.eql(u8, sub, "remove")) {
        if (arg1.len == 0) return printUsagePackage();
        return cmdPackageRemove(io, gpa, project_path, arg1, store_root);
    } else if (std.mem.eql(u8, sub, "update")) {
        return cmdPackageUpdate(io, gpa, project_path, arg1, store_root);
    } else if (std.mem.eql(u8, sub, "list")) {
        return cmdPackageList(io, gpa, project_path, store_root);
    } else if (std.mem.eql(u8, sub, "info")) {
        if (arg1.len == 0) return printUsagePackage();
        return cmdPackageInfo(io, gpa, project_path, arg1, store_root);
    } else if (std.mem.eql(u8, sub, "search")) {
        std.debug.print(
            \\Package registry search is not yet available.
            \\A registry/repository API is planned.
            \\In the meantime, install packages from a local path or git URL:
            \\  turian-cli package install /path/to/package
            \\  turian-cli package install git+https://example.com/my-package
            \\
        , .{});
        return;
    } else {
        printUsagePackage();
        return error.UnknownSubcommand;
    }
}

fn cmdPackageInstall(
    io: std.Io,
    gpa: std.mem.Allocator,
    project_path: []const u8,
    source: []const u8,
    store_root: []const u8,
    vendored: bool,
) !void {
    const is_git = std.mem.startsWith(u8, source, "git+http") or
        std.mem.startsWith(u8, source, "http://") or
        std.mem.startsWith(u8, source, "https://");

    var scratch: ?[]const u8 = null;
    defer if (scratch) |s| {
        std.Io.Dir.cwd().deleteTree(io, s) catch {};
        gpa.free(s);
    };
    const src_dir: []const u8 = if (is_git) blk: {
        const git_url = if (std.mem.startsWith(u8, source, "git+")) source[4..] else source;
        const tmp = try std.fmt.allocPrint(gpa, "{s}/.cache/clone-{x}", .{
            if (store_root.len > 0) store_root else ".",
            std.hash.Wyhash.hash(0, source),
        });
        std.Io.Dir.cwd().deleteTree(io, tmp) catch {};
        std.debug.print("[Turian] Cloning {s}\n", .{git_url});
        const argv = [_][]const u8{ "git", "clone", "--depth=1", git_url, tmp };
        editor.GameBuild.spawnAndWait(io, gpa, &argv) catch |err| {
            std.debug.print("[Turian] git clone failed: {s}\n", .{@errorName(err)});
            gpa.free(tmp);
            return error.InstallFailed;
        };
        scratch = tmp;
        break :blk tmp;
    } else source;

    var mpath_buf: [1024]u8 = undefined;
    const manifest_path = std.fmt.bufPrint(&mpath_buf, "{s}/turian-package.json", .{src_dir}) catch
        return error.PathTooLong;
    const manifest_bytes = std.Io.Dir.cwd().readFileAlloc(io, manifest_path, gpa, .limited(64 * 1024)) catch {
        std.debug.print("[Turian] No turian-package.json found at: {s}\n", .{src_dir});
        return error.InvalidPackage;
    };
    defer gpa.free(manifest_bytes);
    const manifest = editor.PackageManifest.parse(gpa, manifest_bytes) catch |err| {
        std.debug.print("[Turian] Invalid turian-package.json: {s}\n", .{@errorName(err)});
        return error.InvalidPackage;
    };
    defer manifest.deinit();

    if (vendored or store_root.len == 0) {
        try installVendored(io, gpa, project_path, src_dir, manifest.name, manifest.version);
    } else {
        try installToStore(io, gpa, project_path, src_dir, source, is_git, store_root, manifest.name, manifest.version);
    }
}

fn installToStore(
    io: std.Io,
    gpa: std.mem.Allocator,
    project_path: []const u8,
    src_dir: []const u8,
    source: []const u8,
    is_git: bool,
    store_root: []const u8,
    name: []const u8,
    version: []const u8,
) !void {
    const dest = try editor.package_store.packagePath(gpa, store_root, name, version);
    defer gpa.free(dest);

    if (editor.package_store.isInstalled(io, gpa, store_root, name, version)) {
        std.debug.print("[Turian] '{s}' v{s} already in store ({s})\n", .{ name, version, dest });
    } else {
        std.debug.print("[Turian] Installing '{s}' v{s} into store\n", .{ name, version });
        copyDir(io, gpa, src_dir, dest) catch |err| {
            std.debug.print("[Turian] Copy to store failed: {s}\n", .{@errorName(err)});
            return error.InstallFailed;
        };
    }

    var cfg = editor.ProjectConfig.load(io, gpa, project_path) catch try editor.ProjectConfig.initDefault(gpa, "");
    defer cfg.deinit();
    cfg.addPackage(io, project_path, name, version, source, is_git) catch |err| {
        std.debug.print("[Turian] Failed to record package in project.json: {s}\n", .{@errorName(err)});
        return error.InstallFailed;
    };
    std.debug.print("[Turian] '{s}' v{s} added to {s}/project.json\n", .{ name, version, project_path });
}

fn installVendored(
    io: std.Io,
    gpa: std.mem.Allocator,
    project_path: []const u8,
    src_dir: []const u8,
    name: []const u8,
    version: []const u8,
) !void {
    var pkg_dir_buf: [512]u8 = undefined;
    const packages_path = std.fmt.bufPrint(&pkg_dir_buf, "{s}/packages", .{project_path}) catch
        return error.PathTooLong;
    std.Io.Dir.cwd().createDirPath(io, packages_path) catch {};

    const dest = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ packages_path, name });
    defer gpa.free(dest);
    {
        var existing = std.Io.Dir.cwd().openDir(io, dest, .{}) catch null;
        if (existing) |*d| {
            d.close(io);
            std.debug.print("[Turian] Package '{s}' is already vendored at {s}\n", .{ name, dest });
            return error.AlreadyInstalled;
        }
    }
    std.debug.print("[Turian] Vendoring '{s}' v{s} into {s}\n", .{ name, version, dest });
    copyDir(io, gpa, src_dir, dest) catch |err| {
        std.debug.print("[Turian] Copy failed: {s}\n", .{@errorName(err)});
        return error.InstallFailed;
    };
}

fn copyDir(io: std.Io, gpa: std.mem.Allocator, src: []const u8, dst: []const u8) !void {
    std.Io.Dir.cwd().createDirPath(io, dst) catch {};
    var dir = try std.Io.Dir.cwd().openDir(io, src, .{ .iterate = true });
    defer dir.close(io);

    var iter = dir.iterate();
    while (iter.next(io) catch null) |entry| {
        const src_child = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ src, entry.name });
        defer gpa.free(src_child);
        const dst_child = try std.fmt.allocPrint(gpa, "{s}/{s}", .{ dst, entry.name });
        defer gpa.free(dst_child);

        if (entry.kind == .directory) {
            try copyDir(io, gpa, src_child, dst_child);
        } else if (entry.kind == .file) {
            const bytes = try std.Io.Dir.cwd().readFileAlloc(io, src_child, gpa, .unlimited);
            defer gpa.free(bytes);
            try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = dst_child, .data = bytes });
        }
    }
}

fn cmdPackageRemove(io: std.Io, gpa: std.mem.Allocator, project_path: []const u8, name: []const u8, store_root: []const u8) !void {
    _ = store_root;
    var removed = false;

    var cfg = editor.ProjectConfig.load(io, gpa, project_path) catch try editor.ProjectConfig.initDefault(gpa, "");
    defer cfg.deinit();
    if (cfg.removePackage(io, project_path, name) catch false) {
        removed = true;
        std.debug.print("[Turian] Removed '{s}' from {s}/project.json\n", .{ name, project_path });
    }

    var vbuf: [1024]u8 = undefined;
    const vendored = std.fmt.bufPrint(&vbuf, "{s}/packages/{s}", .{ project_path, name }) catch return error.PathTooLong;
    if (std.Io.Dir.cwd().openDir(io, vendored, .{})) |*d| {
        d.close(io);
        std.Io.Dir.cwd().deleteTree(io, vendored) catch |err| {
            std.debug.print("[Turian] Failed to remove vendored dir: {s}\n", .{@errorName(err)});
            return error.RemoveFailed;
        };
        removed = true;
        std.debug.print("[Turian] Removed vendored '{s}'\n", .{name});
    } else |_| {}

    if (!removed) {
        std.debug.print("[Turian] Package '{s}' is not installed.\n", .{name});
        return error.PackageNotFound;
    }
}

fn cmdPackageUpdate(io: std.Io, gpa: std.mem.Allocator, project_path: []const u8, name: []const u8, store_root: []const u8) !void {
    var cfg = editor.ProjectConfig.load(io, gpa, project_path) catch try editor.ProjectConfig.initDefault(gpa, "");
    defer cfg.deinit();

    var n: usize = 0;
    for (cfg.packages) |p| {
        if (name.len > 0 and !std.mem.eql(u8, p.name, name)) continue;
        if (p.source.len == 0) {
            std.debug.print("[Turian] '{s}' has no recorded source; skipping\n", .{p.name});
            continue;
        }
        const src = gpa.dupe(u8, p.source) catch continue;
        defer gpa.free(src);
        std.debug.print("[Turian] Updating '{s}' from {s}\n", .{ p.name, src });
        cmdPackageInstall(io, gpa, project_path, src, store_root, false) catch |err| {
            std.debug.print("[Turian] Update of '{s}' failed: {s}\n", .{ p.name, @errorName(err) });
            continue;
        };
        n += 1;
    }
    if (n == 0) std.debug.print("[Turian] Nothing to update.\n", .{});
}

fn cmdPackageList(io: std.Io, gpa: std.mem.Allocator, project_path: []const u8, store_root: []const u8) !void {
    var pm = editor.PackageManager.discover(io, gpa, project_path, editor.PackageManager.parseEngineVersion(build_options.version), store_root);
    defer pm.deinit();

    if (pm.packageCount() == 0) {
        std.debug.print("No packages installed in {s}\n", .{project_path});
        return;
    }

    std.debug.print("{d} package(s) installed in {s}:\n", .{ pm.packageCount(), project_path });
    for (pm.packages.items) |*pkg| {
        const types_str = formatTypes(pkg.manifest.types);
        std.debug.print("  {s}  v{s}  [{s}]\n", .{ pkg.manifest.name, pkg.manifest.version, types_str.slice() });
    }

    for (pm.diagnostics.items) |d| {
        std.debug.print("  {s}: {s}\n", .{ if (d.is_error) "error" else "warning", d.message });
    }
}

fn cmdPackageInfo(io: std.Io, gpa: std.mem.Allocator, project_path: []const u8, name: []const u8, store_root: []const u8) !void {
    var pm = editor.PackageManager.discover(io, gpa, project_path, editor.PackageManager.parseEngineVersion(build_options.version), store_root);
    defer pm.deinit();

    for (pm.packages.items) |*pkg| {
        if (!std.mem.eql(u8, pkg.manifest.name, name)) continue;
        const m = &pkg.manifest;
        const types_str = formatTypes(m.types);
        std.debug.print("Name:         {s}\n", .{m.name});
        std.debug.print("Version:      {s}\n", .{m.version});
        if (m.author.len > 0) std.debug.print("Author:       {s}\n", .{m.author});
        if (m.description.len > 0) std.debug.print("Description:  {s}\n", .{m.description});
        if (m.license.len > 0) std.debug.print("License:      {s}\n", .{m.license});
        if (m.engine_compat.len > 0) std.debug.print("Engine:       {s}\n", .{m.engine_compat});
        std.debug.print("Types:        {s}\n", .{types_str.slice()});
        std.debug.print("Location:     {s}\n", .{pkg.root});
        if (m.asset_dirs.len > 0) {
            std.debug.print("Asset dirs:   ", .{});
            for (m.asset_dirs, 0..) |d, i| {
                if (i > 0) std.debug.print(", ", .{});
                std.debug.print("{s}", .{d});
            }
            std.debug.print("\n", .{});
        }
        return;
    }

    std.debug.print("Package '{s}' is not installed.\n", .{name});
    return error.PackageNotFound;
}

const TypesStr = struct {
    buf: [64]u8,
    len: usize,
    pub fn slice(self: *const TypesStr) []const u8 {
        return self.buf[0..self.len];
    }
};

fn formatTypes(types: []const editor.PackageType) TypesStr {
    var r = TypesStr{ .buf = std.mem.zeroes([64]u8), .len = 0 };
    for (types, 0..) |t, i| {
        if (i > 0 and r.len < r.buf.len - 2) {
            r.buf[r.len] = ',';
            r.buf[r.len + 1] = ' ';
            r.len += 2;
        }
        const tag = @tagName(t);
        const remaining = r.buf.len - r.len;
        const copy_len = @min(tag.len, remaining);
        @memcpy(r.buf[r.len..][0..copy_len], tag[0..copy_len]);
        r.len += copy_len;
    }
    return r;
}
