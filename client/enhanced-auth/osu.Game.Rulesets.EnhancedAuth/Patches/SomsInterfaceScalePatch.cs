#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using osu.Game.Graphics.Containers;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Screens.Play;
using ScaleContainer = osu.Game.Graphics.Containers.ScalingContainer.ScalingDrawSizePreservingFillContainer;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// Replace only the scale read in the native layout calculation. The original
// config, its limits, animations and the gameplay coordinate system stay intact.
[HarmonyPatch(typeof(ScaleContainer), "Update")]
public static class SomsInterfaceScalePatch
{
    private static readonly MethodInfo currentScaleGetter = AccessTools.PropertyGetter(typeof(ScaleContainer), "CurrentScale");
    private static readonly Func<ScaleContainer, float> nativeScale = AccessTools.MethodDelegate<Func<ScaleContainer, float>>(currentScaleGetter);
    private static readonly AccessTools.FieldRef<ScaleContainer, bool> appliesUI = AccessTools.FieldRefAccess<ScaleContainer, bool>("applyUIScale");
    private static readonly Func<ScaleContainer, OsuGame?> resolvedGame = AccessTools.MethodDelegate<Func<ScaleContainer, OsuGame?>>(
        AccessTools.PropertyGetter(typeof(ScaleContainer), "game"));

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(currentScaleGetter))
            {
                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(SomsInterfaceScalePatch), nameof(effectiveScale));
            }
            yield return instruction;
        }
    }

    private static float effectiveScale(ScaleContainer container)
    {
        if (!appliesUI(container) || !SomsClientPreferences.Enabled)
            return nativeScale(container);

        var preferences = SomsClientPreferences.Instance;
        if (!preferences.SeparateInterfaceScales.Value)
            return nativeScale(container);

        // Screen identity includes replays, pause and breaks. PlayingState alone
        // switches to NotPlaying during those states and would resize mid-game.
        return resolvedGame(container)?.ScreenStack?.CurrentScreen is Player
            ? preferences.GameplayInterfaceScale.Value
            : preferences.MenuInterfaceScale.Value;
    }

    internal static float CursorCompensation(ScaleContainer container) =>
        nativeScale(container) / effectiveScale(container);
}
