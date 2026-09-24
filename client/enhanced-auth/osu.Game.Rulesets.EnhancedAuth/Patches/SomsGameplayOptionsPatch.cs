#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Containers;
using osu.Framework.Screens;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.Settings.Sections.UserInterface;
using osu.Game.Overlays.Settings.Sections.Audio;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.Mods;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch]
public static class SomsGameplayOptionsPatch
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(Assembly.Load("osu.Game.Rulesets.Osu").GetType("osu.Game.Rulesets.Osu.UI.OsuSettingsSubsection", true), "load");
        yield return AccessTools.Method(typeof(SongSelectSettings), "load");
        yield return AccessTools.Method(typeof(AudioDevicesSettings), "load");
        yield return AccessTools.Method(typeof(osu.Game.Overlays.Settings.Sections.Gameplay.HUDSettings), "load");
    }

    static void Postfix(SettingsSubsection __instance, FillFlowContainer ___FlowContent)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance)) return;
        var prefs = SomsClientPreferences.Instance;
        if (__instance is SongSelectSettings)
            add("map-pp", "pp за карту", "Оценка pp за FC при 95%, 98%, 99% и 100% точности с выбранными модами.", prefs.ShowMapPP);
        else if (__instance is AudioDevicesSettings)
            add("volume", "Улучшенное изменение звука", "Alt + колесо меняет громкость из любой точки экрана, включая мультиплеер и наблюдение.", prefs.EnhancedVolume);
        else if (__instance is osu.Game.Overlays.Settings.Sections.Gameplay.HUDSettings)
            add("team-leaderboard", "Teamvs in leaderboards", "Скрывать счёт команд вместе с таблицей игроков в Team VS.", prefs.TeamVsInLeaderboards);
        else
        {
            add("smooth-trail", "Force smooth cursor trail", "Непрерывный шлейф курсора во всех скинах, даже без cursormiddle.png.", prefs.ForceSmoothCursorTrail);
            add("sd-restart", "Sudden death restart on miss", "Автоматически перезапускать карту при провале с Sudden Death в одиночной игре.", prefs.SuddenDeathRestart);
        }
        void add(string key, string label, string hint, Bindable<bool> preference)
        {
            string name = "soms-gameplay-" + key;
            if (__instance.Children.Any(item => item.Name == name)) return;
            var item = new SettingsItemV2(new FormCheckBox
            {
                Caption = label, HintText = hint, Current = preference.GetBoundCopy(),
            }) { Name = name, Keywords = new[] { "SOMS", key, label } };
            var existing = ___FlowContent.Children.ToArray();
            __instance.Add(item);
            if (__instance is AudioDevicesSettings)
            {
                for (int i = 0; i < existing.Length; i++) ___FlowContent.SetLayoutPosition(existing[i], i * 2);
                ___FlowContent.SetLayoutPosition(item, 1);
            }
        }
    }
}

[HarmonyPatch]
public static class SomsSmoothTrailPatch
{
    static MethodBase TargetMethod() => AccessTools.PropertyGetter(Assembly.Load("osu.Game.Rulesets.Osu").GetType("osu.Game.Rulesets.Osu.Skinning.Legacy.LegacyCursorTrail", true), "DisjointTrail");
    static void Postfix(ref bool __result)
    {
        if (SomsClientPreferences.Enabled && SomsClientPreferences.Instance.ForceSmoothCursorTrail.Value)
            __result = false;
    }
}

[HarmonyPatch(typeof(ModFailCondition), "get_RestartOnFail")]
public static class SomsSuddenDeathRestartPatch
{
    static void Postfix(ModFailCondition __instance, ref bool __result)
    {
        if (SomsClientPreferences.Enabled && __instance is ModSuddenDeath && SomsClientPreferences.Instance.SuddenDeathRestart.Value
            && SomsGameplaySeek.Players.Any(pair => pair.Key is osu.Game.Screens.Play.SoloPlayer
                && pair.Key.IsCurrentScreen() && pair.Key.GameplayState?.Ruleset.ShortName == "osu"))
            __result = true;
    }
}
