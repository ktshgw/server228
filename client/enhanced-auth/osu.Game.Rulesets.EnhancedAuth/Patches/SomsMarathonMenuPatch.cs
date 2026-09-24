using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Screens;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Screens.Menu;
using osuTK.Graphics;
using osuTK.Input;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(ButtonSystem), "load")]
public static class SomsMarathonMenuPatch
{
    static void Postfix(ButtonSystem __instance, List<MainMenuButton> ___buttonsPlay, ButtonArea ___buttonArea)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance)) return;
        var old = ___buttonsPlay.OfType<DailyChallengeButton>().FirstOrDefault();
        if (old == null) return;
        int index = ___buttonsPlay.IndexOf(old);
        ___buttonsPlay.Remove(old);
        ___buttonArea.Remove(old, true);
        var button = new MainMenuButton("Марафон", "button-daily-select", FontAwesome.Solid.Music, new Color4(94, 63, 186, 255), (_, _) =>
        {
            if (AccessTools.Property(typeof(ButtonSystem), "game").GetValue(__instance) is OsuGame game)
                game.PerformFromScreen(screen => screen.Push(new SomsMarathonScreen()));
        }, Key.D) { VisibleState = ButtonSystemState.Play, Anchor = Anchor.CentreLeft, Origin = Anchor.CentreLeft };
        ___buttonsPlay.Insert(index, button);
        ___buttonArea.Add(button);
    }
}
