#nullable enable
using System;
using System.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Screens.Play;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>Stable's centred pause/fail menu; native buttons retain the actual player actions.</summary>
public sealed partial class SomsLegacyGameplayMenu : SomsLegacyComponent
{
    private readonly GameplayMenuOverlay owner;

    public SomsLegacyGameplayMenu(GameplayMenuOverlay owner)
    {
        this.owner = owner;
        Name = "soms-legacy-gameplay-menu";
    }

    protected override void Rebuild(ISkinSource skin)
    {
        var originals = SomsLegacyInterfacePatch.Children(owner)
            .Where(child => child != this && child is not SkinnableSound).ToArray();
        HideNative(originals, keepUpdating: true);
        Alpha = 1;

        bool failed = owner is FailOverlay;
        AddInternal(new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black, Alpha = 0.7f });
        var background = SomsLegacyOverlayButton.Art(skin, failed ? "fail-background" : "pause-overlay", FillMode.Fit);
        if (background != null) AddInternal(background);

        var scaling = new DrawSizePreservingFillContainer { TargetDrawSize = new Vector2(1024, 768) };
        var canvas = SomsLegacyOverlayButton.Canvas(this);
        scaling.Add(canvas);
        AddInternal(scaling);

        if (background == null)
            canvas.Add(new TruncatingSpriteText
            {
                Name = "soms-legacy-pause-heading",
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Position = new Vector2(0, 125),
                MaxWidth = 720,
                Text = failed ? "Неудачная попытка" : "Пауза",
                Font = SomsLegacyFont.Font(50, bold: true),
                Shadow = true,
            });

        var flow = new FillFlowContainer
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Width = 430,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Spacing = new Vector2(0, 12),
        };
        canvas.Add(flow);
        int index = 0;
        addButton(owner.OnResume, "pause-continue", "Продолжить", new Color4(106, 216, 134, 255));
        addButton(owner.OnRetry, "pause-retry", "Повторить", new Color4(250, 211, 98, 255));
        addButton(owner.OnQuit, "pause-back", "Вернуться к выбору карт", new Color4(241, 102, 144, 255));

        // Preserve fail-screen replay saving as well, without exposing the lazer footer layout.
        if (failed && owner.FooterContent != null)
        {
            var save = descendants(owner.FooterContent).OfType<osu.Game.Graphics.Containers.OsuClickableContainer>().FirstOrDefault();
            if (save != null)
                canvas.Add(new SomsLegacyOverlayButton(skin, "", "Сохранить реплей", () => save.TriggerClickWithSound(), new Color4(132, 195, 243, 255))
                {
                    Anchor = Anchor.BottomCentre,
                    Origin = Anchor.BottomCentre,
                    Position = new Vector2(0, -40),
                    Size = new Vector2(330, 50),
                });
        }

        void addButton(Action? callback, string asset, string text, Color4 colour)
        {
            if (callback == null) return;
            var native = owner.Buttons?.ElementAtOrDefault(index++);
            flow.Add(new SomsLegacyOverlayButton(skin, asset, text, () =>
            {
                if (native != null) native.TriggerClickWithSound();
                else { callback(); owner.Hide(); }
            }, colour)
            {
                Name = "soms-legacy-" + asset,
                RelativeSizeAxes = Axes.X,
                Height = 82,
            });
        }
    }

    private static System.Collections.Generic.IEnumerable<Drawable> descendants(CompositeDrawable root)
    {
        foreach (var child in SomsLegacyInterfacePatch.Children(root))
        {
            yield return child;
            if (child is CompositeDrawable composite)
                foreach (var descendant in descendants(composite)) yield return descendant;
        }
    }
}
