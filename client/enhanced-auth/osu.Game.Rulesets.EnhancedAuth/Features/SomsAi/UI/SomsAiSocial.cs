#nullable enable
using System;
using osu.Game.Overlays.Dialog;
using osu.Game.Graphics;
using osu.Framework.Graphics.Sprites;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Screens;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Overlays;
using osu.Game.Overlays.Dashboard;
using osu.Game.Overlays.Notifications;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Users;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public partial class SomsAiScreen
{
    private SomsAiPartyChat? partyChat;
    [Resolved(CanBeNull = true)] private DashboardOverlay? friendsOverlay { get; set; }
    internal static WeakReference<SomsAiScreen>? PartyInviter;
    internal bool CanInviteParty => Alive && this.IsCurrentScreen() && !matchOnly && state.Party?.Busy != true
        && (state.Party?.Members.Count ?? 1) < 2 && (state.Party?.CaptainId ?? Api.LocalUser.Value.Id) == Api.LocalUser.Value.Id;
    private void openPartyFriends()
    {
        if (!CanInviteParty) return;
        PartyInviter = new(this);
        if (friendsOverlay == null) { revealForm(inviteForm); return; }
        friendsOverlay.Header.Current.Value = DashboardOverlayTabs.Friends;
        friendsOverlay.Show();
        StatusText.Text = "Select a friend: right-click → Invite to SOMSAI party";
    }
    internal void InviteParty(int id)
    {
        if (!CanInviteParty) return;
        action("party_invite", new JObject { ["target_user_id"] = id }, afterSuccess: () =>
        { friendsOverlay?.Hide(); StatusText.Text = "Invitation sent"; });
    }
}

[HarmonyPatch(typeof(UserPanel), "get_ContextMenuItems")]
internal static class SomsAiFriendInvitePatch
{
    static void Postfix(UserPanel __instance, ref MenuItem[] __result)
    {
        if (!SomsClientPreferences.Enabled || SomsAiSocialNotifications.Instance == null
            || !SomsAiSocialNotifications.Instance.TryGetTarget(out var coordinator)) return;
        if (coordinator.IsLocal(__instance.User.Id)) return;
        if (coordinator.CanInviteCustom)
        {
            __result = __result.Append(new OsuMenuItem("Invite to custom", MenuItemType.Highlighted,
                () => coordinator.InviteCustom(__instance.User.Id))).ToArray();
            return;
        }
        __result = __result
            .Append(new OsuMenuItem("SOMSAI Duel", MenuItemType.Highlighted, () => coordinator.InviteDuel(__instance.User.Id)))
            .Append(new OsuMenuItem("SOMSAI Party", MenuItemType.Highlighted, () => coordinator.InviteParty(__instance.User.Id)))
            .ToArray();
    }
}

[HarmonyPatch(typeof(osu.Game.Online.API.Requests.Responses.APIUser), "get_Username")]
internal static class SomsAiBotDisplayNamePatch
{
    static void Postfix(ref string __result)
    { if (SomsClientPreferences.Enabled) __result = SomsAiRank.DisplayName(__result); }
}

internal sealed partial class SomsAiSocialNotifications : CompositeDrawable
{
    internal static WeakReference<SomsAiSocialNotifications>? Instance;
    internal bool CanInviteCustom => match is { Ranked: false, Stage: "waiting" };
    internal bool IsLocal(int userId) => api.LocalUser.Value.Id == userId;
    [Resolved] private IAPIProvider api { get; set; } = null!;
    [Resolved] private INotificationOverlay notifications { get; set; } = null!;
    [Resolved] private IDialogOverlay dialogs { get; set; } = null!;
    [Resolved] private OsuGame game { get; set; } = null!;
    [Resolved] private MultiplayerClient multiplayer { get; set; } = null!;
    private readonly HashSet<int> seen = new();
    private SomsAiDataRequest? request;
    private ApplySomsAiActionRequest? actionRequest;
    private ProgressNotification? searchNotification;
    private SomsAiReadyCheckDialog? readyDialog;
    private SomsAiQueue? queue;
    private SomsAiMatch? match;
    private int account;
    private int? openedMatchId;

    protected override void LoadComplete()
    {
        base.LoadComplete();
        Instance = new(this);
        Scheduler.AddDelayed(poll, 2000, true);
    }

    protected override void Update()
    {
        base.Update();
        if (searchNotification == null) return;
        if (match is { Ranked: true, Stage: "waiting", Deadline: { } deadline })
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            searchNotification.Text = $"SOMSAI match found · accept in {remaining:mm\\:ss}";
            return;
        }
        if (queue?.JoinedAt is not { } joined) return;
        var elapsed = DateTimeOffset.UtcNow - joined;
        searchNotification.Text = $"SOMSAI search · {queue.Format} · {elapsed:mm\\:ss}";
    }

    private void poll()
    {
        if (!SomsClientPreferences.Enabled || api.State.Value != APIState.Online || request != null) return;
        if (account != api.LocalUser.Value.Id)
        {
            account = api.LocalUser.Value.Id;
            seen.Clear();
            openedMatchId = null;
            clearSearchNotification();
            closeReadyDialog();
        }
        var current = request = new SomsAiDataRequest("social");
        current.Success += data => Schedule(() =>
        {
            if (request != current) return;
            request = null;
            queue = data["queue"]?.ToObject<SomsAiQueue>();
            match = data["match"]?.ToObject<SomsAiMatch>();
            updateSearchNotification();
            updateReadyCheck();
            updatePartyInvites(data);
        });
        current.Failure += _ => Schedule(() => { if (request == current) request = null; });
        api.Queue(current);
    }

    internal void InviteDuel(int targetId) => sendAction(new JObject
    {
        ["action"] = "direct_invite", ["invite_kind"] = "duel", ["target_user_id"] = targetId,
        ["ruleset_id"] = 0, ["variant_id"] = 0,
    });

    internal void InviteParty(int targetId) => sendAction(new JObject
    {
        ["action"] = "party_invite", ["target_user_id"] = targetId,
    }, () => SomsAiBubbleTransition.Enter(game));

    internal void InviteCustom(int targetId)
    {
        if (!CanInviteCustom || match == null) return;
        sendAction(new JObject
        {
            ["action"] = "direct_invite", ["invite_kind"] = "custom", ["target_user_id"] = targetId,
            ["match_id"] = match.Id, ["ruleset_id"] = match.RulesetId, ["variant_id"] = match.VariantId,
        });
    }

    private void updatePartyInvites(JObject data)
    {
        foreach (var direct in data["direct_invites"]?.ToObject<SomsAiDirectInvite[]>() ?? Array.Empty<SomsAiDirectInvite>())
        {
            int key = -direct.Id;
            if (!seen.Add(key)) continue;
            string kind = direct.Kind == "custom" ? "a private SOMSAI custom lobby" : "a SOMSAI duel";
            notifications.Post(new SimpleNotification
            {
                Text = $"{direct.Inviter?.Username} invited you to {kind}. Click to accept.",
                IsCritical = true,
                Activated = () =>
                {
                    sendAction(new JObject { ["action"] = "direct_accept", ["invitation_id"] = direct.Id },
                        () => SomsAiBubbleTransition.Enter(game));
                    return true;
                },
            });
        }
        foreach (var invite in data["invites"]?.ToObject<SomsPartyInvite[]>() ?? Array.Empty<SomsPartyInvite>())
        {
            if (!seen.Add(invite.Id)) continue;
            notifications.Post(new SimpleNotification
            {
                Text = $"{invite.Captain?.Username} invited you to a SOMSAI party. Click to accept.",
                Activated = () =>
                {
                    sendAction(new JObject { ["action"] = "party_accept", ["invitation_id"] = invite.Id },
                        () => SomsAiBubbleTransition.Enter(game));
                    return true;
                },
            });
        }
    }

    private void updateSearchNotification()
    {
        bool active = queue != null || match is { Ranked: true, Stage: "waiting" };
        if (!active) { clearSearchNotification(); return; }
        if (searchNotification != null) return;
        searchNotification = new ProgressNotification
        {
            Text = "SOMSAI search · 00:00",
            CompletionText = "SOMSAI search finished",
            IsCritical = true,
        };
        searchNotification.Progress = -1;
        notifications.Post(searchNotification);
    }

    private void clearSearchNotification()
    {
        if (searchNotification == null) return;
        searchNotification.State = ProgressNotificationState.Cancelled;
        searchNotification = null;
        queue = null;
    }

    private void updateReadyCheck()
    {
        int localId = api.LocalUser.Value.Id;
        if (match is not { Ranked: true, Stage: "waiting" } waiting)
        {
            closeReadyDialog();
            if (match is { IsFinished: false } active && openedMatchId != active.Id)
            {
                openedMatchId = active.Id;
                // The bubbles are the explicit SOMSAI entry transition. A match
                // becoming active must not replay them over gameplay or another
                // SOMSAI screen; the destination screen performs the room join.
                var current = game.ScreenStack.CurrentScreen;
                if (current is not SomsAiScreen)
                    current?.Push(new SomsAiScreen());
            }
            return;
        }
        if (waiting.Accepted.Contains(localId)) { closeReadyDialog(); return; }
        if (readyDialog?.MatchId == waiting.Id) return;
        closeReadyDialog();
        interruptGameplay();
        readyDialog = new SomsAiReadyCheckDialog(
            waiting,
            () => sendMatchAction("ready", waiting),
            () => sendMatchAction("decline", waiting));
        dialogs.Push(readyDialog);
    }

    private void sendMatchAction(string action, SomsAiMatch target)
    {
        sendAction(new JObject
        {
            ["action"] = action,
            ["match_id"] = target.Id,
            ["ruleset_id"] = target.RulesetId,
            ["variant_id"] = target.VariantId,
        }, action == "ready" ? () => poll() : null);
    }

    private void sendAction(JObject body, Action? success = null)
    {
        if (actionRequest != null) return;
        var current = actionRequest = new ApplySomsAiActionRequest(body);
        current.Success += _ => Schedule(() =>
        {
            if (actionRequest != current) return;
            actionRequest = null;
            success?.Invoke();
            poll();
        });
        current.Failure += error => Schedule(() =>
        {
            if (actionRequest != current) return;
            actionRequest = null;
            notifications.Post(new SimpleNotification { Text = "SOMSAI: " + error.Message });
            poll();
        });
        api.Queue(current);
    }

    private void interruptGameplay()
    {
        var screen = game.ScreenStack.CurrentScreen;
        if (screen == null || !screen.GetType().Name.Contains("Player", StringComparison.OrdinalIgnoreCase)) return;
        // Multiplayer gameplay must return to its room before the ready check.
        // Solo gameplay remains loaded and is paused through the player's own pause path.
        if (multiplayer.Room != null) screen.Exit();
        else AccessTools.Method(screen.GetType(), "pause")?.Invoke(screen, null);
    }

    private void closeReadyDialog()
    {
        if (readyDialog == null) return;
        readyDialog.Expire();
        readyDialog = null;
    }

    protected override void Dispose(bool isDisposing)
    {
        if (Instance != null && Instance.TryGetTarget(out var current) && ReferenceEquals(current, this)) Instance = null;
        request?.Cancel();
        actionRequest?.Cancel();
        clearSearchNotification();
        closeReadyDialog();
        base.Dispose(isDisposing);
    }
}

internal sealed partial class SomsAiReadyCheckDialog : PopupDialog
{
    public int MatchId { get; }

    public SomsAiReadyCheckDialog(SomsAiMatch match, Action accept, Action decline)
    {
        MatchId = match.Id;
        HeaderText = "YOUR SOMSAI MATCH IS READY";
        BodyText = $"{match.Format} · Accept within one minute. Only players who decline or do not accept receive a queue ban.";
        Icon = FontAwesome.Solid.Bolt;
        Buttons = new PopupDialogButton[]
        {
            new PopupDialogButton { Text = "ACCEPT", Action = accept },
            new PopupDialogCancelButton { Text = "Decline", Action = decline },
        };
    }

    [BackgroundDependencyLoader]
    private void load(OsuColour colours)
    {
        Buttons.ElementAt(0).ButtonColour = colours.Green3;
        Buttons.ElementAt(1).ButtonColour = colours.Red3;
    }
}

[HarmonyPatch(typeof(OsuGame), "LoadComplete")]
internal static class SomsAiSocialInstallPatch
{
    static void Postfix(OsuGame __instance)
    {
        if (SomsClientPreferences.Enabled) __instance.Add(new SomsAiSocialNotifications { AlwaysPresent = true });
    }
}
