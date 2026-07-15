const std = @import("std");
const editor = @import("editor");
const engine = @import("engine");

pub fn printUsageI18n() void {
    std.debug.print(
        \\Usage:  turian-cli i18n <subcommand> [args]
        \\
        \\Subcommands:
        \\  extract <src-dir> <locale> <out.strings.json>
        \\      Walk <src-dir> for tr/trc/trn/trKey call sites and write (or
        \\      update, preserving existing translations) a .strings file for
        \\      <locale>.
        \\  compile <in.strings.json> <out.strtab>
        \\      Bake a .strings file down to a compiled .strtab binary.
        \\
    , .{});
}

pub fn cmdI18n(io: std.Io, gpa: std.mem.Allocator, sub: []const u8, args: *std.process.Args.Iterator) !void {
    if (std.mem.eql(u8, sub, "extract")) return cmdExtract(io, gpa, args);
    if (std.mem.eql(u8, sub, "compile")) return cmdCompile(io, gpa, args);
    printUsageI18n();
    return error.UnknownSubcommand;
}

fn cmdExtract(io: std.Io, gpa: std.mem.Allocator, args: *std.process.Args.Iterator) !void {
    const src_dir = args.next() orelse return printUsageI18n();
    const locale = args.next() orelse return printUsageI18n();
    const out_path = args.next() orelse return printUsageI18n();

    var extracted: std.ArrayList(editor.i18n.Extractor.ExtractedUnit) = .empty;
    defer {
        for (extracted.items) |u| u.deinit(gpa);
        extracted.deinit(gpa);
    }
    try editor.i18n.Extractor.extractDir(io, gpa, src_dir, &extracted);
    std.mem.sort(editor.i18n.Extractor.ExtractedUnit, extracted.items, {}, struct {
        fn lessThan(_: void, a: editor.i18n.Extractor.ExtractedUnit, b: editor.i18n.Extractor.ExtractedUnit) bool {
            return std.mem.order(u8, a.id, b.id) == .lt;
        }
    }.lessThan);

    var existing: ?engine.Strings = blk: {
        var file = std.Io.Dir.cwd().openFile(io, out_path, .{}) catch break :blk null;
        defer file.close(io);
        var fbuf: [4096]u8 = undefined;
        var reader = file.reader(io, &fbuf);
        const content = reader.interface.allocRemaining(gpa, .unlimited) catch break :blk null;
        defer gpa.free(content);
        break :blk engine.Strings.loadFromBytes(gpa, content) catch null;
    };
    defer if (existing) |*e| e.deinit(gpa);

    var merged = try editor.i18n.Compiler.mergeExtracted(gpa, existing, extracted.items, locale);
    defer merged.deinit(gpa);

    try merged.save(gpa, io, out_path);

    var new_count: usize = 0;
    for (merged.units) |u| {
        if (u.state == .new) new_count += 1;
    }
    std.debug.print("Extracted {d} unit(s) ({d} new) from {s} -> {s}\n", .{ merged.units.len, new_count, src_dir, out_path });
}

fn cmdCompile(io: std.Io, gpa: std.mem.Allocator, args: *std.process.Args.Iterator) !void {
    const in_path = args.next() orelse return printUsageI18n();
    const out_path = args.next() orelse return printUsageI18n();

    var file = try std.Io.Dir.cwd().openFile(io, in_path, .{});
    defer file.close(io);
    var fbuf: [4096]u8 = undefined;
    var reader = file.reader(io, &fbuf);
    const content = try reader.interface.allocRemaining(gpa, .unlimited);
    defer gpa.free(content);

    var strings = try engine.Strings.loadFromBytes(gpa, content);
    defer strings.deinit(gpa);

    const bytes = try editor.i18n.Compiler.compile(gpa, strings);
    defer gpa.free(bytes);

    try std.Io.Dir.cwd().writeFile(io, .{ .sub_path = out_path, .data = bytes });

    var translated: usize = 0;
    for (strings.units) |u| if (u.target.len != 0) {
        translated += 1;
    };
    std.debug.print("Compiled {d} translated unit(s) ({s}, {d} total in source) -> {s}\n", .{ translated, strings.locale, strings.units.len, out_path });
}
