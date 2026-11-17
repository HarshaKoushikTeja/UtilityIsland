namespace DynamicIsland.Core;

/// <summary>
/// A damped spring. Slightly under-damped so the pill overshoots a touch
/// and settles, which is what gives the island its feel.
/// </summary>
internal sealed class Spring
{
    private const double Stiffness = 260;
    private const double Damping = 24;

    public double Value;
    public double Velocity;
    public double Target;

    public Spring(double initial)
    {
        Value = initial;
        Target = initial;
    }

    /// <summary>Advance by dt seconds. Returns true while still moving.</summary>
    public bool Step(double dt)
    {
        if (Value == Target && Velocity == 0) return false;

        double accel = -Stiffness * (Value - Target) - Damping * Velocity;
        Velocity += accel * dt;
        Value += Velocity * dt;

        if (Math.Abs(Value - Target) < 0.05 && Math.Abs(Velocity) < 0.5)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }
        return true;
    }
}
