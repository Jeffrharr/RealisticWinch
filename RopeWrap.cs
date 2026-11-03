using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using JoofFramework;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace RealisticWinch;

// The stock winch rope is a straight spring between car and anchor that passes through
// everything. This bends it around rocks, trees and terrain: every machine tracks where the
// rope wraps, and while it's wrapped the winch's own network tick applies the tension along
// the bent path instead, like a rope over a pulley (with friction). Slack rope is drawn
// sagging onto the ground. Two trucks winched together over an arch hang off each other.
// The maths is in RopeMath; this is the Unity and game side.
[RegisterTypeInIl2Cpp]
public class RopeWrap : MonoBehaviour
{
    public static RopeWrap Instance { get; private set; }

    class Rope
    {
        public readonly RopePath Path = new();
        public NVector3 PrevA, PrevB;
        public bool HasPrev;
        public VehicleWinchSystem.WinchStates State;
        public int LoggedCount;
        public float NextLog;
        public bool LoggedAttach;
        public LineRenderer Line;
        public IntPtr HiddenObi;
    }

    readonly Dictionary<IntPtr, Rope> ropes = new();
    readonly List<VehicleWinchSystem> winches = new();
    float nextScan;
    Material lineMaterial;

    public RopeWrap(IntPtr ptr) : base(ptr) { }

    void Awake() => Instance = this;

    // Only bend the rope when this machine's physics agrees with the host's
    static bool Active =>
        Mod.Wrap.Value && (HillNet.IsAuthority || HillNet.HostHas(Mod.Channel));

    static NVector3 N(Vector3 v) => new(v.x, v.y, v.z);
    static Vector3 U(NVector3 v) => new(v.X, v.Y, v.Z);

    // ---- The world, as RopeMath sees it ----

    class UnityObstacles : IObstacles
    {
        public static readonly UnityObstacles Instance = new();
        int obstacleMask, groundMask;

        void Masks()
        {
            if (obstacleMask != 0) return;
            try
            {
                obstacleMask = Layers.TerrainLayerMask | Layers.RockLayerMask | Layers.TreeLayerMask
                    | Layers.DefaultLayerMask | Layers.CampableRockLayerMask;
                groundMask = Layers.TerrainLayerMask;
            }
            catch (Exception e)
            {
                Mod.Log.Warning($"Rope: falling back to layer names ({e.Message})");
                obstacleMask = LayerMask.GetMask("Default", "Terrain", "Rock", "Tree", "CampableRock");
                groundMask = LayerMask.GetMask("Terrain");
            }
            if (groundMask == 0) groundMask = obstacleMask;
        }

        // Moving bodies (cars, ragdolls, loose objects) don't count
        public bool Raycast(NVector3 origin, NVector3 direction, float maxDistance, out Hit hit)
        {
            Masks();
            hit = default;
            var hits = Physics.RaycastAll(U(origin), U(direction), maxDistance, obstacleMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.distance < 1e-3f || h.distance >= best) continue;
                var rb = h.rigidbody;
                if (rb != null && !rb.isKinematic) continue;
                best = h.distance;
                hit = new Hit(N(h.point), N(h.normal));
                found = true;
            }
            return found;
        }

        public bool Overlaps(NVector3 point, float radius)
        {
            Masks();
            return Physics.CheckSphere(U(point), radius, obstacleMask, QueryTriggerInteraction.Ignore);
        }

        public float? GroundBelow(NVector3 from, float depth)
        {
            Masks();
            return Physics.Raycast(U(from), Vector3.down, out var hit, depth, groundMask, QueryTriggerInteraction.Ignore)
                ? hit.point.y : null;
        }
    }

    // ---- Wrap tracking, every machine ----

    void FixedUpdate()
    {
        try { Tick(); }
        catch (Exception e) { Mod.Log.Error(e.ToString()); }
    }

    void Tick()
    {
        if (Time.time >= nextScan)
        {
            nextScan = Time.time + 1f;
            winches.Clear();
            winches.AddRange(FindObjectsOfType<VehicleWinchSystem>());
            var live = new HashSet<IntPtr>();
            foreach (var w in winches) if (w != null) live.Add(w.Pointer);
            foreach (var key in new List<IntPtr>(ropes.Keys))
                if (!live.Contains(key)) Drop(key, null);
        }

        foreach (var w in winches)
        {
            if (w == null) continue;
            var state = w.WinchState;
            if (!Active || state == VehicleWinchSystem.WinchStates.Disabled || !TryGetEnds(w, out var a, out _, out var b, out _))
            {
                Drop(w.Pointer, w);
                continue;
            }
            if (!ropes.TryGetValue(w.Pointer, out var rope) || rope.State != state)
            {
                Drop(w.Pointer, w);
                ropes[w.Pointer] = rope = new Rope { State = state };
            }
            if (!rope.LoggedAttach)
            {
                rope.LoggedAttach = true;
                var obi = ObiEnd(w);
                Mod.Log.Msg($"Rope: {state} car {a} far end {b} ({(b - a).magnitude:F1} m, rope {w.RopeLength:F1} m), " +
                    $"spring {w.frequency} Hz damping {w.dampingRatio}, obi end {(obi != null ? obi.position.ToString() : "none")}");
            }
            RopeMath.UpdateWraps(UnityObstacles.Instance, rope.Path, N(a), N(b), rope.PrevA, rope.PrevB, rope.HasPrev);
            rope.PrevA = N(a);
            rope.PrevB = N(b);
            rope.HasPrev = true;

            int bends = rope.Path.Points.Count;
            if (bends != rope.LoggedCount && Time.time >= rope.NextLog)
            {
                rope.NextLog = Time.time + 0.5f;
                rope.LoggedCount = bends;
                Mod.Log.Msg($"Rope: {bends} bends, path {RopeMath.PathLength(N(a), rope.Path.Points, N(b)):F1} m, rope {w.RopeLength:F1} m");
            }
        }
    }

    void Drop(IntPtr key, VehicleWinchSystem w)
    {
        if (!ropes.TryGetValue(key, out var rope)) return;
        if (rope.Line != null) Destroy(rope.Line.gameObject);
        if (w != null && rope.HiddenObi != IntPtr.Zero) SetObiVisible(w, true);
        ropes.Remove(key);
    }

    // ---- Rope ends ----

    static Transform ObiEnd(VehicleWinchSystem w)
    {
        var refs = w.obiRopeReferences;
        var att = refs != null ? refs.endPointAttachment : null;
        return att != null ? att.target : null;
    }

    // The car's winch point, and the far end: a static anchor, another car or a movable object.
    // A null body means the end doesn't move.
    static bool TryGetEnds(VehicleWinchSystem w, out Vector3 a, out Rigidbody rbA, out Vector3 b, out Rigidbody rbB)
    {
        a = b = default;
        rbB = null;
        rbA = w.carRigidbody;
        if (rbA == null) return false;
        a = rbA.transform.TransformPoint((Vector3)w.WinchAttachPointInCar);

        var state = w.WinchState;
        if (state == VehicleWinchSystem.WinchStates.AttachedToStaticObject)
        {
            b = (Vector3)w.WinchAnchorPoint;
            return true;
        }

        // The visual rope's far end follows the other car's or object's attach point
        var end = ObiEnd(w);
        if (end != null)
        {
            b = end.position;
            rbB = end.GetComponentInParent<Rigidbody>();
        }
        else if (state == VehicleWinchSystem.WinchStates.AttachedToMovableObject)
        {
            var nrb = w.WinchedMovableObjectRigidbody;
            rbB = nrb != null ? nrb.GetComponent<Rigidbody>() : null;
            if (rbB == null) return false;
            b = rbB.position;
        }
        else if (state == VehicleWinchSystem.WinchStates.AttachedToOtherPlayer)
        {
            var other = Instance != null ? Instance.CarOf(w.WinchedCarPlayerRef.PlayerId) : null;
            rbB = other != null ? other.carRigidbody : null;
            if (rbB == null) return false;
            b = rbB.worldCenterOfMass;
        }
        else return false;

        if (rbB != null && (rbB.isKinematic || rbB.Pointer == rbA.Pointer)) rbB = null;
        return true;
    }

    VehicleWinchSystem CarOf(int playerId)
    {
        foreach (var w in winches)
            if (w != null && w.Object != null && w.Object.InputAuthority.PlayerId == playerId) return w;
        return null;
    }

    // ---- Physics, inside the winch's network tick ----

    internal class StockState { public float RopeLength, MaxRopeLength, Slack, RaisedMax = -1f; }

    // While the rope is bent, apply the tension along the bent path, then hand the game's
    // straight-line spring a rope long enough that it stays slack. Reeling still works: the
    // change it makes to the length is carried over afterwards.
    internal static StockState BeforeStock(VehicleWinchSystem w)
    {
        var self = Instance;
        if (self == null || !Active || !self.ropes.TryGetValue(w.Pointer, out var rope) || rope.Path.Points.Count == 0) return null;
        // The host simulates every winch, a client only predicts its own
        var obj = w.Object;
        if (obj == null || !(obj.HasStateAuthority || obj.HasInputAuthority)) return null;
        if (!TryGetEnds(w, out var a, out var rbA, out var b, out var rbB)) return null;

        var runner = w.Runner;
        float dt = runner != null ? runner.DeltaTime : Time.fixedDeltaTime;
        ApplyTension(w, rope.Path, a, rbA, b, rbB, dt);

        var s = new StockState { RopeLength = w.RopeLength, MaxRopeLength = w.MaxRopeLength };
        s.Slack = RopeMath.SlackLength(s.RopeLength, Vector3.Distance(a, b));
        float max = RopeMath.MaxForSlack(s.RopeLength, s.MaxRopeLength, s.Slack);
        if (max > s.MaxRopeLength) w.MaxRopeLength = s.RaisedMax = max;
        w.RopeLength = s.Slack;
        return s;
    }

    internal static void AfterStock(VehicleWinchSystem w, StockState s)
    {
        if (s == null) return;
        // Another mod may have raised the cap since; keep theirs
        if (s.RaisedMax >= 0f && w.MaxRopeLength == s.RaisedMax) w.MaxRopeLength = s.MaxRopeLength;
        w.RopeLength = RopeMath.CarryOverReeling(s.RopeLength, s.Slack, w.RopeLength, w.minRopeLength, w.MaxRopeLength);
    }

    static void ApplyTension(VehicleWinchSystem w, RopePath path, Vector3 a, Rigidbody rbA, Vector3 b, Rigidbody rbB, float dt)
    {
        var pts = path.Points;
        var uA = (U(pts[0]) - a).normalized;
        var uB = (U(pts[pts.Count - 1]) - b).normalized;
        // How fast each end is paying rope out of the bends
        float sA = -Vector3.Dot(rbA.GetPointVelocity(a), uA);
        float sB = rbB != null ? -Vector3.Dot(rbB.GetPointVelocity(b), uB) : 0f;

        var t = RopeMath.Solve(RopeMath.PathLength(N(a), pts, N(b)), w.RopeLength, sA, sB,
            RopeMath.BendAngle(N(a), pts, N(b)), Mod.Friction.Value,
            InverseMassAlong(rbA, a, uA), rbB != null ? InverseMassAlong(rbB, b, uB) : 0f,
            w.frequency, w.dampingRatio, w.maxTension, dt);
        if (t.Impulse <= 0f) return;
        rbA.AddForceAtPosition(uA * (t.Impulse * t.ShareA), a, ForceMode.Impulse);
        if (rbB != null) rbB.AddForceAtPosition(uB * (t.Impulse * t.ShareB), b, ForceMode.Impulse);
    }

    static float InverseMassAlong(Rigidbody rb, Vector3 point, Vector3 dir)
    {
        if (rb == null || rb.isKinematic || rb.mass <= 0f) return 0f;
        var rn = Vector3.Cross(point - rb.worldCenterOfMass, dir);
        var local = Quaternion.Inverse(rb.rotation * rb.inertiaTensorRotation) * rn;
        var inertia = rb.inertiaTensor;
        float angular = (inertia.x > 0f ? local.x * local.x / inertia.x : 0f)
            + (inertia.y > 0f ? local.y * local.y / inertia.y : 0f)
            + (inertia.z > 0f ? local.z * local.z / inertia.z : 0f);
        return 1f / rb.mass + angular;
    }

    // ---- Drawing ----

    void LateUpdate()
    {
        try { Draw(); }
        catch (Exception e) { Mod.Log.Error(e.ToString()); }
    }

    void Draw()
    {
        foreach (var w in winches)
        {
            if (w == null || !ropes.TryGetValue(w.Pointer, out var rope)) continue;
            if (!Mod.Visual.Value || !TryGetEnds(w, out var a, out _, out var b, out _))
            {
                if (rope.Line != null) rope.Line.enabled = false;
                if (rope.HiddenObi != IntPtr.Zero) SetObiVisible(w, true);
                rope.HiddenObi = IntPtr.Zero;
                continue;
            }

            var obi = w.obiRopeGameObject;
            if (obi != null && rope.HiddenObi != obi.Pointer)
            {
                if (lineMaterial == null) lineMaterial = FindRopeMaterial(obi);
                SetObiVisible(w, false);
                rope.HiddenObi = obi.Pointer;
            }
            if (rope.Line == null) rope.Line = MakeLine();
            rope.Line.enabled = true;

            var path = new List<NVector3>(rope.Path.Points.Count + 2) { N(a) };
            path.AddRange(rope.Path.Points);
            path.Add(N(b));
            var line = RopeMath.Sag(path, w.RopeLength, UnityObstacles.Instance);
            rope.Line.positionCount = line.Count;
            for (int i = 0; i < line.Count; i++) rope.Line.SetPosition(i, U(line[i]));
        }
    }

    LineRenderer MakeLine()
    {
        var go = new GameObject("HillRope");
        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.widthMultiplier = 0.035f;
        line.numCapVertices = 2;
        line.numCornerVertices = 2;
        line.textureMode = LineTextureMode.Tile;
        if (lineMaterial == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            lineMaterial = new Material(shader) { color = new Color(0.12f, 0.1f, 0.08f) };
        }
        line.sharedMaterial = lineMaterial;
        return line;
    }

    static Material FindRopeMaterial(GameObject obi)
    {
        foreach (var r in obi.GetComponentsInChildren<Renderer>(true))
            if (r != null && r.sharedMaterial != null) return r.sharedMaterial;
        return null;
    }

    static void SetObiVisible(VehicleWinchSystem w, bool visible)
    {
        var obi = w.obiRopeGameObject;
        if (obi == null) return;
        foreach (var r in obi.GetComponentsInChildren<Renderer>(true))
            if (r != null) r.enabled = visible;
        // Obi draws the rope through its solver, from components like ObiRopeExtrudedRenderer
        foreach (var b in obi.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (b == null) continue;
            var name = b.GetIl2CppType().Name;
            if (name.StartsWith("ObiRope") && name.EndsWith("Renderer")) b.enabled = visible;
        }
    }
}
