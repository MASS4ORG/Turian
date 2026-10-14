namespace Turian.Engine.Physics;

/// <summary>Simulation settings captured when a world is created.</summary>
public sealed record PhysicsWorldSettings
{
    /// <summary>The initial gravity.</summary>
    public Vector3 Gravity { get; init; } = new(0f, -9.81f, 0f);

    /// <summary>Velocity iterations per substep; more makes stacks and joints stiffer.</summary>
    public int SolverIterations { get; init; } = 4;

    /// <summary>Solver substeps per step; more improves stability at a proportional cost.</summary>
    public int Substeps { get; init; } = 4;

    /// <summary>Spreads each step over worker threads.</summary>
    public bool Multithreaded { get; init; }

    /// <summary>Repeats results on the same machine and inputs, at some cost.</summary>
    public bool Deterministic { get; init; }
}

/// <summary>Creates worlds for one physics engine.</summary>
public interface IPhysicsWorldFactory
{
    /// <summary>Creates an empty world.</summary>
    IPhysicsWorld Create(PhysicsWorldSettings settings);
}

/// <summary>Names the world factory a loaded backend brick provides.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class PhysicsWorldFactoryAttribute(Type factoryType) : Attribute
{
    /// <summary>The factory type, with a public parameterless constructor.</summary>
    public Type FactoryType { get; } = factoryType;
}

/// <summary>Finds the physics backend among the loaded bricks.</summary>
public static class PhysicsWorldFactories
{
    /// <summary>Returns the first declared factory, or null when no backend brick is loaded.</summary>
    public static IPhysicsWorldFactory? Find()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic &&
                assembly.GetCustomAttributes(typeof(PhysicsWorldFactoryAttribute), false) is
                    [PhysicsWorldFactoryAttribute attribute, ..])
            {
                return (IPhysicsWorldFactory)Activator.CreateInstance(attribute.FactoryType)!;
            }
        }

        return null;
    }
}
