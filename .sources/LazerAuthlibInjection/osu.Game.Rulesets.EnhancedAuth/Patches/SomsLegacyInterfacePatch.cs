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
using osu.Game.Screens.Ranking;
using osu.Game.Screens.Select;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch]
public static class SomsLegacyInterfacePatch
{
    private static readonly ConditionalWeakTable<CompositeDrawable, HashSet<string>> attached = new();
    private static readonly MethodInfo addInternal = AccessTools.Method(typeof(CompositeDrawable), "AddInternal");
    private static readonly PropertyInfo children = AccessTools.Property(typeof(CompositeDrawable), "InternalChildren");

    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.DeclaredMethod(typeof(MainMenu), "LoadComplete");
        yield return AccessTools.DeclaredMethod(typeof(SongSelect), "LoadComplete");
        yield return AccessTools.DeclaredMethod(typeof(ResultsScreen), "LoadComplete");
    }

    static void Postfix(CompositeDrawable __instance)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance)) return;
        switch (__instance)
        {
            case MainMenu menu:
                Attach(__instance, "main-menu", () => new SomsLegacyMainMenu(menu) { Depth = -100 });
                break;
            case ResultsScreen results:
                Attach(results, "results", () => new SomsLegacyResults(results) { Depth = -100 });
                break;
            case SongSelect select:
                Attach(select, "selection", () => new SomsLegacySongSelect(select) { Depth = -100 });
                break;
        }
    }

    public static T? Member<T>(object target, string name) where T : class =>
        (AccessTools.Property(target.GetType(), name)?.GetValue(target) ?? AccessTools.Field(target.GetType(), name)?.GetValue(target)) as T;

    public static IEnumerable<Drawable> Children(CompositeDrawable target) => (IEnumerable<Drawable>)children.GetValue(target)!;

    private static void Attach(CompositeDrawable target, string key, Func<Drawable> create)
    {
        lock (attached)
        {
            var keys = attached.GetOrCreateValue(target);
            if (!keys.Add(key)) return;
            try { addInternal.Invoke(target, new object[] { create() }); }
            catch { keys.Remove(key); throw; }
        }
    }
}
