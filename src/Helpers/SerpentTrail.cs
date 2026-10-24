using System;

namespace UnderwaterHorrors;

/// <summary>
/// The path a giant serpent's mouth has swum, kept as breadcrumbs one
/// <see cref="Spacing"/> apart, newest last. The body is laid along it: the
/// point s blocks behind the mouth sits where the mouth was s blocks of
/// travel ago, so the body follows the head like a snake's.
///
/// Shared by the client (posing the spine, EntityBehaviorGiantSerpentBody)
/// and the server (placing hit proxies), so both lay the body the same way.
///
/// Every crumb also stores the mouth's odometer when it was dropped. The
/// side-to-side swimming wave is keyed to it (<see cref="WaveOffset"/>), so
/// the wave stays fixed in the water and the body slides through it, the
/// way a snake's whole body follows one S-shaped track.
/// </summary>
public class SerpentTrail
{
    public const double Spacing = 1.0;

    // A mouth that moves farther than this in one update was teleported
    // (or first seen); the trail restarts instead of drawing a long
    // straight streak to the new spot.
    public const double JumpReset = 64.0;

    private readonly int capacity;
    private readonly double[] xs, ys, zs, odos;
    private int newest = -1;
    private int count;

    /// <summary>The mouth: ahead of the newest crumb by less than Spacing.</summary>
    public double HeadX, HeadY, HeadZ, HeadOdo;

    public bool Initialized => count > 0;

    public SerpentTrail(double length)
    {
        capacity = (int)Math.Ceiling(length / Spacing) + 16;
        xs = new double[capacity];
        ys = new double[capacity];
        zs = new double[capacity];
        odos = new double[capacity];
    }

    private void Add(double x, double y, double z, double odo)
    {
        newest = (newest + 1) % capacity;
        xs[newest] = x;
        ys[newest] = y;
        zs[newest] = z;
        odos[newest] = odo;
        if (count < capacity) count++;
    }

    /// <summary>A straight body behind the mouth, facing yaw.</summary>
    public void Reset(double x, double y, double z, double yaw, double odo)
    {
        count = 0;
        newest = -1;
        double fx = Math.Sin(yaw), fz = Math.Cos(yaw);
        for (int i = capacity - 1; i >= 1; i--)
        {
            double back = i * Spacing;
            Add(x - fx * back, y, z - fz * back, odo - back);
        }
        HeadX = x;
        HeadY = y;
        HeadZ = z;
        HeadOdo = odo;
    }

    /// <summary>Moves the mouth, dropping crumbs every Spacing along the way.</summary>
    public void Push(double x, double y, double z, double yaw)
    {
        if (!Initialized)
        {
            Reset(x, y, z, yaw, 0);
            return;
        }

        double jx = x - HeadX, jy = y - HeadY, jz = z - HeadZ;
        if (jx * jx + jy * jy + jz * jz > JumpReset * JumpReset)
        {
            Reset(x, y, z, yaw, HeadOdo);
            return;
        }

        HeadOdo += Math.Sqrt(jx * jx + jy * jy + jz * jz);
        HeadX = x;
        HeadY = y;
        HeadZ = z;

        while (true)
        {
            double dx = HeadX - xs[newest], dy = HeadY - ys[newest], dz = HeadZ - zs[newest];
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d < Spacing) break;
            double t = Spacing / d;
            Add(xs[newest] + dx * t, ys[newest] + dy * t, zs[newest] + dz * t, odos[newest] + Spacing);
        }
    }

    /// <summary>
    /// Samples the trail at increasing arc lengths behind the mouth (arcs[0]
    /// is usually 0, the mouth itself). Returns the position, the odometer
    /// there, and the horizontal travel direction (pointing toward the head).
    /// Past the oldest crumb the trail runs on straight.
    /// </summary>
    public void SampleMany(double[] arcs, int n,
        double[] outX, double[] outY, double[] outZ, double[] outOdo, double[] outTx, double[] outTz)
    {
        // Walk segment by segment from the mouth back. Segment k runs from
        // point "a" (head side) to point "b" (tail side).
        double ax = HeadX, ay = HeadY, az = HeadZ, aOdo = HeadOdo;
        int idx = newest;
        int left = count;
        double segStart = 0;
        double bx = xs[idx], by = ys[idx], bz = zs[idx], bOdo = odos[idx];
        double segLen = Dist(ax, ay, az, bx, by, bz);
        double tx = 0, tz = 1;
        SetTangent(ax - bx, az - bz, ref tx, ref tz);

        for (int i = 0; i < n; i++)
        {
            double s = arcs[i];
            while (s > segStart + segLen && left > 1)
            {
                segStart += segLen;
                ax = bx; ay = by; az = bz; aOdo = bOdo;
                idx = (idx - 1 + capacity) % capacity;
                left--;
                bx = xs[idx]; by = ys[idx]; bz = zs[idx]; bOdo = odos[idx];
                segLen = Dist(ax, ay, az, bx, by, bz);
                SetTangent(ax - bx, az - bz, ref tx, ref tz);
            }

            double u = segLen > 1e-9 ? (s - segStart) / segLen : 0;
            if (s > segStart + segLen)
            {
                // Out of crumbs: carry on straight past the oldest one.
                double over = s - segStart - segLen;
                double ux = segLen > 1e-9 ? (bx - ax) / segLen : -tx;
                double uy = segLen > 1e-9 ? (by - ay) / segLen : 0;
                double uz = segLen > 1e-9 ? (bz - az) / segLen : -tz;
                outX[i] = bx + ux * over;
                outY[i] = by + uy * over;
                outZ[i] = bz + uz * over;
                outOdo[i] = bOdo - over;
            }
            else
            {
                outX[i] = ax + (bx - ax) * u;
                outY[i] = ay + (by - ay) * u;
                outZ[i] = az + (bz - az) * u;
                outOdo[i] = aOdo + (bOdo - aOdo) * u;
            }
            outTx[i] = tx;
            outTz[i] = tz;
        }
    }

    private double[] smoothArcs, smoothX, smoothY, smoothZ, smoothO, smoothTx, smoothTz;

    /// <summary>
    /// Like <see cref="SampleMany"/>, but the horizontal direction at each
    /// sample is taken across the points <paramref name="delta"/> before and
    /// after it. SampleMany's direction is that of the single breadcrumb
    /// segment the sample sits on, so it jumps whenever a sample crosses onto
    /// the next segment; anything offset sideways along it (the swimming
    /// wave) twitched every time. delta must be under half the smallest gap
    /// between the arcs, so the widened list stays in order.
    /// </summary>
    public void SampleSmooth(double[] arcs, int n, double delta,
        double[] outX, double[] outY, double[] outZ, double[] outOdo, double[] outTx, double[] outTz)
    {
        int m = n * 3;
        if (smoothArcs == null || smoothArcs.Length < m)
        {
            smoothArcs = new double[m]; smoothX = new double[m]; smoothY = new double[m]; smoothZ = new double[m];
            smoothO = new double[m]; smoothTx = new double[m]; smoothTz = new double[m];
        }
        for (int i = 0; i < n; i++)
        {
            smoothArcs[i * 3] = Math.Max(0, arcs[i] - delta);
            smoothArcs[i * 3 + 1] = arcs[i];
            smoothArcs[i * 3 + 2] = arcs[i] + delta;
        }
        SampleMany(smoothArcs, m, smoothX, smoothY, smoothZ, smoothO, smoothTx, smoothTz);

        for (int i = 0; i < n; i++)
        {
            int c = i * 3 + 1;
            outX[i] = smoothX[c];
            outY[i] = smoothY[c];
            outZ[i] = smoothZ[c];
            outOdo[i] = smoothO[c];
            double tx = smoothTx[c], tz = smoothTz[c];
            SetTangent(smoothX[c - 1] - smoothX[c + 1], smoothZ[c - 1] - smoothZ[c + 1], ref tx, ref tz);
            outTx[i] = tx;
            outTz[i] = tz;
        }
    }

    private static void SetTangent(double dx, double dz, ref double tx, ref double tz)
    {
        double h = Math.Sqrt(dx * dx + dz * dz);
        // A vertical stretch (a breach) has no horizontal heading of its
        // own; keep the last one so the wave's side direction stays put.
        if (h < 1e-6) return;
        tx = dx / h;
        tz = dz / h;
    }

    private static double Dist(double ax, double ay, double az, double bx, double by, double bz)
    {
        double dx = ax - bx, dy = ay - by, dz = az - bz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Sideways offset of the swimming wave at arc s, given the odometer
    /// there. Zero at the mouth so the head glides straight, full strength
    /// from about a fifth of the body back, a little wider toward the tail.
    /// </summary>
    public static double WaveOffset(double s, double odo, double amplitude, double wavelength, double bodyLength)
    {
        if (amplitude <= 0 || wavelength <= 0 || bodyLength <= 0) return 0;
        double ramp = Math.Clamp(s / (0.2 * bodyLength), 0, 1);
        ramp = ramp * ramp * (3 - 2 * ramp);
        double grow = 1 + 0.5 * s / bodyLength;
        return amplitude * ramp * grow * Math.Sin(2 * Math.PI * odo / wavelength);
    }

    /// <summary>
    /// A coarse copy of the trail for clients that start watching a serpent
    /// mid-swim: [headOdo, then x,y,z relative to the mouth every
    /// seedSpacing blocks back]. Written by the server, read once per client.
    /// </summary>
    public float[] ToSeed(double length, double seedSpacing)
    {
        int points = (int)(length / seedSpacing) + 1;
        var arcs = new double[points];
        for (int i = 0; i < points; i++) arcs[i] = i * seedSpacing;
        var x = new double[points]; var y = new double[points]; var z = new double[points];
        var o = new double[points]; var tx = new double[points]; var tz = new double[points];
        SampleMany(arcs, points, x, y, z, o, tx, tz);

        var seed = new float[1 + points * 3];
        seed[0] = (float)HeadOdo;
        for (int i = 0; i < points; i++)
        {
            seed[1 + i * 3] = (float)(x[i] - HeadX);
            seed[2 + i * 3] = (float)(y[i] - HeadY);
            seed[3 + i * 3] = (float)(z[i] - HeadZ);
        }
        return seed;
    }

    /// <summary>Rebuilds the trail from a server seed, with the mouth at (x, y, z).</summary>
    public bool FromSeed(float[] seed, double seedSpacing, double x, double y, double z)
    {
        if (seed == null || seed.Length < 7) return false;
        int points = (seed.Length - 1) / 3;
        double headOdo = seed[0];
        double maxS = (points - 1) * seedSpacing;

        count = 0;
        newest = -1;
        int crumbs = Math.Min(capacity - 1, (int)Math.Floor(maxS / Spacing));
        for (int c = crumbs; c >= 1; c--)
        {
            double s = c * Spacing;
            int k = Math.Min(points - 2, (int)(s / seedSpacing));
            double u = (s - k * seedSpacing) / seedSpacing;
            double px = seed[1 + k * 3] + (seed[4 + k * 3] - seed[1 + k * 3]) * u;
            double py = seed[2 + k * 3] + (seed[5 + k * 3] - seed[2 + k * 3]) * u;
            double pz = seed[3 + k * 3] + (seed[6 + k * 3] - seed[3 + k * 3]) * u;
            Add(x + px, y + py, z + pz, headOdo - s);
        }
        if (count == 0) return false;
        HeadX = x;
        HeadY = y;
        HeadZ = z;
        HeadOdo = headOdo;
        return true;
    }
}
