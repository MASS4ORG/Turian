global using System.Numerics;
global using BepuPhysics;
global using BepuPhysics.Collidables;
global using BepuPhysics.CollisionDetection;
global using BepuPhysics.Constraints;
global using BepuPhysics.Trees;
global using BepuUtilities;
global using BepuUtilities.Memory;
global using BepuBodyHandle = BepuPhysics.BodyHandle;
global using BepuMesh = BepuPhysics.Collidables.Mesh;
global using BepuBox = BepuPhysics.Collidables.Box;
global using BepuSphere = BepuPhysics.Collidables.Sphere;
global using BepuCapsule = BepuPhysics.Collidables.Capsule;
global using BepuCylinder = BepuPhysics.Collidables.Cylinder;

[assembly: Turian.Engine.Physics.PhysicsWorldFactory(typeof(Turian.Engine.Physics.BepuPhysicsWorldFactory))]
