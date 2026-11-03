using System;
using System.Collections.Generic;
using System.Numerics;

namespace RealisticWinch;

// The rope's maths, kept free of Unity and the game so it can be unit tested. The world is
// reached only through IObstacles; RopeWrap adapts Unity physics to it.

public struct Hit
{
    public Vector3 Point, Normal;
    public Hit(Vector3 point, Vector3 normal) { Point = point; Normal = normal; }
}

public interface IObstacles
{
    // The first static obstacle along a ray, if any within maxDistance
    bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Hit hit);
    bool Overlaps(Vector3 point, float radius);
    // Height of the ground under (x, z), searching down from `from`; null if there's none within `depth`
    float? GroundBelow(Vector3 from, float depth);
}

// Wrap points between the car (A) and the far end (B). Into is the way the rope bent at each
// when it wrapped, i.e. into the obstacle.
public class RopePath
{
    public readonly List<Vector3> Points = new();
    public readonly List<Vector3> Into = new();
}

public readonly struct Tension
{
    public readonly float Impulse, ShareA, ShareB;
    public Tension(float impulse, float shareA, float shareB) { Impulse = impulse; ShareA = shareA; ShareB = shareB; }
    public static readonly Tension None = new(0f, 0f, 0f);
}

public static class RopeMath
{
    public const float Radius = 0.1f;      // how far wrap points sit off the surface
    public const float EndMargin = 0.3f;   // ignore hits this close to the rope's ends (the anchor's own collider)
    public const float WrapMargin = 0.2f;  // and this close to a wrap point, so a bend doesn't catch its own surface
    public const int MaxWraps = 32;

    // ---- Geometry ----

    public static float PathLength(Vector3 a, IReadOnlyList<Vector3> points, Vector3 b)
    {
        float len = 0f;
        var prev = a;
        foreach (var p in points) { len += Vector3.Distance(prev, p); prev = p; }
        return len + Vector3.Distance(prev, b);
    }

    // Total angle (radians) the rope turns through over its bends
    public static float BendAngle(Vector3 a, IReadOnlyList<Vector3> points, Vector3 b)
    {
        float total = 0f;
        var prev = a;
        for (int i = 0; i < points.Count; i++)
        {
            var next = i + 1 < points.Count ? points[i + 1] : b;
            total += Angle(points[i] - prev, next - points[i]);
            prev = points[i];
        }
        return total;
    }

    public static float Angle(Vector3 u, Vector3 v)
    {
        float lu = u.Length(), lv = v.Length();
        if (lu < 1e-6f || lv < 1e-6f) return 0f;
        return MathF.Acos(Math.Clamp(Vector3.Dot(u, v) / (lu * lv), -1f, 1f));
    }

    static Vector3 Normalized(Vector3 v)
    {
        float l = v.Length();
        return l > 1e-6f ? v / l : Vector3.Zero;
    }

    // ---- Wrapping ----

    // First static thing between p and q, ignoring the margins near rope ends and wrap points
    public static bool Blocked(IObstacles world, Vector3 p, Vector3 q, bool trimStart, bool trimEnd, out Hit hit)
    {
        hit = default;
        var d = q - p;
        float len = d.Length();
        float start = trimStart ? EndMargin : WrapMargin;
        float end = len - (trimEnd ? EndMargin : WrapMargin);
        if (end - start < 0.05f) return false;
        var dir = d / len;
        return world.Raycast(p + dir * start, dir, end - start, out hit);
    }

    // Which way the rope bends at a point: the pull of the two stretches either side
    public static Vector3 Bend(Vector3 prev, Vector3 point, Vector3 next) =>
        Normalized(prev - point) + Normalized(next - point);

    // A bend is held while the rope still bends the way it did when it wrapped. Once it
    // straightens past that, it has swung off the obstacle.
    public static bool PressesIn(Vector3 prev, Vector3 point, Vector3 next, Vector3 into) =>
        Vector3.Dot(Bend(prev, point, next), into) > 0f;

    // Removes bends the rope has swung off and adds bends where it now passes through
    // something. prevA/prevB are last tick's ends, used to find where the rope first touched.
    public static void UpdateWraps(IObstacles world, RopePath rope, Vector3 a, Vector3 b, Vector3 prevA, Vector3 prevB, bool hasPrev)
    {
        var pts = rope.Points;
        var into = rope.Into;

        for (int i = pts.Count - 1; i >= 0; i--)
        {
            var p = i == 0 ? a : pts[i - 1];
            var q = i == pts.Count - 1 ? b : pts[i + 1];
            if (PressesIn(p, pts[i], q, into[i])) continue;
            if (Blocked(world, p, q, i == 0, i == pts.Count - 1, out _)) continue;
            pts.RemoveAt(i);
            into.RemoveAt(i);
        }

        int added = 0;
        for (int i = 0; i <= pts.Count && added < 4 && pts.Count < MaxWraps; i++)
        {
            bool fromCar = i == 0, toEnd = i == pts.Count;
            var p = fromCar ? a : pts[i - 1];
            var q = toEnd ? b : pts[i];
            if (!Blocked(world, p, q, fromCar, toEnd, out _)) continue;
            var p0 = fromCar && hasPrev ? prevA : p;
            var q0 = toEnd && hasPrev ? prevB : q;
            if (!FindWrap(world, p0, q0, p, q, fromCar, toEnd, out var point, out var normal)) continue;
            if (Vector3.DistanceSquared(point, p) < WrapMargin * WrapMargin
                || Vector3.DistanceSquared(point, q) < WrapMargin * WrapMargin) continue;
            // The rope still cuts through behind the new point, so it bends towards the obstacle
            var bend = Bend(p, point, q);
            pts.Insert(i, point);
            into.Insert(i, bend.LengthSquared() > 1e-8f ? Normalized(bend) : -normal);
            added++;
            i--; // the new segment towards the car may need another bend
        }
    }

    // Where the rope first touched: sweep back from this tick's segment to last tick's
    // clear one. Without a clear one (e.g. attached straight through a rock) drape it over the top.
    public static bool FindWrap(IObstacles world, Vector3 p0, Vector3 q0, Vector3 p1, Vector3 q1, bool fromCar, bool toEnd,
        out Vector3 point, out Vector3 normal)
    {
        point = normal = default;
        Hit hit;
        if (!Blocked(world, p0, q0, fromCar, toEnd, out _))
        {
            float lo = 0f, hi = 1f;
            for (int k = 0; k < 10; k++)
            {
                float s = (lo + hi) * 0.5f;
                if (Blocked(world, Vector3.Lerp(p0, p1, s), Vector3.Lerp(q0, q1, s), fromCar, toEnd, out _)) hi = s; else lo = s;
            }
            if (!Blocked(world, Vector3.Lerp(p0, p1, hi), Vector3.Lerp(q0, q1, hi), fromCar, toEnd, out hit)) return false;
            normal = hit.Normal;
            point = PushOut(world, hit.Point + normal * Radius, normal);
            return true;
        }

        if (!Blocked(world, p1, q1, fromCar, toEnd, out hit) || !Blocked(world, q1, p1, toEnd, fromCar, out var back)) return false;
        var mid = (hit.Point + back.Point) * 0.5f;
        bool Clear(float h)
        {
            var c = mid + Vector3.UnitY * h;
            return !Blocked(world, p1, c, fromCar, false, out _) && !Blocked(world, c, q1, false, toEnd, out _);
        }
        float up = -1f;
        for (int k = 1; k <= 100; k++)
            if (Clear(0.5f * k)) { up = 0.5f * k; break; }
        if (up < 0f) return false;
        // Lower it back down onto whatever it's resting on
        float clear = up, low = 0f;
        for (int k = 0; k < 8; k++)
        {
            float t = (clear + low) * 0.5f;
            if (Clear(t)) clear = t; else low = t;
        }
        normal = Vector3.UnitY;
        point = mid + Vector3.UnitY * (clear + Radius);
        return true;
    }

    static Vector3 PushOut(IObstacles world, Vector3 p, Vector3 normal)
    {
        for (int k = 0; k < 10 && world.Overlaps(p, Radius * 0.5f); k++)
            p += normal * Radius;
        return p;
    }

    // ---- Tension ----

    // A soft rope constraint on the whole path length, solved as one impulse per tick with the
    // winch's spring frequency and damping. sA/sB are how fast each end pays rope out of the
    // bends (m/s); invA/invB each end's inverse mass along the rope. Friction over the bends
    // (capstan equation) means the side the rope slides away from gets less of the pull.
    public static Tension Solve(float pathLength, float ropeLength, float sA, float sB, float bendAngle, float friction,
        float invA, float invB, float frequency, float dampingRatio, float maxTension, float dt)
    {
        float c = pathLength - ropeLength;
        float cDot = sA + sB;

        float slide = sA - sB;
        float keep = MathF.Exp(-MathF.Max(0f, friction) * bendAngle * Math.Clamp(MathF.Abs(slide) / 0.1f, 0f, 1f));
        float kA = slide >= 0f ? 1f : keep, kB = slide >= 0f ? keep : 1f;

        float k = kA * invA + kB * invB;
        if (k < 1e-8f || dt <= 0f) return Tension.None;

        float impulse;
        if (c > 0f)
        {
            float omega = 2f * MathF.PI * (frequency > 0f ? frequency : 3f);
            float zeta = dampingRatio > 0f ? dampingRatio : 1f;
            float a1 = 2f * zeta + dt * omega;
            float a2 = dt * omega * a1;
            float massScale = a2 / (1f + a2);
            impulse = massScale * (cDot + omega / a1 * c) / k;
        }
        else if (c + cDot * dt > 0f)
            impulse = (cDot + c / dt) / k; // about to go taut: stop it overshooting
        else return Tension.None;

        impulse = Math.Clamp(impulse, 0f, MathF.Max(0f, maxTension) * dt);
        return impulse > 0f ? new Tension(impulse, kA, kB) : Tension.None;
    }

    // ---- Handing over to the game's straight-line winch ----

    // A rope length that keeps the game's own spring slack across the straight gap
    public static float SlackLength(float ropeLength, float straightDistance) =>
        MathF.Max(ropeLength, straightDistance + MathF.Max(1f, straightDistance * 0.1f));

    // The cap on paying out to hand the game alongside the slack length: the same room to pay
    // out as the real rope has, so reeling out isn't cut short (e.g. WinchUnlimited's MaxExtension)
    public static float MaxForSlack(float ropeLength, float maxLength, float slackLength) =>
        MathF.Max(maxLength, slackLength + MathF.Max(0f, maxLength - ropeLength));

    // The real rope length after the game's tick, carrying over any reeling it did to the slack length
    public static float CarryOverReeling(float realLength, float slackLength, float lengthAfterTick, float minLength, float maxLength) =>
        Math.Clamp(realLength + (lengthAfterTick - slackLength), minLength, MathF.Max(maxLength, minLength));

    // ---- Drawing ----

    // The rope as a polyline: each straight stretch sags by its share of the slack and comes
    // to rest on the ground
    public static List<Vector3> Sag(IReadOnlyList<Vector3> path, float ropeLength, IObstacles world)
    {
        float total = 0f;
        for (int i = 1; i < path.Count; i++) total += Vector3.Distance(path[i - 1], path[i]);
        float slack = MathF.Max(0f, ropeLength - total);
        var line = new List<Vector3> { path[0] };

        for (int i = 1; i < path.Count; i++)
        {
            var p = path[i - 1];
            var q = path[i];
            float len = Vector3.Distance(p, q);
            float extra = total > 0f ? slack * len / total : 0f;
            float depth = MathF.Sqrt(3f * len * extra / 8f); // parabola with that much extra length
            int n = slack > 0.01f ? Math.Clamp((int)MathF.Ceiling(len / 0.75f), 2, 40) : 1;
            for (int k = 1; k <= n; k++)
            {
                float s = (float)k / n;
                var pt = Vector3.Lerp(p, q, s);
                if (k < n && depth > 0.01f)
                {
                    float drop = depth * 4f * s * (1f - s);
                    var sagged = pt - Vector3.UnitY * drop;
                    var ground = world.GroundBelow(pt + Vector3.UnitY * 1.5f, 1.5f + drop + 0.5f);
                    float rest = Radius * 0.3f;
                    if (ground is float g && g + rest > sagged.Y) sagged.Y = g + rest;
                    pt = sagged;
                }
                line.Add(pt);
            }
        }
        return line;
    }
}
