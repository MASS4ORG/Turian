const engine = @import("engine");

/// Shared configuration for an enemy archetype.
/// Create instances from the Asset Browser → Create ▸ Gameplay ▸ Enemy Stats.
pub const EnemyStats = struct {
    pub const is_data_asset = true;
    /// Where this shows up in the Asset Browser's cascaded Create menu
    /// (issues #85/#72). Without this, it would fall back to
    /// "Data/EnemyStats".
    pub const menu_path = "Gameplay/Enemy Stats";

    max_health: f32 = 100,
    move_speed: f32 = 5,
    damage: i32 = 10,
    /// Reference to the material used for this enemy archetype.
    material: engine.api.TypedAssetRef(.material) = .{},
};
