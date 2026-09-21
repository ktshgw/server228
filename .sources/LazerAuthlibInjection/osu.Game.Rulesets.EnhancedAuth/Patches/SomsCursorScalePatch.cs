using System.Linq;
using HarmonyLib;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Graphics.Cursor;
using osuTK;
using ScaleContainer = osu.Game.Graphics.Containers.ScalingContainer.ScalingDrawSizePreservingFillContainer;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(MenuCursorContainer.Cursor), "load")]
public static partial class SomsCursorScalePatch
{
    static void Postfix(MenuCursorContainer.Cursor __instance)
    {
        if (__instance.Children.Any(child => child is CursorVisualScale)) return;
        var visuals = __instance.Children.ToArray();
        __instance.Clear(false);
        __instance.Add(new CursorVisualScale { Children = visuals });
    }

    // Only the graphics get compensation. The cursor's mouse position, press
    // animations and the inner native MenuCursorSize binding keep their owners.
    private partial class CursorVisualScale : Container
    {
        public CursorVisualScale() => AutoSizeAxes = Axes.Both;
        protected override void Update()
        {
            base.Update();
            float compensation = 1;
            for (var ancestor = Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ancestor is not ScaleContainer scaling) continue;
                compensation = SomsInterfaceScalePatch.CursorCompensation(scaling);
                break;
            }
            Scale = new Vector2(compensation);
        }
    }
}
