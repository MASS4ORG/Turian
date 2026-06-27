//! Remote Debug Protocol (issue #49): JSON-RPC 2.0 over TCP, backed by the
//! Runtime Introspection Layer. Separate from the engine module so games never
//! link the TCP server or JSON-RPC stack unless they explicitly opt in.

/// JSON-RPC 2.0 framing: request parsing, response helpers, error codes.
pub const Protocol = @import("Protocol.zig");
/// Method dispatch: maps JSON-RPC method names to introspect calls.
pub const Handler = @import("Handler.zig");
/// TCP server: background accept loop, multi-client, main-thread request pump.
pub const Server = @import("Server.zig").Server;
/// WorldProvider callback type (legacy convenience).
pub const WorldProvider = @import("Server.zig").WorldProvider;
/// Server options (port, localhost_only, auth_token, allow_write, …).
pub const ServerOptions = @import("Server.zig").Options;
/// A validated mutation handed to the host's applier on the main thread.
pub const Mutation = @import("Server.zig").Mutation;
/// Result of applying a mutation.
pub const MutationResult = @import("Server.zig").MutationResult;
/// Host-supplied mutation applier (runs on the main thread inside pump).
pub const MutationApplier = @import("Server.zig").MutationApplier;
/// TCP client: connects to a running server, sends JSON-RPC requests.
pub const Client = @import("Client.zig").Client;
/// Pretty-print a JSON-RPC response line to any writer.
pub const printResponse = @import("Client.zig").printResponse;

test {
    @import("std").testing.refAllDecls(@This());
}
