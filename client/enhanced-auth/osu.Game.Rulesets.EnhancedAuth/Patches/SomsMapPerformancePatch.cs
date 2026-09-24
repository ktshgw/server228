#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Screens.Select;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(BeatmapTitleWedge), "load")]
public static class SomsMapPerformancePatch
{
    private static readonly PropertyInfo internalChildren = AccessTools.Property(typeof(CompositeDrawable), "InternalChildren");
    private static readonly Action<CompositeDrawable, bool> setMasking = AccessTools.MethodDelegate<Action<CompositeDrawable, bool>>(
        AccessTools.PropertySetter(typeof(CompositeDrawable), "Masking"));
    static void Postfix(BeatmapTitleWedge __instance)
    {
        if (!SomsClientPreferences.Enabled) return;
        var children = ((IEnumerable<Drawable>)AccessTools.Property(typeof(CompositeDrawable), "InternalChildren").GetValue(__instance)!).ToArray();
        if (children.Any(child => child.Name == "soms-map-pp")) return;
        var labels = new[] { "titleLabel", "artistLabel" }.Select(name =>
            (MarqueeContainer)AccessTools.Field(typeof(BeatmapTitleWedge), name).GetValue(__instance)!).ToArray();
        var headers = new[] { "titleLink", "artistLink" }.Select(name =>
            (Container)AccessTools.Property(typeof(Drawable), "Parent").GetValue(AccessTools.Field(typeof(BeatmapTitleWedge), name).GetValue(__instance))!).ToArray();
        var preview = new SomsMapPerformance { Name = "soms-map-pp", Anchor = Anchor.TopRight, Origin = Anchor.TopRight,
            Position = new osuTK.Vector2(-24, __instance.TopPadding + 32), Width = 260, Height = 83,
            Depth = -1, Shear = -OsuGame.SHEAR, DifficultySource = () => findRating(__instance) };
        AccessTools.Method(typeof(CompositeDrawable), "AddInternal").Invoke(__instance, new object[] { preview });
        var originalPadding = headers.Select(header => header.Padding).ToArray();
        var originalMasking = labels.Select(label => label.Masking).ToArray();
        // MarqueeContainer deliberately draws outside its width. Clip the text
        // itself, and derive the available width from the actual counter geometry
        // (including the header's shear/scale), not a fixed guessed padding.
        preview.UpdateHeaderLayout = () =>
        {
            bool clip = SomsClientPreferences.Enabled && SomsClientPreferences.Instance.ShowMapPP.Value;
            for (int i = 0; i < headers.Length; i++)
            {
                if (SomsDrawableLifecycle.IsDisposed(headers[i]) || SomsDrawableLifecycle.IsDisposed(labels[i])) continue;
                var padding = originalPadding[i];
                if (clip)
                {
                    float edge = Math.Min(preview.ToSpaceOfOtherDrawable(osuTK.Vector2.Zero, headers[i]).X,
                        preview.ToSpaceOfOtherDrawable(new osuTK.Vector2(0, preview.DrawHeight), headers[i]).X);
                    padding.Right = Math.Max(padding.Right, headers[i].DrawWidth - Math.Max(padding.Left, edge - 12));
                }
                headers[i].Padding = padding;
                if (labels[i].Masking != (clip || originalMasking[i])) setMasking(labels[i], clip || originalMasking[i]);
            }
        };
    }

    private static StarRatingDisplay? findRating(CompositeDrawable root)
    {
        foreach (var child in (IEnumerable<Drawable>)internalChildren.GetValue(root)!)
        {
            if (child is StarRatingDisplay rating) return rating;
            if (child is CompositeDrawable composite && findRating(composite) is { } nested) return nested;
        }
        return null;
    }
}
