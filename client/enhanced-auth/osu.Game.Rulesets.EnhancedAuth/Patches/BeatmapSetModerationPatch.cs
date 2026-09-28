#nullable enable
using System;
using System.Linq;
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays;
using osu.Game.Overlays.BeatmapSet;
using osu.Game.Overlays.BeatmapSet.Buttons;
using osu.Game.Overlays.Dialog;
using osu.Game.Overlays.Notifications;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(BeatmapSetHeaderContent), "load")]
public static class BeatmapSetModerationPatch
{
    public static void Postfix(BeatmapSetHeaderContent __instance)
    {
        if (!GlobalConfigManager.Patched || GlobalConfigManager.Config.DisableServerExtensions)
            return;

        var content = Traverse.Create(__instance).Field("fadeContent").GetValue<FillFlowContainer>();

        // Background dependency loaders may run again when overlay dependencies
        // are refreshed. Keep one set-level toolbar instead of appending another
        // pair of buttons for every subsequent load invocation.
        if (content == null || content.Any(child => child is BeatmapModerationControls))
            return;

        content.Add(new BeatmapModerationControls(__instance.BeatmapSet, __instance.Picker.Beatmap));
    }
}

public partial class BeatmapModerationControls : CompositeDrawable
{
    private const float button_width = 175;

    private readonly Bindable<APIBeatmapSet> sourceBeatmapSet;
    private readonly Bindable<APIBeatmap?> selectedBeatmap;
    private readonly Bindable<APIBeatmapSet> beatmapSet = new();
    private readonly FillFlowContainer buttonFlow;
    private readonly HeaderButton singleDifficultyButton;
    private readonly HeaderButton allDifficultiesButton;

    [Resolved]
    private IAPIProvider api { get; set; } = null!;

    [Resolved(canBeNull: true)]
    private IDialogOverlay? dialogs { get; set; }

    [Resolved]
    private INotificationOverlay notifications { get; set; } = null!;

    [Resolved]
    private OsuGame game { get; set; } = null!;

    private APIRequest? currentRequest;
    private BeatmapModerationState? currentState;
    private int requestGeneration;
    private bool busy;

    public BeatmapModerationControls(Bindable<APIBeatmapSet> sourceBeatmapSet, Bindable<APIBeatmap?> selectedBeatmap)
    {
        this.sourceBeatmapSet = sourceBeatmapSet;
        this.selectedBeatmap = selectedBeatmap;

        RelativeSizeAxes = Axes.X;
        Height = 45;
        Margin = new MarginPadding { Top = 10 };
        Hide();

        InternalChild = buttonFlow = new FillFlowContainer
        {
            RelativeSizeAxes = Axes.Both,
            Direction = FillDirection.Horizontal,
            Spacing = new Vector2(5),
            Children = new Drawable[]
            {
                singleDifficultyButton = createButton(
                    "Difficulty",
                    "Change status for selected difficulty only",
                    () => chooseScope(allDifficulties: false)),
                allDifficultiesButton = createButton(
                    "Beatmapset",
                    "Change status for all difficulties of this map",
                    () => chooseScope(allDifficulties: true)),
            },
        };
    }

    private HeaderButton createButton(string text, string tooltip, Action action) => new HeaderButton
    {
        Width = button_width,
        Text = text,
        TooltipText = tooltip,
        Action = action,
    };

    [BackgroundDependencyLoader]
    private void load(OsuColour colours)
    {
        singleDifficultyButton.BackgroundColour = colours.Blue3;
        allDifficultiesButton.BackgroundColour = colours.Purple3;

        beatmapSet.BindTo(sourceBeatmapSet);
        beatmapSet.BindValueChanged(change => queryState(change.NewValue), true);
    }

    private void queryState(APIBeatmapSet? set)
    {
        int generation = ++requestGeneration;
        cancelCurrentRequest();
        currentState = null;
        Hide();

        if (set == null || set.OnlineID <= 0 || !api.IsLoggedIn)
            return;

        var request = new GetBeatmapModerationStateRequest(set.OnlineID);
        currentRequest = request;
        request.Success += state =>
        {
            if (generation != requestGeneration || beatmapSet.Value?.OnlineID != state.BeatmapSetId)
                return;

            currentRequest = null;
            applyState(state);
        };
        request.Failure += _ =>
        {
            if (generation != requestGeneration)
                return;

            currentRequest = null;
            Hide();
        };
        api.Queue(request);
    }

    private void chooseScope(bool allDifficulties)
    {
        APIBeatmapSet? set = beatmapSet.Value;
        if (busy || currentState == null || !currentState.Allowed || set == null || dialogs == null)
            return;

        int? beatmapId = null;
        string target;
        if (allDifficulties)
        {
            target = $"all difficulties in “{set.Title}”";
        }
        else
        {
            APIBeatmap? selected = selectedBeatmap.Value;
            if (selected == null || selected.OnlineID <= 0)
            {
                notifications.Post(new SimpleErrorNotification
                {
                    Text = "Could not determine the selected difficulty.",
                });
                return;
            }

            beatmapId = selected.OnlineID;
            target = $"difficulty “{selected.DifficultyName}”";
        }

        dialogs.Push(new BeatmapModerationActionDialog(target, action => perform(action, beatmapId)));
    }

    private void perform(string action, int? beatmapId)
    {
        APIBeatmapSet? set = beatmapSet.Value;
        if (busy || set == null || set.OnlineID <= 0)
            return;

        int beatmapSetId = set.OnlineID;
        int generation = ++requestGeneration;
        cancelCurrentRequest();
        setBusy(true);

        var request = new ApplyBeatmapModerationActionRequest(beatmapSetId, action, beatmapId);
        currentRequest = request;
        request.Success += state =>
        {
            if (generation != requestGeneration || beatmapSet.Value?.OnlineID != beatmapSetId)
                return;

            currentRequest = null;
            setBusy(false);
            applyState(state);
            postSuccess(action, beatmapId);

            // Re-fetch official metadata with the server-local status overlay so
            // the pill, difficulty statuses and available score sections update.
            game.ShowBeatmapSet(beatmapSetId);
        };
        request.Failure += exception =>
        {
            if (generation != requestGeneration)
                return;

            currentRequest = null;
            setBusy(false);
            if (currentState != null)
                applyState(currentState);
            notifications.Post(new SimpleErrorNotification
            {
                Text = $"Could not change beatmap status: {exception.Message}",
            });
        };
        api.Queue(request);
    }

    private void applyState(BeatmapModerationState state)
    {
        currentState = state;
        setVisible(singleDifficultyButton, state.Allowed);
        setVisible(allDifficultiesButton, state.Allowed);
        setVisible(this, state.Allowed);
        setBusy(false);
    }

    private static void setVisible(Drawable drawable, bool visible)
    {
        if (visible)
            drawable.Show();
        else
            drawable.Hide();
    }

    private void setBusy(bool value)
    {
        busy = value;
        singleDifficultyButton.Enabled.Value = !value;
        allDifficultiesButton.Enabled.Value = !value;
        buttonFlow.FadeTo(value ? 0.55f : 1, 100);
    }

    private void postSuccess(string action, int? beatmapId)
    {
        string target = beatmapId == null ? "All difficulties" : "Selected difficulty";
        string text = action switch
        {
            "rank" => $"{target} was ranked on this server.",
            "unrank" => $"{target} was unranked on this server.",
            "love" => $"{target} received Loved status: leaderboard enabled, PP disabled.",
            _ => "Beatmap status updated.",
        };
        notifications.Post(new SimpleNotification
        {
            Text = text,
            Icon = FontAwesome.Solid.CheckCircle,
        });
    }

    private void cancelCurrentRequest()
    {
        currentRequest?.Cancel();
        currentRequest = null;
    }

    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing)
        {
            ++requestGeneration;
            cancelCurrentRequest();
            beatmapSet.UnbindAll();
        }

        base.Dispose(isDisposing);
    }
}

public partial class BeatmapModerationActionDialog : PopupDialog
{
    private readonly PopupDialogButton rankButton;
    private readonly PopupDialogButton unrankButton;
    private readonly PopupDialogButton loveButton;

    public BeatmapModerationActionDialog(string target, Action<string> action)
    {
        HeaderText = $"What should happen to {target}?";
        BodyText = "Select a new status. This change applies only to this server.";
        Icon = FontAwesome.Solid.ExclamationTriangle;
        Buttons = new PopupDialogButton[]
        {
            rankButton = new PopupDialogButton
            {
                Text = "Ranked",
                Action = () => action("rank"),
            },
            unrankButton = new PopupDialogButton
            {
                Text = "Graveyard",
                Action = () => action("unrank"),
            },
            loveButton = new PopupDialogButton
            {
                Text = "Loved",
                Action = () => action("love"),
            },
            new PopupDialogCancelButton
            {
                Text = "Cancel",
            },
        };
    }

    [BackgroundDependencyLoader]
    private void load(OsuColour colours)
    {
        rankButton.ButtonColour = colours.Green3;
        unrankButton.ButtonColour = colours.Red3;
        loveButton.ButtonColour = colours.Pink3;
    }
}
