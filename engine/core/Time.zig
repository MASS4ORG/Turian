/// Frame timing data passed to user script update hooks.
pub const Time = struct {
    /// Seconds since the last frame.
    delta: f32,
    /// Total seconds since the scene started.
    elapsed: f32,
    /// Monotonically increasing frame counter.
    frame: u64,

    /// Ceiling applied to `delta` before scripts see it. A hitch, a breakpoint
    /// or a scene load otherwise hands a script most of a second in one step,
    /// which teleports anything integrating it; capping trades a little
    /// slow-motion for staying on the rails. Ten frames per second.
    pub const MAX_DELTA: f32 = 0.1;
};
