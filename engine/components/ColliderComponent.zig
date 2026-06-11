/// Collision shape attached to a rigid body.
pub const ColliderComponent = struct {
    pub const is_component = true;

    /// When true, the collider acts as a trigger (no physical response).
    is_trigger: bool = false,
};
