#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Threading;
using osu.Framework.Localisation;
using osu.Game.Graphics.UserInterface;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online;
using osu.Game.Online.Chat;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays;
using osu.Game.Overlays.Profile;
using osu.Game.Overlays.Profile.Sections;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Users;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>Native profile UI with its own API and identity namespace.</summary>
public sealed partial class SomsOfficialProfileOverlay : UserProfileOverlay
{
    private static readonly ConditionalWeakTable<OsuGame, SomsOfficialProfileOverlay> instances = new();
    private IUser? pendingUser;
    private GetUserRequest? request;
    private ScheduledDelegate? timeout;
    private FormButton? retry;
    private int generation;
    private bool ready;
    private SomsOfficialProfileAPI officialAPI = null!;

    internal static void Open(OsuGame game, IUser user)
    {
        var scheduler = (Scheduler)AccessTools.PropertyGetter(typeof(Drawable), "Scheduler").Invoke(game, null)!;
        scheduler.Add(() =>
        {
            if (SomsDrawableLifecycle.IsDisposed(game)) return;
            if (!instances.TryGetValue(game, out var overlay))
            {
                overlay = new SomsOfficialProfileOverlay();
                instances.Add(game, overlay);
                var content = (Container)AccessTools.Field(typeof(OsuGame), "overlayContent").GetValue(game)!;
                var created = overlay;
                Action<Drawable> complete = drawable =>
                {
                    content.Add(drawable);
                    created.ready = true;
                    if (created.pendingUser != null) created.ShowOfficialUser(created.pendingUser);
                };
                // Register with lazer's focused overlay stack, without replacing its SOMS! profile dependency.
                AccessTools.Method(typeof(OsuGame), "loadComponentSingleFile").MakeGenericMethod(typeof(SomsOfficialProfileOverlay))
                    .Invoke(game, [overlay, complete, false]);
            }
            overlay.pendingUser = user;
            if (overlay.ready) overlay.ShowOfficialUser(user);
        });
    }

    private void ShowOfficialUser(IUser user)
    {
        Cancel();
        // Native ShowUser ignores ID=0 (system user), but imported mapper metadata
        // legitimately has no online ID. Our separate overlay resolves it by username.
        AccessTools.Field(typeof(UserProfileOverlay), "user").SetValue(this, user);
        AccessTools.Field(typeof(UserProfileOverlay), "ruleset").SetValue(this, null);
        Show();
        // PopIn also calls native fetchAndSetContent. Coalesce both paths instead
        // of starting and immediately cancelling the same official request twice.
        QueueFetch();
    }

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
        dependencies.CacheAs<IAPIProvider>(officialAPI = new SomsOfficialProfileAPI(parent.Get<IAPIProvider>()));
        dependencies.CacheAs<UserProfileOverlay>(this);
        var game = parent.Get<OsuGame>();
        dependencies.CacheAs<ILinkHandler>(new OfficialLinks(parent.Get<ILinkHandler>() ?? game, game));
        return dependencies;
    }

    protected override ProfileHeader CreateHeader()
    {
        var header = base.CreateHeader();
        AccessTools.PropertySetter(typeof(OverlayTitle), nameof(OverlayTitle.Title)).Invoke(header.Title, [(LocalisableString)"Профиль · osu!"]);
        return header;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        State.BindValueChanged(value => { if (value.NewValue == Visibility.Hidden) Cancel(); });
    }

    private void Cancel()
    {
        generation++;
        timeout?.Cancel();
        request?.Cancel();
        request = null;
        if (retry != null) retry.Alpha = 0;
    }

    internal void QueueFetch() => Scheduler.AddOnce(Fetch);

    internal void Fetch()
    {
        Cancel();
        if (State.Value != Visibility.Visible) return;
        var user = (IUser?)AccessTools.Field(typeof(UserProfileOverlay), "user").GetValue(this);
        var mode = (IRulesetInfo?)AccessTools.Field(typeof(UserProfileOverlay), "ruleset").GetValue(this);
        if (user == null) return;
        int current = generation;
        var previousSections = AccessTools.Field(typeof(UserProfileOverlay), "sectionsContainer").GetValue(this);
        if (previousSections != null) AccessTools.PropertySetter(previousSections.GetType(), "ExpandableHeader").Invoke(previousSections, [null]);
        AccessTools.Field(typeof(UserProfileOverlay), "lastSection").SetValue(this, null);
        AccessTools.Field(typeof(UserProfileOverlay), "sections").SetValue(this, new ProfileSection[]
        {
            new RecentSection(), new RanksSection(), new HistoricalSection(), new BeatmapsSection(), new KudosuSection()
        });
        AccessTools.Method(typeof(UserProfileOverlay), "recreateBaseContent").Invoke(this, null);
        var loading = (LoadingLayer)AccessTools.Field(typeof(UserProfileOverlay), "loadingLayer").GetValue(this)!;
        loading.Show();
        var next = request = user.OnlineID > 1 ? new GetUserRequest(user.OnlineID, mode) : new GetUserRequest(user.Username, mode);
        bool IsCurrent() => current == generation && !SomsDrawableLifecycle.IsDisposed(this);
        void Failed(Exception _)
        {
            if (!IsCurrent()) return;
            Cancel();
            loading.Hide();
            if (retry == null)
                AddInternal(retry = new FormButton
                {
                    Caption = "Не удалось загрузить официальный профиль", ButtonText = "Повторить",
                    RelativeSizeAxes = Axes.X, Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre,
                    Y = 15, Depth = float.MinValue, Name = "soms-official-profile-retry", Action = Fetch
                });
            retry.Alpha = 1;
        }
        next.Success += loaded =>
        {
            if (!IsCurrent()) return;
            timeout?.Cancel();
            request = null;
            SomsMapperProfiles.Mark(loaded);
            AccessTools.Method(typeof(UserProfileOverlay), "userLoadComplete").Invoke(this, [loaded, mode]);
        };
        next.Failure += Failed;
        timeout = Scheduler.AddDelayed(() => Failed(new TimeoutException()), 20000);
        officialAPI.Queue(next);
    }

    protected override void Dispose(bool isDisposing)
    {
        Cancel();
        officialAPI?.Dispose();
        base.Dispose(isDisposing);
    }

    private sealed class OfficialLinks(ILinkHandler native, OsuGame game) : ILinkHandler
    {
        public void HandleLink(string url)
        {
            if (!Uri.TryCreate(new Uri("https://osu.ppy.sh"), url, out var uri)) return;
            if (uri.Host == "osu.ppy.sh")
            {
                var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    if (parts[0] is "users" or "u")
                    {
                        var lookup = Uri.UnescapeDataString(parts[1]);
                        HandleLink(new LinkDetails(LinkAction.OpenUserProfile, new APIUser
                        {
                            Id = int.TryParse(lookup, out int id) ? id : 0, Username = lookup
                        }));
                        return;
                    }
                    if (parts[0] is "beatmaps" or "b" or "beatmapsets" or "s")
                    {
                        native.HandleLink(new LinkDetails(parts[0] is "beatmaps" or "b" ? LinkAction.OpenBeatmap : LinkAction.OpenBeatmapSet, parts[1]));
                        return;
                    }
                }
            }
            game.OpenUrlExternally(uri.AbsoluteUri);
        }

        public void HandleLink(LinkDetails link)
        {
            if (link.Action == LinkAction.OpenUserProfile && link.Argument is IUser user)
            {
                SomsMapperProfiles.Mark(user);
                game.ShowUser(user);
            }
            else if (link.Action == LinkAction.External) HandleLink((string)link.Argument);
            else native.HandleLink(link);
        }
    }
}

internal sealed partial class SomsOfficialProfileAPI : DummyAPIAccess
{
    internal static readonly ConditionalWeakTable<APIRequest, Route> Routes = new();
    internal sealed record Route(string Origin);
    private readonly IAPIProvider transport;
    internal SomsOfficialProfileAPI(IAPIProvider transport)
    {
        this.transport = transport;
        LocalUser.Value = new APIUser { Id = 0, Username = "Guest" };
        Endpoints.APIUrl = transport.Endpoints.APIUrl;
        Endpoints.WebsiteUrl = "https://osu.ppy.sh";
    }

    public override void Queue(APIRequest request)
    {
        if (request is not (GetUserRequest or GetUserScoresRequest or GetUserBeatmapsRequest or GetUserRecentActivitiesRequest
            or GetUserMostPlayedBeatmapsRequest or GetUserKudosuHistoryRequest))
        {
            request.AttachAPI(transport);
            request.Fail(new InvalidOperationException("Откройте osu.ppy.sh для этого действия."));
            return;
        }
        Routes.GetValue(request, _ => new Route(transport.Endpoints.APIUrl.TrimEnd('/')));
        transport.Queue(request);
    }
}
