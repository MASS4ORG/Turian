//! Turian MCP server: Model Context Protocol adapter over the Remote Debug
//! Protocol. Exposes engine runtime state as MCP tools for Claude Code, Cursor,
//! and other MCP clients. JSON-RPC 2.0 over stdio.

/// Protocol framing: request parsing + response writers.
pub const Protocol = @import("Protocol.zig");
/// Tool registry: definitions and debug-method mapping.
pub const Tools = @import("Tools.zig");
/// Tool entry type.
pub const Tool = Tools.Tool;
/// MCP server run options.
pub const ServerOptions = @import("Server.zig").Options;
/// Run the MCP stdio server. Blocks until stdin closes.
pub const run = @import("Server.zig").run;

test {
    @import("std").testing.refAllDecls(@This());
}
