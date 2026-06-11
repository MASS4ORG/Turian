/// Frame timing data passed to user script update hooks.
pub const Time = struct {
    /// Seconds since the last frame.
    delta: f32,
    /// Total seconds since the scene started.
    elapsed: f32,
    /// Monotonically increasing frame counter.
    frame: u64,
};
