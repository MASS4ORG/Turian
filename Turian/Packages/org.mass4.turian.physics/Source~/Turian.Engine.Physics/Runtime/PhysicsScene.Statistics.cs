using System.Diagnostics;

namespace Turian.Engine.Physics;

/// <summary>A snapshot of the physics scene's size and cost, for profiling overlays and benchmarks.</summary>
/// <param name="Bodies">Backend bodies, static ones included.</param>
/// <param name="StaticBodies">Bodies built for colliders without a rigidbody.</param>
/// <param name="SleepingBodies">Dynamic and kinematic bodies currently asleep.</param>
/// <param name="Joints">Joints created in the backend.</param>
/// <param name="ContactPairs">Collider pairs touching after the last step.</param>
/// <param name="Steps">Steps taken since the scene started.</param>
/// <param name="StepMilliseconds">The backend's time for the last step.</param>
/// <param name="SimulateMilliseconds">The last step's total time: sync, step, pose write-back and callbacks.</param>
public readonly record struct PhysicsStatistics(
    int Bodies,
    int StaticBodies,
    int SleepingBodies,
    int Joints,
    int ContactPairs,
    long Steps,
    double StepMilliseconds,
    double SimulateMilliseconds)
{
    /// <summary>Dynamic and kinematic bodies currently awake.</summary>
    public int AwakeBodies => Bodies - StaticBodies - SleepingBodies;
}

public sealed partial class PhysicsScene
{
    long steps;
    long stepTicks;
    long simulateTicks;
    int contactPairs;

    /// <summary>The scene's current size and the cost of its last step; body states are counted when read.</summary>
    public PhysicsStatistics Statistics
    {
        get
        {
            var statics = 0;
            var sleeping = 0;
            foreach (var record in byHandle.Values)
            {
                if (record.Motion == MotionType.Static)
                {
                    statics++;
                }
                else if (!World!.IsAwake(record.Handle))
                {
                    sleeping++;
                }
            }

            return new PhysicsStatistics(byHandle.Count, statics, sleeping, JointCount, contactPairs, steps,
                Milliseconds(stepTicks), Milliseconds(simulateTicks));
        }
    }

    static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    static int TouchingPairs(ReadOnlySpan<ContactEvent> contacts)
    {
        var touching = 0;
        foreach (ref readonly var contact in contacts)
        {
            if (contact.State != PhysicsEventState.Exit)
            {
                touching++;
            }
        }

        return touching;
    }
}
