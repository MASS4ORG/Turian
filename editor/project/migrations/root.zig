//! Hand-maintained migration registry (ADR-0012), same explicit-registration
//! convention as `engine.scene.Component`'s builtin list — no comptime
//! directory scanning. Add new entries in ascending `to_version` order.
const std = @import("std");

pub const MigrationApi = @import("MigrationApi.zig");
pub const Migration = MigrationApi.Migration;
pub const Context = MigrationApi.Context;
pub const Direction = MigrationApi.Direction;
pub const RunResult = MigrationApi.RunResult;
pub const classify = MigrationApi.classify;
pub const pendingFor = MigrationApi.pendingFor;
pub const run = MigrationApi.run;

pub const all = [_]Migration{
    @import("v3_0_0.zig").migration,
};

test "all is sorted ascending by to_version" {
    var i: usize = 1;
    while (i < all.len) : (i += 1) {
        try std.testing.expect(all[i - 1].to_version.order(all[i].to_version) == .lt);
    }
}

test {
    std.testing.refAllDecls(@This());
}
