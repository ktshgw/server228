#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Online.API;
using osu.Game.Online.Matchmaking;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Screens.Menu;
using osu.Game.Screens.OnlinePlay.Matchmaking.Queue;
using osu.Game.Screens;
using osu.Game.Screens.OnlinePlay.Matchmaking.RankedPlay;
using osuTK.Graphics;
using osuTK.Input;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(ButtonSystem), "load")]
public static class SomsAiMultiplayerMenuPatch
{
    static void Postfix(ButtonSystem __instance, List<MainMenuButton> ___buttonsMulti, ButtonArea ___buttonArea)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance) || ___buttonsMulti.Any(button => button.Name == "somsai-menu-button")) return;
        var button = new MainMenuButton("SOMSAI", "button-daily-select", FontAwesome.Solid.Trophy, new Color4(67, 157, 172, 255), (_, _) =>
        {
            var api = (IAPIProvider?)AccessTools.Property(typeof(ButtonSystem), "api").GetValue(__instance);
            if (api?.State.Value != APIState.Online)
            {
                (AccessTools.Property(typeof(ButtonSystem), "loginOverlay").GetValue(__instance) as LoginOverlay)?.Show();
                return;
            }
            var game = AccessTools.Property(typeof(ButtonSystem), "game").GetValue(__instance) as OsuGame;
            if (game != null) SomsAiBubbleTransition.Enter(game);
        }, Key.A)
        {
            Name = "somsai-menu-button",
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            VisibleState = ButtonSystemState.Multi,
        };
        ___buttonsMulti.Add(button);
        ___buttonArea.Add(button);
    }
}

[HarmonyPatch(typeof(ScreenQueue), nameof(ScreenQueue.SetState))]
public static class SomsTeamRankedScreenPatch
{
    static void Postfix(ScreenQueue __instance, ScreenQueue.MatchmakingScreenState newState, Container ___mainContent)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance)
            || newState != ScreenQueue.MatchmakingScreenState.Idle
            || (MatchmakingPoolType)AccessTools.Field(typeof(ScreenQueue), "poolType").GetValue(__instance)! != MatchmakingPoolType.RankedPlay
            || ___mainContent.Children.FirstOrDefault() is not FillFlowContainer flow) return;

        foreach (var button in flow.Children.OfType<osu.Game.Graphics.UserInterface.ShearedButton>())
            DisableSearch(button);
    }

    internal static void DisableSearch(osu.Game.Graphics.UserInterface.ShearedButton button)
    {
        // Do not propagate false into the native connection state. Disconnect
        // the selected-pool binding too: LoadComplete otherwise re-enables it.
        button.Enabled.UnbindBindings();
        if (AccessTools.Field(button.GetType(), "SelectedPool")?.GetValue(button) is Bindable<MatchmakingPool?> selected)
        {
            selected.UnbindBindings();
            selected.Value = null;
        }
        button.Action = null;
        button.Enabled.Value = false;
        button.Text = "Иди в обычный лазер";
        button.Width = 350;
    }
}
