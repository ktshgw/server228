#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Screens;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Screens.Ranking;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>A separate ranking scene. Native results still own replay downloads, retry and detailed statistics.</summary>
public sealed partial class SomsLegacyResults : SomsLegacyComponent
{
    // Stable skin positions are documented against 1024 x 768. Our virtual scene is 640 x 480.
    private const float skin_scale = 0.625f;
    private static readonly Color4 panel_colour = new(20, 27, 34, 232);
    private static readonly Color4 accent = new(239, 113, 164, 255);
    private readonly ResultsScreen owner;
    private Bindable<ScoreInfo?>? selected;
    private bool details;
    private ResultButton? replayButton;
    private ClickableContainer? nativeReplayButton;

    public SomsLegacyResults(ResultsScreen owner)
    {
        this.owner = owner;
        Name = "soms-legacy-results";
    }

    protected override void LoadComplete()
    {
        selected = owner.SelectedScore.GetBoundCopy();
        selected.BindValueChanged(_ => RequestRefresh());
        base.LoadComplete();
    }

    protected override void Rebuild(ISkinSource skin)
    {
        replayButton = null;
        nativeReplayButton = null;
        var score = selected?.Value;
        var native = SomsLegacyInterfacePatch.Member<Drawable>(owner, "VerticalScrollContent");
        if (score == null || native == null) return;
        Alpha = 1;
        var scaler = new DrawSizePreservingFillContainer { TargetDrawSize = new Vector2(640, 480) };
        var scene = new Container
        {
            // Stable keeps its results panel against the left screen edge on widescreen displays.
            // Only the scale is based on 640 x 480; the usable viewport is not a centred 4:3 rectangle.
            Name = "soms-legacy-results-canvas", RelativeSizeAxes = Axes.Both,
        };
        // Both views use the same classic navigation strip. Keep native download workers alive.
        var bottom = SomsLegacyInterfacePatch.Member<Drawable>(owner, "bottomPanel");
        if (bottom != null) HideNative(new[] { bottom }, keepUpdating: true);
        if (!details)
        {
            // Preserve native download and score updates while this scene owns mouse input.
            HideNative(new[] { native }, keepUpdating: true);
            AddInternal(new InputShield { RelativeSizeAxes = Axes.Both });
            AddInternal(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(5, 10, 16, 200) });
            scaler.Add(scene);
            AddInternal(scaler);
            createRanking(scene, skin, score);
        }
        else
        {
            scaler.Add(scene);
            AddInternal(scaler);
        }
        scene.Add(new ResultButton(skin, "menu-back", "Назад", back)
        {
            Name = "soms-legacy-results-back", Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft, Size = new Vector2(104, 38),
        });
        scene.Add(new ResultButton(skin, null, details ? "Классический результат" : "Подробная статистика", () =>
        {
            details = !details;
            RequestRefresh();
        })
        {
            Name = "soms-legacy-results-details", Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft,
            Position = new Vector2(116, -5), Size = new Vector2(179, 28),
        });
    }

    private void createRanking(Container scene, ISkin skin, ScoreInfo score)
    {
        var metadata = score.BeatmapInfo?.Metadata;
        scene.Add(new Box { RelativeSizeAxes = Axes.X, Height = 62, Colour = new Color4(6, 10, 15, 226) });
        scene.Add(new Box { RelativeSizeAxes = Axes.X, Y = 61, Height = 1, Colour = accent });
        scene.Add(label("soms-result-map", $"{metadata?.Artist} - {metadata?.Title}", 15, 10, 7, 442));
        scene.Add(label("soms-result-difficulty", $"[{score.BeatmapInfo?.DifficultyName}] · {metadata?.Author.Username}", 11, 10, 26, 442));
        scene.Add(label("soms-result-player", $"{score.User.Username} · {score.Date.ToLocalTime():dd.MM.yyyy HH:mm}", 11, 10, 43, 442));
        if (!naturalArt(scene, skin, "ranking-title", -15, 12, Anchor.TopRight, Anchor.TopRight))
        {
            var title = label("soms-result-title", "Результат", 25, -10, 18, 169);
            title.Anchor = title.Origin = Anchor.TopRight;
            scene.Add(title);
        }

        // Keep a ranking-panel's natural aspect and stable origin; skins may contain additional decoration.
        bool hasPanel = naturalArt(scene, skin, "ranking-panel", 0, 102 * skin_scale, Anchor.TopLeft);
        if (!hasPanel)
        {
            scene.Add(new Container
            {
                Name = "soms-result-panel-fallback",
                Position = new Vector2(0, 72), Size = new Vector2(386, 349), Masking = true, CornerRadius = 5,
                Children = new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = panel_colour },
                    new Box { RelativeSizeAxes = Axes.X, Height = 3, Colour = accent },
                },
            });
            scene.Add(label("soms-result-score-label", "Общий счёт", 10, 17, 77, 105));
        }
        // Numeric origins and row spacing are measured against the installed stable screenshot008
        // and its authored ranking-panel, not recovered from stable's obfuscated implementation.
        number(scene, skin, "soms-result-score", score.TotalScore.ToString("D8", CultureInfo.InvariantCulture), 135, 77, 220, 36);
        var rows = statistics(score);
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            // osu! stable uses three rows: 300/geki, 100/katu, 50/miss. Lazer has no osu! geki/katu
            // counts, but the remaining results must not move into their positions.
            int slot = score.Ruleset.OnlineID == 0 ? row.Result switch
            {
                HitResult.Great => 0, HitResult.Ok => 2, HitResult.Meh => 4, HitResult.Miss => 5, _ => i,
            } : i;
            float column = slot % 2 * 320 * skin_scale, rowOffset = slot / 2 * 97 * skin_scale;
            var icon = new Container
            {
                Name = "soms-result-hit-" + row.Result, Origin = Anchor.Centre,
                Position = new Vector2(64 * skin_scale + column, 252 * skin_scale + rowOffset), Size = new Vector2(60, 32),
            };
            // Stable ranking uses static hit sprites even when the gameplay hitburst is animated.
            // Skins can deliberately supply a blank hit300-0 and a separate labelled hit300 image.
            var graphic = score.Ruleset.OnlineID == 0 ? rankingHitSprite(skin, row.Asset) : usableSprite(skin, row.Asset);
            if (graphic != null) icon.Add(graphic);
            else icon.Add(new OsuSpriteText
            {
                Text = row.Title, Font = OsuFont.Default.With(size: row.Title.Length > 5 ? 11 : 22, weight: FontWeight.Bold),
                Colour = row.Colour, Anchor = Anchor.Centre, Origin = Anchor.Centre, Shadow = true,
            });
            scene.Add(icon);
            number(scene, skin, "soms-result-count-" + row.Result, row.Count.ToString(CultureInfo.InvariantCulture), 80 + column, 145 + rowOffset, 105, 26);
        }
        // Lazer does not store stable's osu! geki/katu combo bursts. Do not manufacture their counts.
        if (rows.Length <= 4)
        {
            string extra = score.Ruleset.OnlineID == 0
                ? sliderSummary(score)
                : score.Ruleset.OnlineID == 1 && score.Statistics.TryGetValue(HitResult.LargeBonus, out int strong)
                    ? $"Усиленные ноты: {strong:N0}" : "";
            if (extra.Length > 0) scene.Add(label("soms-result-extra", extra, 9, 17, 407, 355));
        }
        if (!naturalArt(scene, skin, "ranking-maxcombo", 8 * skin_scale, 480 * skin_scale, Anchor.TopLeft))
            scene.Add(label("soms-result-combo-label", "Макс. комбо", 12, 17, 300, 163));
        if (!naturalArt(scene, skin, "ranking-accuracy", 291 * skin_scale, 480 * skin_scale, Anchor.TopLeft))
            scene.Add(label("soms-result-accuracy-label", "Точность", 12, 199, 300, 169));
        number(scene, skin, "soms-result-combo", score.MaxCombo.ToString(CultureInfo.InvariantCulture) + "x", 16, 325, 165, 33);
        number(scene, skin, "soms-result-accuracy", (score.Accuracy * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%", 198, 325, 171, 33);
        if (score.PP.HasValue)
            scene.Add(label("soms-result-pp", score.PP.Value.ToString("0.##", CultureInfo.InvariantCulture) + " pp", 18, 17, 381, 350));
        scene.Add(label("soms-result-mode", modeName(score.Ruleset.OnlineID), 11, 17, 363, 350));

        string grade = score.Rank.ToString().ToUpperInvariant();
        if (!naturalArt(scene, skin, "ranking-" + grade, -120, 200, Anchor.Centre, anchor: Anchor.TopRight))
        {
            scene.Add(new Container
            {
                Name = "soms-result-rank", Anchor = Anchor.TopRight, Origin = Anchor.Centre,
                Position = new Vector2(-120, 200), Size = new Vector2(241, 214),
                Child = new OsuSpriteText
                {
                    Text = grade is "X" or "XH" ? "SS" : grade,
                    Font = OsuFont.Default.With(size: grade is "X" or "XH" ? 116 : 151, weight: FontWeight.Bold),
                    Colour = rankColour(grade), Anchor = Anchor.Centre, Origin = Anchor.Centre, Shadow = true,
                },
            });
        }
        var mods = new FillFlowContainer
        {
            Name = "soms-result-mods", Anchor = Anchor.TopRight, Origin = Anchor.TopRight,
            Position = new Vector2(-13, 286), Size = new Vector2(226, 54),
            Direction = FillDirection.Full, Spacing = new Vector2(3),
        };
        foreach (var mod in score.Mods)
        {
            var icon = usableSprite(skin, modAsset(mod.Acronym));
            mods.Add(new Container
            {
                Size = new Vector2(37, 27),
                Children = icon != null ? new[] { icon } : new Drawable[]
                {
                    new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(90, 70, 100, 230) },
                    new OsuSpriteText
                    {
                        Text = mod.Acronym, Font = OsuFont.Default.With(size: 13, weight: FontWeight.Bold),
                        Anchor = Anchor.Centre, Origin = Anchor.Centre,
                    },
                },
            });
        }
        scene.Add(mods);

        var retry = findNative<RetryButton>(owner);
        if (retry != null)
            scene.Add(new ResultButton(skin, firstAsset(skin, "pause-retry", "ranking-retry"), "Ещё раз", () => retry.TriggerClick())
            {
                Name = "soms-result-retry", Anchor = Anchor.TopRight, Origin = Anchor.TopRight,
                Position = new Vector2(0, 343), Size = new Vector2(206, 43),
            });
        var replay = findNative<ReplayDownloadButton>(owner);
        if (replay != null)
        {
            nativeReplayButton = SomsLegacyInterfacePatch.Member<ClickableContainer>(replay, "button");
            scene.Add(replayButton = new ResultButton(skin, firstAsset(skin, "pause-replay", "ranking-replay"), "Смотреть повтор", () => nativeReplayButton?.TriggerClick())
            {
                Name = "soms-result-replay", Anchor = Anchor.TopRight, Origin = Anchor.TopRight,
                Position = new Vector2(0, retry != null ? 394 : 343), Size = new Vector2(206, 43),
            });
        }
    }

    private void back()
    {
        if (!owner.IsCurrentScreen()) return;
        if (details) { details = false; RequestRefresh(); return; }
        if (!owner.OnBackButton()) owner.Exit();
    }

    protected override void Update()
    {
        base.Update();
        if (replayButton != null && nativeReplayButton != null)
        {
            replayButton.Enabled.Value = nativeReplayButton.Enabled.Value;
            replayButton.Alpha = nativeReplayButton.Enabled.Value ? 1 : .45f;
            string caption = !nativeReplayButton.Enabled.Value ? "Повтор недоступен" : nativeReplayButton is DownloadButton download ? download.State.Value switch
            {
                DownloadState.NotDownloaded => "Скачать повтор",
                DownloadState.Downloading => "Загрузка повтора…",
                DownloadState.Importing => "Импорт повтора…",
                _ => "Смотреть повтор",
            } : "Смотреть повтор";
            replayButton.SetText(caption);
        }
    }

    protected override void RestoreLayout()
    {
        replayButton = null;
        nativeReplayButton = null;
    }

    private T? findNative<T>(CompositeDrawable root) where T : Drawable
    {
        foreach (var child in SomsLegacyInterfacePatch.Children(root))
        {
            if (child == this) continue;
            if (child is T match) return match;
            if (child is CompositeDrawable composite && findNative<T>(composite) is { } nested) return nested;
        }
        return null;
    }

    private readonly record struct ResultStat(HitResult Result, string Asset, string Title, int Count, Color4 Colour);

    private static ResultStat[] statistics(ScoreInfo score)
    {
        var blue = new Color4(103, 211, 255, 255);
        var green = new Color4(135, 233, 85, 255);
        var yellow = new Color4(245, 216, 70, 255);
        var red = new Color4(248, 90, 112, 255);
        ResultStat stat(HitResult result, string asset, string title, Color4 colour)
            => new(result, asset, title, score.Statistics.GetValueOrDefault(result), colour);
        return score.Ruleset.OnlineID switch
        {
            1 => new[]
            {
                stat(HitResult.Great, "taiko-hit300", "300", blue),
                stat(HitResult.Ok, "taiko-hit100", "100", green),
                stat(HitResult.Miss, "taiko-hit0", "MISS", red),
            },
            2 => new[]
            {
                stat(HitResult.Great, "fruit-apple", "Фрукты", blue),
                stat(HitResult.LargeTickHit, "fruit-drop", "Капли", green),
                stat(HitResult.SmallTickHit, "fruit-drop", "Капельки", yellow),
                stat(HitResult.SmallTickMiss, "", "Мимо капель", yellow),
                stat(HitResult.Miss, "hit0", "MISS", red),
            },
            3 => new[]
            {
                stat(HitResult.Perfect, "mania-hit300g", "MAX", blue),
                stat(HitResult.Great, "mania-hit300", "300", blue),
                stat(HitResult.Good, "mania-hit200", "200", green),
                stat(HitResult.Ok, "mania-hit100", "100", green),
                stat(HitResult.Meh, "mania-hit50", "50", yellow),
                stat(HitResult.Miss, "mania-hit0", "MISS", red),
            },
            _ => new[]
            {
                stat(HitResult.Great, "hit300", "300", blue),
                stat(HitResult.Ok, "hit100", "100", green),
                stat(HitResult.Meh, "hit50", "50", yellow),
                stat(HitResult.Miss, "hit0", "MISS", red),
            },
        };
    }

    private static string sliderSummary(ScoreInfo score)
    {
        var parts = new List<string>();
        if (score.MaximumStatistics.TryGetValue(HitResult.LargeTickHit, out int ticks) && ticks > 0)
            parts.Add($"Тики: {score.Statistics.GetValueOrDefault(HitResult.LargeTickHit)}/{ticks}");
        if (score.MaximumStatistics.TryGetValue(HitResult.SliderTailHit, out int tails) && tails > 0)
            parts.Add($"Концы слайдеров: {score.Statistics.GetValueOrDefault(HitResult.SliderTailHit)}/{tails}");
        return string.Join(" · ", parts);
    }

    private static string modeName(int mode) => mode switch { 1 => "osu!taiko", 2 => "osu!catch", 3 => "osu!mania", _ => "osu!" };
    private static Color4 rankColour(string grade) => grade switch
    {
        "XH" or "SH" => new Color4(225, 233, 241, 255),
        "X" or "S" => new Color4(251, 218, 95, 255),
        "A" => new Color4(155, 237, 88, 255),
        "B" => new Color4(103, 212, 252, 255),
        "C" => new Color4(211, 143, 245, 255),
        _ => new Color4(245, 104, 127, 255),
    };

    private static string? modAsset(string acronym) => acronym switch
    {
        "EZ" => "selection-mod-easy", "NF" => "selection-mod-nofail", "HT" => "selection-mod-halftime",
        "HR" => "selection-mod-hardrock", "SD" => "selection-mod-suddendeath", "PF" => "selection-mod-perfect",
        "DT" => "selection-mod-doubletime", "NC" => "selection-mod-nightcore", "HD" => "selection-mod-hidden",
        "FL" => "selection-mod-flashlight", "RX" => "selection-mod-relax", "AP" => "selection-mod-relax2",
        "SO" => "selection-mod-spunout", "AT" => "selection-mod-autoplay", "CN" => "selection-mod-cinema",
        "FI" => "selection-mod-fadein", "RD" => "selection-mod-random", "MR" => "selection-mod-mirror",
        "1K" or "2K" or "3K" or "4K" or "5K" or "6K" or "7K" or "8K" or "9K" => "selection-mod-key" + acronym[0],
        _ => null,
    };

    private static TruncatingSpriteText label(string name, string text, float size, float x, float y, float width) => new()
    {
        Name = name, Text = text, Font = OsuFont.Default.With(size: size), Position = new Vector2(x, y),
        Width = width, Shadow = true,
    };

    private static Drawable? usableSprite(ISkin skin, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        // Transparent 1x1 files intentionally hide skin elements. Only a missing lookup uses fallback.
        return SpriteFor(skin, name);
    }

    private static Drawable? rankingHitSprite(ISkin skin, string name)
    {
        var provider = (skin as ISkinSource)?.FindProvider(source => source.GetTexture(name) != null || source.GetTexture(name + "-0") != null) ?? skin;
        var texture = provider.GetTexture(name) ?? provider.GetTexture(name + "-0");
        return texture == null ? null : new Sprite
        {
            Texture = texture, Size = new Vector2(texture.DisplayWidth, texture.DisplayHeight) * skin_scale * .5f,
            Anchor = Anchor.Centre, Origin = Anchor.Centre,
        };
    }

    private static string? firstAsset(ISkin skin, params string[] names)
        => names.FirstOrDefault(name => (skin.GetTexture(name + "-0") ?? skin.GetTexture(name)) != null);

    private static bool naturalArt(Container canvas, ISkin skin, string name, float x, float y, Anchor origin, Anchor anchor = Anchor.TopLeft)
    {
        var drawable = NaturalSpriteFor(skin, name);
        if (drawable == null) return false;
        // The native loader chose the provider; looking up frame 0 again can accidentally
        // substitute a default animation's dimensions for a static image in the user's skin.
        var size = NaturalSpriteSize(drawable) * skin_scale;
        drawable.RelativeSizeAxes = Axes.Both;
        drawable.Size = Vector2.One;
        drawable.FillMode = FillMode.Fit;
        drawable.Anchor = drawable.Origin = Anchor.Centre;
        canvas.Add(new Container { Name = "soms-result-asset-" + name, Position = new Vector2(x, y), Anchor = anchor, Origin = origin, Size = size, Child = drawable });
        return true;
    }

    private static void number(Container canvas, ISkin skin, string name, string value, float x, float y, float width, float height)
    {
        string prefix = skin.GetFontPrefix(LegacyFont.Score);
        var glyphs = value.Select(c =>
        {
            string suffix = c switch { '.' => "dot", ',' => "comma", '%' => "percent", _ => c.ToString() };
            return (Character: c, Texture: skin.GetTexture(prefix + "-" + suffix));
        }).ToArray();
        if (!glyphs.Any(g => char.IsDigit(g.Character) && g.Texture != null))
        {
            canvas.Add(label(name, value, height * .84f, x, y, width));
            return;
        }

        // A font has one scale. In particular, a short dot/x texture must not be independently
        // enlarged to the same height as a digit. DisplaySize already incorporates @2x.
        float fontHeight = Enumerable.Range(0, 10).Select(n => skin.GetTexture(prefix + "-" + n))
            .OfType<Texture>().Select(t => t.DisplayHeight).DefaultIfEmpty(height).Max();
        canvas.Add(new LegacyNumber(name, glyphs, fontHeight, skin.GetFontOverlap(LegacyFont.Score))
        {
            Position = new Vector2(x, y), Size = new Vector2(width, height),
        });
    }

    private sealed partial class LegacyNumber : Container
    {
        private readonly FillFlowContainer glyphFlow;
        private readonly float fontHeight;

        public LegacyNumber(string name, IReadOnlyList<(char Character, Texture? Texture)> glyphs, float fontHeight, float overlap)
        {
            Name = name;
            this.fontHeight = Math.Max(1, fontHeight);
            Add(glyphFlow = new FillFlowContainer
            {
                Name = name + "-glyphs", AutoSizeAxes = Axes.Both, Direction = FillDirection.Horizontal,
                Spacing = new Vector2(-overlap, 0),
            });
            for (int i = 0; i < glyphs.Count; i++)
            {
                var glyph = glyphs[i];
                if (glyph.Texture is { } texture)
                    glyphFlow.Add(new Sprite
                    {
                        Name = $"{name}-glyph-{glyph.Character}-{i}", Texture = texture,
                        Size = new Vector2(texture.DisplayWidth, texture.DisplayHeight),
                    });
                else
                    // A missing suffix or a partial font only falls back for that character.
                    glyphFlow.Add(new Container
                    {
                        AutoSizeAxes = Axes.X, Height = this.fontHeight,
                        Child = new OsuSpriteText
                        {
                            Name = $"{name}-fallback-{glyph.Character}-{i}", Text = glyph.Character.ToString(),
                            Font = OsuFont.Default.With(size: this.fontHeight * .84f), Shadow = true,
                            Anchor = Anchor.BottomLeft, Origin = Anchor.BottomLeft,
                        },
                    });
            }
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();
            // Wide suffix images can contain decoration or transparent padding. They must neither
            // shrink the digits nor be clipped to the numeric field's nominal width.
            float scale = DrawHeight / fontHeight;
            glyphFlow.Scale = new Vector2(scale);
        }
    }

    private sealed partial class InputShield : Container
    {
        public override bool HandlePositionalInput => true;
        protected override bool OnMouseDown(MouseDownEvent e) => true;
        protected override bool OnClick(ClickEvent e) => true;
        protected override bool OnScroll(ScrollEvent e) => true;
    }

    private sealed partial class ResultButton : ClickableContainer, IHasTooltip
    {
        private readonly Box highlight;
        private readonly OsuSpriteText? caption;
        public LocalisableString TooltipText { get; private set; }

        public ResultButton(ISkin skin, string? asset, string text, Action action)
        {
            Action = action;
            TooltipText = text;
            var graphic = usableSprite(skin, asset);
            if (graphic != null) Add(graphic);
            else
            {
                Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(71, 48, 64, 245) });
                Add(new Box { Width = 3, RelativeSizeAxes = Axes.Y, Colour = accent });
                Add(caption = new OsuSpriteText
                {
                    Text = text, Font = OsuFont.Default.With(size: 13),
                    Anchor = Anchor.Centre, Origin = Anchor.Centre, Shadow = true,
                });
            }
            Add(highlight = new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(255, 255, 255, 32), Alpha = 0 });
        }

        public void SetText(string text)
        {
            TooltipText = text;
            if (caption != null) caption.Text = text;
        }

        protected override bool OnHover(HoverEvent e) { highlight.FadeIn(90); return true; }
        protected override void OnHoverLost(HoverLostEvent e) => highlight.FadeOut(120);
    }

    protected override void Dispose(bool isDisposing)
    {
        selected?.UnbindAll();
        base.Dispose(isDisposing);
    }
}
