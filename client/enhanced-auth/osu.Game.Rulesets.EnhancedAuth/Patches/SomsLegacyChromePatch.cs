#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Screens.Menu;
using osu.Game.Screens.OnlinePlay;
using osu.Game.Screens.Ranking;
using osu.Game.Screens.Select;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

/// <summary>Classic scenes own their header and footer. Other screens retain normal lazer navigation.</summary>
[HarmonyPatch]
public static class SomsLegacyChromePatch
{
    private static readonly ConditionalWeakTable<OsuGame, ChromeState> states = new();

    static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(OsuGame), "UpdateAfterChildren");

    static void Postfix(OsuGame __instance)
    {
        bool enabled = SomsClientPreferences.Enabled && SomsClientPreferences.Instance.LegacyInterface.Value;
        var screen = __instance.ScreenStack?.CurrentScreen;
        var active = screen is OnlinePlayScreen online ? online.CurrentSubScreen : screen;
        bool ownsChrome = enabled && active is MainMenu or SongSelect or ResultsScreen
            && active is CompositeDrawable drawable && SomsLegacyInterfacePatch.Children(drawable)
                .OfType<SomsLegacyComponent>().Any(component => component.IsLoaded && component.Alpha > 0);
        if (!ownsChrome)
        {
            if (states.TryGetValue(__instance, out var previous)) previous.Restore();
            return;
        }

        var state = states.GetOrCreateValue(__instance);
        state.Hide(__instance.Toolbar);
        state.Hide(SomsLegacyInterfacePatch.Member<Drawable>(__instance, "osuLogo"));
        state.Hide(SomsLegacyInterfacePatch.Member<Drawable>(__instance, "screenStackFooter"));

        // OsuGame derives its top inset from toolbar height even when Alpha == 0.
        // Reclaim that strip for the classic scene without changing settings/notification overlay geometry.
        if (SomsLegacyInterfacePatch.Member<Container>(__instance, "ScreenOffsetContainer") is { } offset)
            offset.Padding = new MarginPadding { Left = offset.Padding.Left, Right = offset.Padding.Right, Bottom = offset.Padding.Bottom };
    }

    private sealed class ChromeState
    {
        private readonly Dictionary<Drawable, (float Alpha, bool AlwaysPresent)> hidden = new();

        public ChromeState() { }

        public void Hide(Drawable? drawable)
        {
            if (drawable == null || SomsDrawableLifecycle.IsDisposed(drawable)) return;
            if (!hidden.TryGetValue(drawable, out var saved))
                hidden.Add(drawable, (drawable.Alpha, drawable.AlwaysPresent));
            else if (drawable.Alpha > 0)
                hidden[drawable] = (drawable.Alpha, saved.AlwaysPresent);
            drawable.Alpha = 0;
            drawable.AlwaysPresent = false;
        }

        public void Restore()
        {
            foreach (var entry in hidden)
            {
                if (SomsDrawableLifecycle.IsDisposed(entry.Key)) continue;
                entry.Key.Alpha = entry.Value.Alpha;
                entry.Key.AlwaysPresent = entry.Value.AlwaysPresent;
            }
            hidden.Clear();
        }
    }
}
