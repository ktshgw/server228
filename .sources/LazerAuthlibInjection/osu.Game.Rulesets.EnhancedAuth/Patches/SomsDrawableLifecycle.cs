using System;
using System.Reflection;
using HarmonyLib;
using osu.Framework.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

internal static class SomsDrawableLifecycle
{
    // These framework lifecycle members are internal in supported lazer builds.
    private static readonly Func<Drawable, bool> isDisposed =
        (Func<Drawable, bool>)Delegate.CreateDelegate(typeof(Func<Drawable, bool>),
            AccessTools.PropertyGetter(typeof(Drawable), "IsDisposed"));
    private static readonly MethodInfo addDispose = AccessTools.Event(typeof(Drawable), "OnDispose").GetAddMethod(true);

    internal static bool IsDisposed(Drawable drawable) => isDisposed(drawable);
    internal static void OnDispose(Drawable drawable, Action action) => addDispose.Invoke(drawable, [action]);
}
