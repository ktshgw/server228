#nullable enable
using System;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.Matchmaking;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>Keep format, ruleset and actions within the queue panel at any window size.</summary>
public sealed partial class SomsRankedQueueControls : CompositeDrawable
{
    private readonly Bindable<MatchmakingPool[]?> available;
    private readonly Bindable<MatchmakingPool?> selected;
    private readonly FillFlowContainer choices = SomsNativeMatchScreen.Flow();
    private bool disposed;
    private static readonly Color4 selectedColour = new(42, 125, 143, 255);
    private static readonly Color4 idleColour = new(64, 53, 76, 255);

    public SomsRankedQueueControls(Bindable<MatchmakingPool[]?> pools, Bindable<MatchmakingPool?> selection,
                                  Drawable beginQueueing, Drawable duelHint, Action openParty)
    {
        Name = "soms-ranked-controls";
        RelativeSizeAxes = Axes.Both;
        available = pools.GetBoundCopy();
        selected = selection.GetBoundCopy();

        var party = new CompactButton
        {
            Name = "soms-ranked-party",
            Text = "Пати 2v2",
            TooltipText = "Пригласить напарника или принять приглашение. Поиск запускает капитан.",
            BackgroundColour = idleColour,
            Action = openParty,
        };
        var content = SomsNativeMatchScreen.Flow();
        content.Padding = new MarginPadding(4);
        content.Spacing = new Vector2(0, 8);
        choices.Spacing = new Vector2(0, 8);
        content.Add(choices);
        content.Add(Row(42, beginQueueing, party));
        duelHint.Anchor = duelHint.Origin = Anchor.TopLeft;
        content.Add(duelHint);
        InternalChild = new OsuScrollContainer
        {
            RelativeSizeAxes = Axes.Both,
            Child = content,
        };
        available.BindValueChanged(_ => rebuildChoices());
        selected.BindValueChanged(_ => rebuildChoices());
        rebuildChoices();
    }

    // The native pool DTO has no lobby-size field; SOMS names its team queues "2v2".
    internal static bool IsTeamPool(MatchmakingPool pool) => pool.Name.Contains("2v2", StringComparison.OrdinalIgnoreCase);

    private void selectFormat(bool team)
    {
        var candidates = (available.Value ?? Array.Empty<MatchmakingPool>()).Where(p => IsTeamPool(p) == team).ToArray();
        selected.Value = candidates.FirstOrDefault(p => p.RulesetId == selected.Value?.RulesetId && p.Variant == selected.Value?.Variant)
                         ?? candidates.FirstOrDefault() ?? selected.Value;
    }

    private void rebuildChoices()
    {
        if (disposed) return;
        choices.Clear();
        var pools = available.Value;
        bool team = selected.Value != null && IsTeamPool(selected.Value);
        choices.Add(Row(32, formatButton(false, "1v1 · соло"), formatButton(true, "2v2 · команды")));
        if (pools == null || pools.Length == 0)
        {
            choices.Add(SomsNativeMatchScreen.Text(pools == null ? "Загружаем очереди…" : "Нет доступных очередей", 16));
            return;
        }
        foreach (var group in pools.Where(p => IsTeamPool(p) == team).OrderBy(p => p.RulesetId).ThenBy(p => p.Variant).Chunk(5))
            choices.Add(Row(38, group.Select(pool => (Drawable)new CompactButton
            {
                Name = $"soms-ranked-pool-{pool.Id}",
                Text = ModeLabel(pool),
                TooltipText = pool.DisplayName,
                BackgroundColour = selected.Value?.Id == pool.Id ? selectedColour : idleColour,
                Action = () => selected.Value = pool,
            }).ToArray()));

        CompactButton formatButton(bool isTeam, string caption) => new()
        {
            Name = isTeam ? "soms-ranked-format-team" : "soms-ranked-format-solo",
            Text = caption,
            BackgroundColour = isTeam == team ? selectedColour : idleColour,
            Enabled = { Value = pools?.Any(pool => IsTeamPool(pool) == isTeam) == true },
            Action = () => selectFormat(isTeam),
        };
    }

    internal static string ModeLabel(MatchmakingPool pool) => pool.RulesetId switch
    {
        0 => "osu!",
        1 => "taiko",
        2 => "catch",
        3 => $"mania {pool.Variant}K",
        _ => pool.Name,
    };

    private static GridContainer Row(float height, params Drawable[] children) => new()
    {
        RelativeSizeAxes = Axes.X,
        Height = height,
        Content = new[]
        {
            children.Select(child =>
            {
                child.Anchor = child.Origin = Anchor.TopLeft;
                child.RelativeSizeAxes = Axes.Both;
                child.Size = Vector2.One;
                return (Drawable)new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Horizontal = 3 },
                    Child = child,
                };
            }).ToArray(),
        },
    };

    protected override void Dispose(bool isDisposing)
    {
        if (disposed) return;
        disposed = true;
        available.UnbindAll();
        selected.UnbindAll();
        base.Dispose(isDisposing);
    }

    private sealed partial class CompactButton : RoundedButton
    {
        protected override SpriteText CreateText() => new OsuSpriteText
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Font = OsuFont.GetFont(size: 15, weight: FontWeight.Bold),
        };
    }
}
