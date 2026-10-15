# 3D Physics: BepuPhysics backend

Enable `org.mass4.turian.physics.bepu` beside [`org.mass4.turian.physics`](../org.mass4.turian.physics/) to simulate its worlds with [BepuPhysics v2](https://github.com/bepu/bepuphysics2). The physics brick finds this backend on its own; no settings or code are needed.

## Packages

The brick's `nuget` map pins `BepuPhysics` and `BepuUtilities` at `2.5.0-beta.33`. Both are pure managed code. `brick sync-precast` copies them beside the brick's assembly for the editor, and exported games receive them as `PackageReference` items. See [brick NuGet dependencies](../../../docs/bricks/nuget.md).

## Performance

`PhysicsSettings.Multithreaded` spreads each step over one worker per processor. `SolverIterations` and `Substeps` trade cost for stiffer stacks and joints; the cost grows with their product. Bodies at rest fall asleep below `SleepThreshold` and cost almost nothing until touched.

## Limitations

- Bounciness is approximated with contact springs, so rebounds are close to Unity's but not identical.
- Triangle meshes are one-sided, and only static or kinematic bodies may use them; a mesh on a dynamic body is ignored with a warning.
- Determinism is best effort and holds only on the same machine.
- A compound body uses the material of its first solid collider for every contact.
- A sweep that hits a compound reports the child nearest the hit point.
- `IncludeLayers` and the layer matrix are checked both ways: a pair collides only when each side accepts the other.
