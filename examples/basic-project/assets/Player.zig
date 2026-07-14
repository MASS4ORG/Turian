const std = @import("std");
const engine = @import("engine");

const log = std.log.scoped(.player);

pub const Player = struct {
    pub const is_component = true;

    health: i32 = 100,
    speed: f32 = 4.5,

    _print_timer: f32 = 0,

    pub fn awake(self: *@This()) void {
        _ = self;
        log.debug("awake", .{});
    }

    pub fn enable(self: *@This()) void {
        _ = self;
        log.debug("enable", .{});
    }

    pub fn start(self: *@This()) void {
        _ = self;
        log.debug("start", .{});
    }

    pub fn update(self: *@This(), time: engine.Time) void {
        self._print_timer += time.delta;
        if (self._print_timer >= 1.0) {
            self._print_timer -= 1.0;
            const fps = if (time.delta > 0) 1.0 / time.delta else 0.0;
            log.info("fps={d:.1}  frame={d}", .{ fps, time.frame });
        }
    }

    pub fn disable(self: *@This()) void {
        _ = self;
        log.debug("disable", .{});
    }

    pub fn destroy(self: *@This()) void {
        _ = self;
        log.debug("destroy", .{});
    }
};
