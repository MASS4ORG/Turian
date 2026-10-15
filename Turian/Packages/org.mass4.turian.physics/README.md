# 3D Physics

Enable `org.mass4.turian.physics` to use `Turian.Engine.Physics`: Unity-style rigid bodies, colliders, physics materials and queries. This brick holds the components and the engine-neutral backend contract; it simulates nothing on its own. Install a backend brick such as [`org.mass4.turian.physics.bepu`](../org.mass4.turian.physics.bepu/) to make bodies move. Without one, components register harmlessly, nothing falls and every query misses.

## Components

- `RigidbodyComponent` puts a node under physics control: mass, damping, gravity scale, kinematic mode, interpolation, continuous detection, frozen axes (`Constraints`) and center of mass. Forces use Unity's `ForceMode`; `MovePosition` and `MoveRotation` move a kinematic body over the next fixed tick so it pushes what it meets.
- `BoxColliderComponent`, `SphereColliderComponent`, `CapsuleColliderComponent` and `CylinderColliderComponent`. A collider joins the nearest enabled rigidbody on its node or an ancestor; several colliders on one body form a compound. A collider without a rigidbody is static.
- `PhysicsMaterial` is a data asset with friction, bounciness and Unity's combine modes.

## Settings and stepping

Create **Settings / Physics** under `Assets/Settings` to change gravity, solver iterations and substeps, the default material, the layer collision matrix, the sleep threshold, threading and determinism. `SimulationMode.FixedUpdate`, the default, steps once per fixed tick after the components' `OnFixedUpdate`; `Update` steps once per frame; `Script` steps only when a script calls `PhysicsScene.Simulate`. Interpolated bodies blend their last two simulated poses between fixed ticks.

## Queries

Inject `PhysicsScene` with `[InjectService(Optional = true)]` to call `Raycast`, `RaycastAll`, `SphereCast`, `BoxCast`, `CapsuleCast`, the `Overlap*` and `Check*` methods and `ComputePenetration`, with layer masks and `QueryTriggerInteraction`. The brick also serves core's `IPhysicsQueries`, so engine code that queries physics without naming this brick sees the same colliders.

## Collisions and layers

Components implementing `ICollisionHandler` or `ITriggerHandler` on a collider's node, or on its rigidbody's node, receive Enter, Stay and Exit callbacks. `IgnoreLayerCollision` and `IgnoreCollision` adjust contacts for the running session. A collider's `IncludeLayers` and `ExcludeLayers` refine the layer matrix; contacts are checked both ways, so either side's filter can reject a pair.

## Limitations

Mesh colliders, joint components, the character controller and wheel colliders are not available yet. The backend contract already describes meshes and joints, so they arrive without changing it.
