#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.AspNetCore.SignalR.Client;
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
using osu.Game.Online.Rooms;
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
    static void Postfix(ButtonSystem __instance, List<MainMenuButton> ___buttonsMulti)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance))
            return;

        MainMenuButton? rankedButton = ___buttonsMulti.ElementAtOrDefault(1);
        if (rankedButton == null || rankedButton.Name == "somsai-menu-button")
            return;

        // Keep the native Ranked Play button itself, including its crown, purple
        // colour, sounds and shortcut. Only its label and destination change.
        var content = Traverse.Create(rankedButton).Field("content").GetValue<Container>();
        var label = content?.Children.OfType<osu.Game.Graphics.Sprites.OsuSpriteText>().FirstOrDefault();
        if (label != null)
            label.Text = "SOMSAI";
        rankedButton.Name = "somsai-menu-button";

        __instance.OnRankedPlay = () =>
        {
            var game = AccessTools.Property(typeof(ButtonSystem), "game").GetValue(__instance) as OsuGame;
            if (game != null)
                SomsAiBubbleTransition.Enter(game);
        };
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
        button.Text = "Use official osu!lazer";
        button.Width = 350;
    }
}

/// <summary>
/// The stock client routes even empty-password joins through
/// JoinRoomWithPassword. Use the passwordless hub endpoint instead.
/// </summary>
[HarmonyPatch(typeof(OnlineMultiplayerClient), "JoinRoomInternal")]
public static class SomsPasswordlessRoomJoinPatch
{
    static bool Prefix(
        OnlineMultiplayerClient __instance,
        long roomId,
        string? password,
        ref Task<MultiplayerRoom> __result)
    {
        if (!SomsClientPreferences.Enabled || !string.IsNullOrEmpty(password))
            return true;

        var connection = AccessTools.Property(typeof(OnlineMultiplayerClient), "connection").GetValue(__instance) as HubConnection;
        if (connection == null)
            return true;

        __result = connection.InvokeAsync<MultiplayerRoom>(nameof(IMultiplayerServer.JoinRoom), roomId);
        return false;
    }
}
