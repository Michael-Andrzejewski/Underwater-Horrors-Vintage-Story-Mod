using System;
using Vintagestory.API.MathTools;

namespace UnderwaterHorrors;

public enum AnglerfishPhase { Lurking, Lunging, Recovering, Returning }

/// <summary>Server attack latch and timing, independent of rendering and tick frequency.</summary>
public sealed class AnglerfishAttackCycle
{
    // Defaults for the config's AnglerfishStrikeRange (blocks from the
    // mouth), AnglerfishBiteDamage and AnglerfishStrikeSpeed. At 28 blocks/s
    // a swoop from the edge of its range arrives as the bite clip snaps shut.
    public const double DefaultTriggerRange = 14;
    public const float Damage = 50;
    public const double LungeSpeed = 28; // blocks/second
    public double TriggerRange { get; set; } = DefaultTriggerRange;
    public const float MaxLungeSeconds = 2f;
    public const float CooldownSeconds = 6;
    public const double DiveSpeed = 12, PatrolSpeed = 2;
    public AnglerfishPhase Phase { get; private set; }
    public bool DamageDealt { get; private set; }
    public float Cooldown { get; private set; }
    private float elapsed;

    public bool TryBegin(double distanceToMouth)
    {
        if (!double.IsFinite(distanceToMouth) || distanceToMouth < 0 || distanceToMouth > TriggerRange
            || Cooldown > 0 || Phase == AnglerfishPhase.Lunging) return false;
        Phase = AnglerfishPhase.Lunging;
        DamageDealt = false;
        elapsed = 0;
        return true;
    }

    public bool TryHit()
    {
        if (Phase != AnglerfishPhase.Lunging || DamageDealt) return false;
        DamageDealt = true;
        return true;
    }

    public void Tick(float dt)
    {
        if (!float.IsFinite(dt) || dt <= 0) return;
        if (Phase == AnglerfishPhase.Lunging)
        {
            elapsed += dt;
            if (elapsed >= MaxLungeSeconds) Finish();
        }
        else
        {
            Cooldown = Math.Max(0, Cooldown - dt);
            if (Phase == AnglerfishPhase.Recovering && Cooldown <= 0) Phase = AnglerfishPhase.Returning;
        }
    }

    public void Finish()
    {
        if (Phase != AnglerfishPhase.Lunging) return;
        Phase = AnglerfishPhase.Recovering;
        Cooldown = CooldownSeconds;
    }

    public void ArriveHome() { if (Phase == AnglerfishPhase.Returning) Phase = AnglerfishPhase.Lurking; }

    // Same shortest-angle smoothing and angular-speed cap as the serpent.
    public static float SmoothYaw(float current,float target,float dt,float degreesPerSecond)
    {
        float difference=GameMath.AngleRadDistance(current,target);
        float limit=degreesPerSecond*GameMath.DEG2RAD*dt;
        return current+Math.Clamp(difference*Math.Min(1,dt*5),-limit,limit);
    }

    public static double DistanceToSegment(double px, double py, double pz,
        double ax, double ay, double az, double bx, double by, double bz)
    {
        double x=bx-ax, y=by-ay, z=bz-az, length=x*x+y*y+z*z;
        double t=length < 1e-12 ? 0 : Math.Clamp(((px-ax)*x+(py-ay)*y+(pz-az)*z)/length,0,1);
        return Math.Sqrt(Math.Pow(px-ax-t*x,2)+Math.Pow(py-ay-t*y,2)+Math.Pow(pz-az-t*z,2));
    }
}
