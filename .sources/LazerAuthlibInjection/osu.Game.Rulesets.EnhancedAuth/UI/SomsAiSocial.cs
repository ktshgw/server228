#nullable enable
using System;
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
        StatusText.Text = "Выберите друга: правый клик → Пригласить в пати SOMSAI";
    }
    internal void InviteParty(int id)
    {
        if (!CanInviteParty) return;
        action("party_invite", new JObject { ["target_user_id"] = id }, afterSuccess: () =>
        { friendsOverlay?.Hide(); StatusText.Text = "Приглашение отправлено"; });
    }
}

[HarmonyPatch(typeof(UserPanel), "get_ContextMenuItems")]
internal static class SomsAiFriendInvitePatch
{
    static void Postfix(UserPanel __instance, ref MenuItem[] __result)
    {
        if (!SomsClientPreferences.Enabled || SomsAiScreen.PartyInviter == null || !SomsAiScreen.PartyInviter.TryGetTarget(out var screen)
            || !screen.CanInviteParty) return;
        __result = __result.Append(new OsuMenuItem("Пригласить в пати SOMSAI", MenuItemType.Highlighted,
            () => screen.InviteParty(__instance.User.Id))).ToArray();
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
    [Resolved] private IAPIProvider api { get; set; } = null!;
    [Resolved] private INotificationOverlay notifications { get; set; } = null!;
    [Resolved] private OsuGame game { get; set; } = null!;
    private readonly HashSet<int> seen = new();
    private SomsAiDataRequest? request;
    private int account;
    protected override void LoadComplete()
    {
        base.LoadComplete();
        Scheduler.AddDelayed(poll, 5000, true);
    }
    private void poll()
    {
        if (!SomsClientPreferences.Enabled || api.State.Value != APIState.Online || request != null) return;
        if (account != api.LocalUser.Value.Id) { account = api.LocalUser.Value.Id; seen.Clear(); }
        var current = request = new SomsAiDataRequest("social");
        current.Success += data => Schedule(() =>
        {
            request = null;
            foreach (var invite in data["invites"]?.ToObject<SomsPartyInvite[]>() ?? Array.Empty<SomsPartyInvite>())
            {
                if (!seen.Add(invite.Id)) continue;
                notifications.Post(new SimpleNotification
                {
                    Text = $"{invite.Captain?.Username} приглашает в пати SOMSAI. Нажмите, чтобы принять.",
                    Activated = () =>
                    {
                        var accept = new ApplySomsAiActionRequest(new JObject { ["action"] = "party_accept", ["invitation_id"] = invite.Id });
                        accept.Success += _ => Schedule(() => SomsAiBubbleTransition.Enter(game));
                        accept.Failure += _ => Schedule(() => notifications.Post(new SimpleNotification { Text = "Не удалось принять приглашение. Оно могло истечь или пати уже занята." }));
                        api.Queue(accept);
                        return true;
                    },
                });
            }
        });
        current.Failure += _ => Schedule(() => request = null);
        api.Queue(current);
    }
    protected override void Dispose(bool isDisposing) { request?.Cancel(); base.Dispose(isDisposing); }
}

[HarmonyPatch(typeof(OsuGame), "LoadComplete")]
internal static class SomsAiSocialInstallPatch
{
    static void Postfix(OsuGame __instance)
    {
        if (SomsClientPreferences.Enabled) __instance.Add(new SomsAiSocialNotifications { AlwaysPresent = true });
    }
}
