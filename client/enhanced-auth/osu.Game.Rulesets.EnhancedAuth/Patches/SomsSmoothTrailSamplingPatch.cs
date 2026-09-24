#nullable enable
using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Textures;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Skinning;
using osuTK;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

// Reuse lazer's GPU trail buffer, but sample like McOsu: quarter-width spacing
// along the actual mouse path, up to the cursor, without InputResampler latency
// or lazer's intentional exclusion zone around cursormiddle.
public static class SomsSmoothTrailSampling
{
    internal static readonly Type LegacyType = Assembly.Load("osu.Game.Rulesets.Osu").GetType("osu.Game.Rulesets.Osu.Skinning.Legacy.LegacyCursorTrail", true)!;
    internal static readonly Type TrailType = LegacyType.BaseType!;
    private static readonly FieldInfo disjoint = AccessTools.Field(LegacyType, "<DisjointTrail>k__BackingField");
    private static readonly FieldInfo skin = AccessTools.Field(LegacyType, "skin");
    private static readonly PropertyInfo origin = AccessTools.Property(TrailType, "TrailOrigin");
    private static readonly FieldInfo nativeLast = AccessTools.Field(TrailType, "lastPosition");
    private static readonly FieldInfo resampler = AccessTools.Field(TrailType, "resampler");
    private static readonly Type configType = LegacyType.Assembly.GetType("osu.Game.Rulesets.Osu.Skinning.OsuSkinConfiguration", true)!;
    private static readonly object centreKey = Enum.Parse(configType, "CursorCentre");
    private static readonly MethodInfo getConfig = typeof(ISkin).GetMethod("GetConfig")!.MakeGenericMethod(configType, typeof(bool));
    private static readonly ConditionalWeakTable<Drawable, TrailState> states = new();
    private static readonly Func<Drawable, Texture?> texture = getter<Texture?>("Texture");
    private static readonly Func<Drawable, Vector2> scale = getter<Vector2>("CursorScale");
    private static readonly Func<Drawable, Vector2> expansion = getter<Vector2>("NewPartScale");
    private static readonly Action<Drawable, Vector2> add = makeAdd();
    private sealed class TrailState
    {
        public bool? Forced;
        public Vector2? Last;
    }
    internal static bool Forced => SomsClientPreferences.Enabled && SomsClientPreferences.Instance.ForceSmoothCursorTrail.Value;

    private static Func<Drawable, T> getter<T>(string name)
    {
        var instance = Expression.Parameter(typeof(Drawable));
        return Expression.Lambda<Func<Drawable, T>>(Expression.Property(Expression.Convert(instance, TrailType), name), instance).Compile();
    }
    private static Action<Drawable, Vector2> makeAdd()
    {
        var instance = Expression.Parameter(typeof(Drawable));
        var position = Expression.Parameter(typeof(Vector2));
        return Expression.Lambda<Action<Drawable, Vector2>>(Expression.Call(Expression.Convert(instance, TrailType),
            AccessTools.Method(TrailType, "addPart"), position), instance, position).Compile();
    }
    internal static void UpdateStyle(Drawable trail)
    {
        var state = states.GetOrCreateValue(trail);
        bool forced = Forced;
        if (state.Forced == forced) return;
        state.Forced = forced;
        state.Last = null;
        nativeLast.SetValue(trail, null);
        resampler.SetValue(trail, Activator.CreateInstance(resampler.FieldType));
        bool originalDisjoint = (bool)disjoint.GetValue(trail)!;
        var source = (ISkin)skin.GetValue(trail)!;
        bool centred = (getConfig.Invoke(source, new[] { centreKey }) as IBindable<bool>)?.Value ?? true;
        trail.Blending = forced || !originalDisjoint ? BlendingParameters.Additive : BlendingParameters.Inherit;
        origin.SetValue(trail, forced || !originalDisjoint || centred ? Anchor.Centre : Anchor.TopLeft);
    }

    // Returns true to execute the untouched native sampler when disabled.
    internal static bool Sample(Drawable trail, Vector2 screenPosition)
    {
        if (!LegacyType.IsInstanceOfType(trail) || !Forced) return true;
        UpdateStyle(trail);
        var image = texture(trail);
        if (image == null) return false;
        var state = states.GetOrCreateValue(trail);
        var position = trail.ToLocalSpace(screenPosition);
        float width = image.DisplayWidth * Math.Abs(scale(trail).X * expansion(trail).X);
        float step = Math.Max(.5f, width / 4);
        if (state.Last is not { } previous)
        {
            add(trail, position);
            state.Last = position;
            return false;
        }
        var delta = position - previous;
        float length = delta.Length;
        if (!float.IsFinite(length) || length < step) return false;
        int count = (int)Math.Min(1_000_000, Math.Floor(length / step));
        // Bound work on a teleport/tablet warp to half the native 2048-part buffer.
        var direction = delta / length;
        for (int i = Math.Max(1, count - 1023); i <= count; i++) add(trail, previous + direction * (i * step));
        state.Last = previous + direction * (count * step);
        return false;
    }
}

[HarmonyPatch]
public static class SomsSmoothTrailSamplingPatch
{
    static MethodBase TargetMethod() => AccessTools.Method(SomsSmoothTrailSampling.TrailType, "AddTrail");
    static bool Prefix(Drawable __instance, Vector2 position) => SomsSmoothTrailSampling.Sample(__instance, position);
}

[HarmonyPatch]
public static class SomsSmoothTrailStylePatch
{
    static MethodBase TargetMethod() => AccessTools.Method(SomsSmoothTrailSampling.LegacyType, "Update");
    static void Prefix(Drawable __instance) => SomsSmoothTrailSampling.UpdateStyle(__instance);
}
