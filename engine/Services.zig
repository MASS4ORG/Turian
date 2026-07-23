//! Type-keyed service registry (ADR 0001). Engine subsystems and user-defined
//! services are registered by Zig type on a scoped `Services` instance owned
//! by the host (game `main`), avoiding globals and init-order hazards.

const std = @import("std");

pub const Services = struct {
    /// Maximum number of distinct services registered at once.
    pub const MAX = 64;

    const Entry = struct { id: usize, ptr: *anyopaque };

    entries: [MAX]Entry = undefined,
    count: usize = 0,

    pub fn init() Services {
        return .{};
    }

    /// Stable, unique id per type. `@typeName(T)` is a distinct comptime string per
    /// type; its pointer is a stable address in the binary, so it makes a perfect key
    /// with no manual registration or collisions.
    fn typeId(comptime T: type) usize {
        return @intFromPtr(@typeName(T).ptr);
    }

    /// Register (or replace) the instance providing service `T`. The pointer is
    /// borrowed — the caller owns the storage and must outlive the registry's use.
    pub fn register(self: *Services, comptime T: type, ptr: *T) void {
        const id = typeId(T);
        for (self.entries[0..self.count]) |*e| {
            if (e.id == id) {
                e.ptr = ptr;
                return;
            }
        }
        std.debug.assert(self.count < MAX);
        self.entries[self.count] = .{ .id = id, .ptr = @ptrCast(ptr) };
        self.count += 1;
    }

    /// Fetch the instance providing service `T`, or null if none is registered.
    pub fn get(self: *const Services, comptime T: type) ?*T {
        const id = typeId(T);
        for (self.entries[0..self.count]) |e| {
            if (e.id == id) return @ptrCast(@alignCast(e.ptr));
        }
        return null;
    }

    /// True if a provider for `T` is registered.
    pub fn has(self: *const Services, comptime T: type) bool {
        return self.get(T) != null;
    }
};

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test "register, fetch, and mutate a user service" {
    const Translation = struct { locale: [8]u8 = undefined, count: u32 = 0 };
    const Clock = struct { ticks: u64 = 0 };

    var services = Services.init();
    var tr = Translation{ .count = 3 };
    var clk = Clock{ .ticks = 99 };

    try std.testing.expect(!services.has(Translation));
    services.register(Translation, &tr);
    services.register(Clock, &clk);

    // Distinct types resolve to distinct instances.
    try std.testing.expect(services.has(Translation));
    try std.testing.expectEqual(@as(u32, 3), services.get(Translation).?.count);
    try std.testing.expectEqual(@as(u64, 99), services.get(Clock).?.ticks);

    // Returned pointer aliases the registered storage.
    services.get(Translation).?.count += 1;
    try std.testing.expectEqual(@as(u32, 4), tr.count);

    // Unregistered type is null, never a crash.
    const Unknown = struct {};
    try std.testing.expect(services.get(Unknown) == null);
}

test "register replaces an existing provider in place" {
    const Svc = struct { v: i32 };
    var services = Services.init();
    var a = Svc{ .v = 1 };
    var b = Svc{ .v = 2 };
    services.register(Svc, &a);
    services.register(Svc, &b);
    try std.testing.expectEqual(@as(usize, 1), services.count);
    try std.testing.expectEqual(@as(i32, 2), services.get(Svc).?.v);
}
