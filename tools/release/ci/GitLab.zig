/// GitLab CI provider plugin.
///
/// Required environment variables (set automatically by GitLab CI):
///   CI_API_V4_URL, CI_PROJECT_ID, CI_COMMIT_TAG
/// Auth (first one set wins): CI_PUSH_TOKEN (project access token, scopes
/// write_repository + api) > CI_JOB_TOKEN (auto-issued, no setup) > GITLAB_TOKEN.
///
/// To add a new provider, create a sibling file with the same
/// `pub fn run(io, gpa, environ)` entry point.
const std = @import("std");
const common = @import("Common.zig");

// ── JSON payload types (serialised with std.json.stringify, no jq needed) ────

const LinkJson = struct { name: []const u8, url: []const u8 };
const AssetsJson = struct { links: []const LinkJson };
const ReleaseJson = struct {
    name: []const u8,
    tag_name: []const u8,
    description: []const u8,
    assets: AssetsJson,
};

// ── Entry point ───────────────────────────────────────────────────────────────

/// Publish the release to GitLab, uploading artifacts and creating the release.
pub fn run(
    io: std.Io,
    gpa: std.mem.Allocator,
    environ: *const std.process.Environ.Map,
) !void {
    const api_url = environ.get("CI_API_V4_URL") orelse {
        std.debug.print("error: CI_API_V4_URL not set\n", .{});
        return error.MissingEnv;
    };
    const project_id = environ.get("CI_PROJECT_ID") orelse {
        std.debug.print("error: CI_PROJECT_ID not set\n", .{});
        return error.MissingEnv;
    };
    const push_token = environ.get("CI_PUSH_TOKEN");
    const job_token = environ.get("CI_JOB_TOKEN");
    const generic_token = environ.get("GITLAB_TOKEN");

    const token = push_token orelse job_token orelse generic_token orelse {
        std.debug.print("error: no GitLab token found (CI_PUSH_TOKEN, CI_JOB_TOKEN, or GITLAB_TOKEN)\n", .{});
        return error.MissingEnv;
    };

    const auth_header = if (push_token == null and job_token != null) "JOB-TOKEN" else "PRIVATE-TOKEN";

    // If CI_COMMIT_TAG is set, we are in a tag pipeline (traditional flow or artifact upload)
    // If not, we might be creating the release early in check_release (needs .release-version)
    const commit_tag = environ.get("CI_COMMIT_TAG");
    const version = blk: {
        if (commit_tag) |tag| {
            break :blk if (tag.len > 0 and tag[0] == 'v') tag[1..] else tag;
        }
        const raw = readFile(io, gpa, ".release-version") catch {
            std.debug.print("error: neither CI_COMMIT_TAG nor .release-version found\n", .{});
            return error.MissingVersion;
        };
        defer gpa.free(raw);
        break :blk try gpa.dupe(u8, std.mem.trim(u8, raw, " \n\r\t"));
    };
    defer if (commit_tag == null) gpa.free(version);

    const tag_name = if (commit_tag) |t| t else try std.fmt.allocPrint(gpa, "v{s}", .{version});
    defer if (commit_tag == null) gpa.free(tag_name);

    // ── Mode detection ────────────────────────────────────────────────────────
    // 1. If .public/ has files: upload them and LINK to release
    // 2. Otherwise: create the release (just the tag + description)

    var artifacts: std.ArrayList(common.Artifact) = .empty;
    defer {
        for (artifacts.items) |a| {
            gpa.free(a.local_path);
            gpa.free(a.download_url);
        }
        artifacts.deinit(gpa);
    }

    if (std.Io.Dir.cwd().openDir(io, ".public", .{ .iterate = true })) |dist_dir| {
        defer dist_dir.close(io);
        var iter = dist_dir.iterate();
        while (iter.next(io) catch null) |entry| {
            if (entry.kind != .file) continue;
            if (entry.name[0] == '.') continue;

            const local_path = try std.fmt.allocPrint(gpa, ".public/{s}", .{entry.name});
            const dl_url = try std.fmt.allocPrint(gpa, "{s}/projects/{s}/packages/generic/turian/{s}/{s}", .{ api_url, project_id, version, entry.name });

            const plt = common.Platform.fromFileName(entry.name);
            try artifacts.append(gpa, .{
                .label = plt.label(),
                .local_path = local_path,
                .file_name = entry.name,
                .download_url = dl_url,
            });
        }
    } else |_| {}

    if (artifacts.items.len > 0) {
        // Mode: Artifact Upload + Linking
        for (artifacts.items) |art| {
            std.debug.print("[gitlab] uploading {s} …\n", .{art.file_name});
            const url = try std.fmt.allocPrint(gpa, "{s}/projects/{s}/packages/generic/turian/{s}/{s}", .{ api_url, project_id, version, art.file_name });
            defer gpa.free(url);
            const auth = try std.fmt.allocPrint(gpa, "{s}: {s}", .{ auth_header, token });
            defer gpa.free(auth);

            try spawnAndWait(io, gpa, &.{
                "curl",     "--fail-with-body", "--silent",      "--show-error",
                "--header", auth,               "--upload-file", art.local_path,
                url,
            });

            // Link to release
            const link_url = try std.fmt.allocPrint(gpa, "{s}/projects/{s}/releases/{s}/assets/links", .{ api_url, project_id, tag_name });
            defer gpa.free(link_url);
            const link_payload = try std.fmt.allocPrint(gpa, "{{\"name\":\"{s}\",\"url\":\"{s}\"}}", .{ art.label, art.download_url });
            defer gpa.free(link_payload);

            std.debug.print("[gitlab] linking {s} to release …\n", .{art.file_name});
            try spawnAndWait(io, gpa, &.{
                "curl",     "--fail-with-body", "--silent",  "--show-error",
                "--header", auth,               "--header",  "Content-Type: application/json",
                "--data",   link_payload,       "--request", "POST",
                link_url,
            });
        }
        return;
    }

    // Mode: Create Release
    const changelog = readFile(io, gpa, "CHANGELOG.md") catch "";
    defer if (changelog.len > 0) gpa.free(changelog);
    const notes = extractEntry(changelog, version);

    const payload = ReleaseJson{
        .name = tag_name,
        .tag_name = tag_name,
        .description = notes,
        .assets = .{ .links = &.{} },
    };

    var out: std.Io.Writer.Allocating = .init(gpa);
    defer out.deinit();
    var stringify: std.json.Stringify = .{
        .writer = &out.writer,
        .options = .{},
    };
    try stringify.write(payload);

    std.Io.Dir.cwd().createDirPath(io, ".public") catch {};
    try writeCwd(io, ".public/.release-payload.json", out.written());

    const release_url = try std.fmt.allocPrint(gpa, "{s}/projects/{s}/releases", .{ api_url, project_id });
    defer gpa.free(release_url);
    const auth = try std.fmt.allocPrint(gpa, "{s}: {s}", .{ auth_header, token });
    defer gpa.free(auth);

    std.debug.print("[gitlab] creating release {s} …\n", .{tag_name});
    try spawnAndWait(io, gpa, &.{
        "curl",     "--fail-with-body",               "--silent",  "--show-error",
        "--header", auth,                             "--header",  "Content-Type: application/json",
        "--data",   "@.public/.release-payload.json", release_url,
    });
    std.debug.print("[gitlab] release {s} created.\n", .{tag_name});
}

// ── Helpers ───────────────────────────────────────────────────────────────────

/// Returns a slice into `changelog` — no allocation, caller must not free separately.
fn extractEntry(changelog: []const u8, version: []const u8) []const u8 {
    var hdr_buf: [64]u8 = undefined;
    const hdr = std.fmt.bufPrint(&hdr_buf, "## [{s}]", .{version}) catch return "";
    const sec = std.mem.indexOf(u8, changelog, hdr) orelse return "";
    const nl = std.mem.indexOfScalarPos(u8, changelog, sec, '\n') orelse return "";
    const body = nl + 1;
    const next = std.mem.indexOfPos(u8, changelog, body, "\n## [") orelse changelog.len;
    return std.mem.trim(u8, changelog[body..next], "\n ");
}

fn spawnAndWait(io: std.Io, _: std.mem.Allocator, argv: []const []const u8) !void {
    var child = try std.process.spawn(io, .{ .argv = argv });
    const term = try child.wait(io);
    switch (term) {
        .exited => |code| if (code != 0) return error.ProcessFailed,
        else => return error.ProcessKilled,
    }
}

fn readFile(io: std.Io, gpa: std.mem.Allocator, path: []const u8) ![]u8 {
    var file = try std.Io.Dir.cwd().openFile(io, path, .{});
    defer file.close(io);
    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    return reader.interface.allocRemaining(gpa, .unlimited);
}

fn writeCwd(io: std.Io, path: []const u8, data: []const u8) !void {
    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = path, .data = data });
}
