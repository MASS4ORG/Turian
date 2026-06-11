const engine = @import("engine");

/// Shared configuration for an enemy archetype.
/// Create instances from the Asset Browser → "New EnemyStats".
pub const EnemyStats = struct {
    pub const is_data_asset = true;

    max_health: f32 = 100,
    move_speed: f32 = 5,
    damage: i32 = 10,
    /// Reference to the material used for this enemy archetype.
    material: engine.api.TypedAssetRef(.material) = .{},
};
