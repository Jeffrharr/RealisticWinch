using System;
using System.Collections.Generic;
using System.Numerics;
using RealisticWinch;
using Xunit;

namespace RealisticWinch.Tests;

// A world of axis-aligned boxes, optionally standing on flat ground at y = 0
class BoxWorld : IObstacles
{
    public readonly List<(Vector3 Min, Vector3 Max)> Boxes = new();
    public bool Ground;

    public BoxWorld Box(Vector3 min, Vector3 max) { Boxes.Add((min, max)); return this; }

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out Hit hit)
    {
        hit = default;
        float best = float.MaxValue;
        foreach (var (min, max) in Boxes)
        {
            if (!RayBox(origin, direction, min, max, out float t, out var normal) || t > maxDistance || t >= best) continue;
            best = t;
            hit = new Hit(origin + direction * t, normal);
        }
        if (Ground && direction.Y < 0f && origin.Y >= 0f)
        {
            float t = -origin.Y / direction.Y;
            if (t <= maxDistance && t < best) { best = t; hit = new Hit(origin + direction * t, Vector3.UnitY); }
        }
        return best < float.MaxValue;
    }

    public bool Overlaps(Vector3 p, float radius)
    {
        foreach (var (min, max) in Boxes)
            if (Vector3.Distance(Vector3.Clamp(p, min, max), p) < radius) return true;
        return Ground && p.Y < radius;
    }

    public float? GroundBelow(Vector3 from, float depth) =>
        Raycast(from, -Vector3.UnitY, depth, out var hit) ? hit.Point.Y : null;

    static bool RayBox(Vector3 o, Vector3 d, Vector3 min, Vector3 max, out float tHit, out Vector3 normal)
    {
        float tMin = 0f, tMax = float.MaxValue;
        normal = default;
        tHit = 0f;
        for (int axis = 0; axis < 3; axis++)
        {
            float oa = Get(o, axis), da = Get(d, axis), lo = Get(min, axis), hi = Get(max, axis);
            if (MathF.Abs(da) < 1e-9f)
            {
                if (oa < lo || oa > hi) return false;
                continue;
            }
            float t1 = (lo - oa) / da, t2 = (hi - oa) / da;
            float sign = -1f;
            if (t1 > t2) { (t1, t2) = (t2, t1); sign = 1f; }
            if (t1 > tMin) { tMin = t1; normal = Axis(axis) * sign; }
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) return false;
        }
        if (normal == default) return false; // started inside
        tHit = tMin;
        return true;
    }

    static float Get(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    static Vector3 Axis(int axis) => axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
}

public class GeometryTests
{
    [Fact]
    public void PathLengthAddsTheStretches()
    {
        var pts = new List<Vector3> { new(3, 4, 0) };
        Assert.Equal(5f + 4f, RopeMath.PathLength(Vector3.Zero, pts, new Vector3(3, 0, 0)), 4);
    }

    [Fact]
    public void BendAngleOfARightAngle()
    {
        var pts = new List<Vector3> { new(0, 5, 0) };
        Assert.Equal(MathF.PI / 2, RopeMath.BendAngle(Vector3.Zero, pts, new Vector3(5, 5, 0)), 4);
    }

    [Fact]
    public void StraightRopeHasNoBend() =>
        Assert.Equal(0f, RopeMath.BendAngle(Vector3.Zero, new List<Vector3>(), new Vector3(5, 0, 0)), 4);

    [Fact]
    public void RopeOverATopPressesIntoIt() =>
        Assert.True(RopeMath.PressesIn(new Vector3(-5, 0, 0), new Vector3(0, 3, 0), new Vector3(5, 0, 0), -Vector3.UnitY));

    [Fact]
    public void RopeLiftedOffATopNoLongerPresses() =>
        Assert.False(RopeMath.PressesIn(new Vector3(-5, 6, 0), new Vector3(0, 3, 0), new Vector3(5, 6, 0), -Vector3.UnitY));
}

public class WrapTests
{
    // A 2 m wide, 4 m tall rock between x = -1 and 1
    static BoxWorld Rock() => new BoxWorld().Box(new Vector3(-1, 0, -1), new Vector3(1, 4, 1));

    [Fact]
    public void ClearRopeHasNoBends()
    {
        var rope = new RopePath();
        RopeMath.UpdateWraps(Rock(), rope, new Vector3(-10, 6, 0), new Vector3(10, 6, 0), default, default, false);
        Assert.Empty(rope.Points);
    }

    [Fact]
    public void SwingingIntoTheRockWrapsAtTheTouchPoint()
    {
        var world = Rock();
        var rope = new RopePath();
        var b = new Vector3(10, 2, 0);
        // Last tick the rope passed over the rock; now the car end has dropped so it cuts through
        var prevA = new Vector3(-10, 8, 0);
        var a = new Vector3(-10, 2, 0);
        RopeMath.UpdateWraps(world, rope, a, b, prevA, b, true);

        // Caught over the rock's top corners
        Assert.InRange(rope.Points.Count, 1, 2);
        foreach (var p in rope.Points)
        {
            Assert.False(world.Overlaps(p, 0.01f));
            Assert.InRange(p.Y, 3.9f, 4.5f);
            Assert.InRange(p.X, -1.5f, 1.5f);
        }
        AssertClear(world, a, rope, b);
        Assert.True(RopeMath.PathLength(a, rope.Points, b) > Vector3.Distance(a, b));
    }

    [Fact]
    public void AttachedThroughTheRockDrapesOverTheTop()
    {
        var world = Rock();
        var rope = new RopePath();
        var a = new Vector3(-10, 1, 0);
        var b = new Vector3(10, 1, 0);
        RopeMath.UpdateWraps(world, rope, a, b, default, default, false);

        Assert.NotEmpty(rope.Points);
        foreach (var p in rope.Points) Assert.False(world.Overlaps(p, 0.01f));
        AssertClear(world, a, rope, b);
    }

    // Every stretch of the rope is clear of obstacles
    static void AssertClear(IObstacles world, Vector3 a, RopePath rope, Vector3 b)
    {
        var path = new List<Vector3> { a };
        path.AddRange(rope.Points);
        path.Add(b);
        for (int i = 1; i < path.Count; i++)
            Assert.False(RopeMath.Blocked(world, path[i - 1], path[i], i == 1, i == path.Count - 1, out _),
                $"stretch {path[i - 1]} to {path[i]} passes through something");
    }

    [Fact]
    public void LiftingTheRopeOffUnwraps()
    {
        var world = Rock();
        var rope = new RopePath();
        var b = new Vector3(10, 2, 0);
        RopeMath.UpdateWraps(world, rope, new Vector3(-10, 2, 0), b, new Vector3(-10, 8, 0), b, true);
        Assert.NotEmpty(rope.Points);

        // Both ends rise well above the rock: the rope swings clear
        RopeMath.UpdateWraps(world, rope, new Vector3(-10, 12, 0), new Vector3(10, 12, 0), default, default, false);
        Assert.Empty(rope.Points);
    }

    [Fact]
    public void ArchBetweenTwoTrucksHoldsTheRopeOverTheTop()
    {
        // An arch's span, 3 m thick and 20 m up, with a truck hanging each side
        var world = new BoxWorld().Box(new Vector3(-1.5f, 18, -3), new Vector3(1.5f, 20, 3));
        var rope = new RopePath();
        var a = new Vector3(-6, 10, 0);
        var b = new Vector3(6, 12, 0);
        RopeMath.UpdateWraps(world, rope, a, b, default, default, false);
        Assert.Empty(rope.Points); // under the arch, the rope is clear

        // Trucks drive over the top and down either side: rope drapes over the span
        var rope2 = new RopePath();
        var a2 = new Vector3(-6, 19, 0);
        var b2 = new Vector3(6, 19, 0);
        RopeMath.UpdateWraps(world, rope2, a2, b2, new Vector3(-6, 25, 0), new Vector3(6, 25, 0), true);
        Assert.NotEmpty(rope2.Points);
        foreach (var p in rope2.Points)
        {
            Assert.True(p.Y >= 19.9f, $"bend at {p} should sit at the top of the span");
            Assert.False(world.Overlaps(p, 0.01f));
        }
        AssertClear(world, a2, rope2, b2);

        // The trucks hang lower: the rope stays over the span
        var a3 = new Vector3(-6, 5, 0);
        var b3 = new Vector3(6, 8, 0);
        RopeMath.UpdateWraps(world, rope2, a3, b3, a2, b2, true);
        Assert.NotEmpty(rope2.Points);
        AssertClear(world, a3, rope2, b3);
        Assert.True(RopeMath.PathLength(a3, rope2.Points, b3) > 2 * 12f);
    }
}

public class TensionTests
{
    const float Dt = 1f / 60f;

    static Tension Solve(float path, float rope, float sA = 0f, float sB = 0f, float bend = 0f, float friction = 0f,
        float invA = 1f / 2000f, float invB = 0f, float maxTension = 1e9f) =>
        RopeMath.Solve(path, rope, sA, sB, bend, friction, invA, invB, 3f, 0.8f, maxTension, Dt);

    [Fact]
    public void SlackRopePullsNothing() => Assert.Equal(0f, Solve(path: 10f, rope: 12f).Impulse);

    [Fact]
    public void StretchedRopePullsTheCar()
    {
        var t = Solve(path: 12f, rope: 10f);
        Assert.True(t.Impulse > 0f);
        Assert.Equal(1f, t.ShareA);
    }

    [Fact]
    public void MoreStretchPullsHarder() =>
        Assert.True(Solve(path: 14f, rope: 10f).Impulse > Solve(path: 11f, rope: 10f).Impulse);

    [Fact]
    public void RopeAboutToGoTautIsCaught() =>
        // Just slack, but the car is moving away fast enough to overshoot this tick
        Assert.True(Solve(path: 9.99f, rope: 10f, sA: 5f).Impulse > 0f);

    [Fact]
    public void FrictionlessPulleySharesEqually()
    {
        var t = Solve(path: 12f, rope: 10f, sA: 1f, sB: -1f, bend: MathF.PI, friction: 0f, invB: 1f / 2000f);
        Assert.Equal(t.ShareA, t.ShareB, 4);
    }

    [Fact]
    public void FrictionShieldsTheSideTheRopeSlidesTowards()
    {
        // The rope slides over the bend from A's side to B's (B is falling): A feels less pull
        float mu = 0.3f, bend = MathF.PI;
        var t = Solve(path: 12f, rope: 10f, sA: -1f, sB: 1f, bend: bend, friction: mu, invB: 1f / 2000f);
        Assert.Equal(1f, t.ShareB);
        Assert.Equal(MathF.Exp(-mu * bend), t.ShareA, 4);
    }

    [Fact]
    public void ImpulseIsCappedByMaxTension() =>
        Assert.Equal(100f * Dt, Solve(path: 50f, rope: 10f, maxTension: 100f).Impulse, 4);

    [Fact]
    public void ImmovableEndsGetNoImpulse() => Assert.Equal(0f, Solve(path: 12f, rope: 10f, invA: 0f).Impulse);
}

public class StockHandoverTests
{
    [Fact]
    public void SlackLengthKeepsTheStraightSpringSlack()
    {
        Assert.True(RopeMath.SlackLength(5f, 20f) > 20f);
        Assert.Equal(30f, RopeMath.SlackLength(30f, 20f));
    }

    [Fact]
    public void PayingOutKeepsItsRoomWhileBent()
    {
        // 15 m of rope with WinchUnlimited's 500 m cap, handed to the game as 22 m
        float max = RopeMath.MaxForSlack(15f, 500f, 22f);
        Assert.Equal(500f - 15f, max - 22f, 4);
        // Paying out 2 m during the tick comes through in full
        Assert.Equal(17f, RopeMath.CarryOverReeling(15f, 22f, MathF.Min(24f, max), 0.3f, 500f), 4);
    }

    [Fact]
    public void PayingOutStopsAtTheRealCap()
    {
        // 499 m of 500 m paid out: the game may pay out 1 m more, no further
        float max = RopeMath.MaxForSlack(499f, 500f, 520f);
        Assert.Equal(521f, max, 4);
        Assert.Equal(500f, RopeMath.CarryOverReeling(499f, 520f, MathF.Min(530f, max), 0.3f, 500f), 4);
    }

    [Fact]
    public void ReelingDuringTheGameTickCarriesOver() =>
        Assert.Equal(14.5f, RopeMath.CarryOverReeling(15f, 22f, 21.5f, 0.3f, 500f), 4);

    [Fact]
    public void CarriedOverLengthStaysInRange()
    {
        Assert.Equal(0.3f, RopeMath.CarryOverReeling(0.5f, 22f, 20f, 0.3f, 500f), 4);
        Assert.Equal(40f, RopeMath.CarryOverReeling(39f, 50f, 52f, 0.3f, 40f), 4);
    }
}

public class SagTests
{
    [Fact]
    public void TautRopeIsStraight()
    {
        var path = new List<Vector3> { Vector3.Zero, new(10, 0, 0) };
        Assert.Equal(path, RopeMath.Sag(path, 10f, new BoxWorld()));
    }

    [Fact]
    public void SlackRopeHangsDown()
    {
        var path = new List<Vector3> { new(0, 10, 0), new(10, 10, 0) };
        var line = RopeMath.Sag(path, 12f, new BoxWorld());
        Assert.True(line.Count > 2);
        Assert.Equal(path[0], line[0]);
        Assert.Equal(path[1], line[^1]);
        float lowest = float.MaxValue;
        foreach (var p in line) lowest = MathF.Min(lowest, p.Y);
        Assert.True(lowest < 9f);
    }

    [Fact]
    public void SlackRopeRestsOnTheGround()
    {
        var path = new List<Vector3> { new(0, 0.5f, 0), new(10, 0.5f, 0) };
        var line = RopeMath.Sag(path, 20f, new BoxWorld { Ground = true });
        foreach (var p in line) Assert.True(p.Y >= 0f, $"{p} is underground");
        Assert.Contains(line, p => p.Y < 0.1f);
    }
}
