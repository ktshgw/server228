#nullable enable
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Events;
using osu.Framework.Screens;
using osu.Game.Input.Bindings;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Screens.Play;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

public static class SomsGameplaySeek
{
    internal static readonly ConditionalWeakTable<Player, SomsSeekOverlay> Players = new();
    public static bool IsPractice(Player player) => Players.TryGetValue(player, out var overlay) && overlay.Used;
    public static bool SetHeld(bool held)
    {
        bool handled = false;
        foreach (var pair in Players)
        {
            if (!held || pair.Key.IsCurrentScreen())
            {
                pair.Value.SetHeld(held);
                handled |= pair.Key.IsCurrentScreen();
            }
        }
        return handled;
    }
}

[HarmonyPatch(typeof(Player), "LoadComplete")]
public static class SomsSeekPlayerPatch
{
    static void Postfix(Player __instance)
    {
        if (!SomsClientPreferences.Enabled || SomsGameplaySeek.Players.TryGetValue(__instance, out _)
            || !(__instance is SoloPlayer || __instance.GetType().Name == "ReplayPlayer")) return;
        var overlay = new SomsSeekOverlay(__instance) { Depth = float.MinValue };
        SomsGameplaySeek.Players.Add(__instance, overlay);
        AccessTools.Method(typeof(CompositeDrawable), "AddInternal").Invoke(__instance, new object[] { overlay });
        SomsDrawableLifecycle.OnDispose(__instance, () => SomsGameplaySeek.Players.Remove(__instance));
    }
}

[HarmonyPatch(typeof(OsuGame), "OnPressed", new[] { typeof(KeyBindingPressEvent<GlobalAction>) })]
public static class SomsSeekPressedPatch
{
    static bool Prefix(KeyBindingPressEvent<GlobalAction> e, ref bool __result)
    {
        if (!SomsClientPreferences.Enabled || (int)e.Action != (int)SomsSkinAction.GameplaySeek) return true;
        __result = SomsGameplaySeek.SetHeld(true);
        return false;
    }
}

[HarmonyPatch(typeof(OsuGame), "OnReleased", new[] { typeof(KeyBindingReleaseEvent<GlobalAction>) })]
public static class SomsSeekReleasedPatch
{
    static void Prefix(KeyBindingReleaseEvent<GlobalAction> e)
    {
        if ((int)e.Action == (int)SomsSkinAction.GameplaySeek) SomsGameplaySeek.SetHeld(false);
    }
}

[HarmonyPatch]
public static class SomsPracticeSubmissionPatch
{
    static MethodBase TargetMethod() => AccessTools.Method("osu.Game.Screens.Play.SubmittingPlayer:submitScore");
    static bool Prefix(Player __instance, ref Task __result)
    {
        if (!SomsGameplaySeek.IsPractice(__instance)) return true;
        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(Player), "ImportScore")]
public static class SomsPracticeImportPatch
{
    static bool Prefix(Player __instance, ref Task __result)
    {
        if (!SomsGameplaySeek.IsPractice(__instance)) return true;
        __result = Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(Player), "onFail")]
public static class SomsPracticeFailurePatch
{
    static bool Prefix(Player __instance, ref bool __result)
    {
        if (!SomsGameplaySeek.IsPractice(__instance)) return true;
        __result = false;
        return false;
    }
}
