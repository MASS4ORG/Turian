//! Turian MCP server: Model Context Protocol adapter over the
//! Remote Debug Protocol. Exposes engine runtime state as MCP tools
//! so Claude Code, Cursor, and other MCP clients can inspect live games.
//!
//! Wire: JSON-RPC 2.0 over stdio (newline-delimited), MCP version 2024-11-05.
//!
//! To connect from Claude Code, add to your MCP settings:
//! ```json
//! {
//!   "turian": {
//!     "command": "turian-cli",
//!     "args": ["mcp"]
//!   }
//! }
//! ```
//! The game must be running with the debug server enabled (port 7777).

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
