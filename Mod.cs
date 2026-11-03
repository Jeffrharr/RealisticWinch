using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppFusion;
using MelonLoader;
using JoofOTHFramework;
using UnityEngine;

[assembly: MelonInfo(typeof(RealisticWinch.Mod), "RealisticWinch", "1.0.0", "Joof")]
[assembly: MelonGame("Funselektor", "over the hill")]

namespace RealisticWinch;

// The winch rope bends around rocks and terrain and lies on the ground, so trucks can
// rappel off each other over an arch
public class Mod : MelonMod
{
    internal const string Channel = "RealisticWinch";
    internal static MelonLogger.Instance Log;

    internal static MelonPreferences_Entry<bool> Wrap;
    internal static MelonPreferences_Entry<float> Friction;
    internal static MelonPreferences_Entry<bool> Visual;

    // The rope length handed to the game's own winch code, per winch, between the two hooks
    static readonly Dictionary<IntPtr, RopeWrap.StockState> stock = new();
    GameObject host;

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        var c = MelonPreferences.CreateCategory("RealisticWinch");
        Wrap = c.CreateEntry("Wrap", true, description:
            "The winch rope bends around rocks, trees and terrain instead of passing through them, like a rope over a pulley. " +
            "Lets two winched trucks hang off each other over an arch. In multiplayer the host needs RealisticWinch too.");
        Friction = c.CreateEntry("Friction", 0.3f, description:
            "Friction where the rope bends over something (0 = frictionless pulley). Higher = the far side feels less of the pull.");
        Visual = c.CreateEntry("Visual", true, description:
            "Draw the winch rope following its bends and lying on the ground when slack, instead of the game's rope.");

        // Only so clients can tell whether the host has the mod
        HillNet.Register<object>(Channel, (NetworkRunner _, PlayerRef _, object _) => { });
        WinchHooks.BeforeTick += w =>
        {
            var s = RopeWrap.BeforeStock(w);
            if (s != null) stock[w.Pointer] = s;
        };
        WinchHooks.AfterTick += w =>
        {
            if (stock.Remove(w.Pointer, out var s)) RopeWrap.AfterStock(w, s);
        };
    }

    public override void OnUpdate()
    {
        HillNet.Tick();
        // Unity destroys the helper object while the game closes; don't make a new one then
        if (host != null || Core.Quitting) return;
        host = new GameObject("RealisticWinch") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<RopeWrap>();
    }
}
