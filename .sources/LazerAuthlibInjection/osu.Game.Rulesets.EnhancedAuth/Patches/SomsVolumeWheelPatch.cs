#nullable enable
using System.Reflection;
using HarmonyLib;
using osu.Framework.Input;
using osu.Framework.Input.States;
using osu.Game.Input.Bindings;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osuTK;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// Intercept before room scroll containers consume the wheel. Only the game's root
// input manager is handled, so nested ruleset/overlay managers cannot apply it twice.
[HarmonyPatch(typeof(InputManager), "handleScroll")]
public static class SomsVolumeWheelPatch
{
    private static readonly MethodInfo containing = AccessTools.Method(typeof(osu.Framework.Graphics.Drawable), "GetContainingInputManager");
    private static readonly FieldInfo volumeField = AccessTools.Field(typeof(OsuGame), "volume");
    static bool Prefix(InputManager __instance, InputState state, Vector2 lastScroll, bool isPrecise, ref bool __result)
    {
        if (!SomsClientPreferences.Enabled || !SomsClientPreferences.Instance.EnhancedVolume.Value
            || !state.Keyboard.AltPressed || state.Keyboard.ControlPressed
            || GlobalConfigManager.GameBase is not OsuGame game
            || !ReferenceEquals(containing.Invoke(game, null), __instance)) return true;
        float delta = state.Mouse.Scroll.Y - lastScroll.Y;
        if (delta == 0 || volumeField.GetValue(game) is not VolumeOverlay volume) return true;
        volume.Show();
        volume.Adjust(delta > 0 ? GlobalAction.IncreaseVolume : GlobalAction.DecreaseVolume, System.Math.Abs(delta), isPrecise);
        __result = true;
        return false;
    }
}
