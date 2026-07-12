const std = @import("std");
const rdebug = @import("debug");
const rmcp = @import("mcp");

pub fn cmdMcp(io: std.Io, gpa: std.mem.Allocator, args: *std.process.Args.Iterator) !void {
    var host_buf: [256]u8 = std.mem.zeroes([256]u8);
    @memcpy(host_buf[0..9], "127.0.0.1");
    var host_len: usize = 9;
    var port: u16 = rdebug.Protocol.DEFAULT_PORT;
    var token: []const u8 = "";

    while (args.next()) |a| {
        if (std.mem.eql(u8, a, "--host")) {
            const v = args.next() orelse return error.MissingArg;
            const n = @min(v.len, host_buf.len - 1);
            @memcpy(host_buf[0..n], v[0..n]);
            host_len = n;
        } else if (std.mem.eql(u8, a, "--port")) {
            const v = args.next() orelse return error.MissingArg;
            port = std.fmt.parseInt(u16, v, 10) catch return error.InvalidPort;
        } else if (std.mem.eql(u8, a, "--token")) {
            token = args.next() orelse return error.MissingArg;
        }
    }

    std.debug.print("[turian-mcp] connecting to {s}:{d}\n", .{ host_buf[0..host_len], port });
    try rmcp.run(io, gpa, .{
        .host = host_buf[0..host_len],
        .port = port,
        .token = token,
    });
}
