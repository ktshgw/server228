#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using osu.Framework.Threading;
using osu.Framework.Logging;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Users;
using osu.Game.Users.Drawables;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(DrawableAvatar), "load")]
public static class SomsAvatarPatch
{
    private static readonly ConditionalWeakTable<LargeTextureStore, object> registeredStores = new();
    private sealed class AvatarState
    {
        public IUser? User;
        public LargeTextureStore? Textures;
        public string? RequestedUrl;
        public bool Requested;
        public int Revision;
    }
    private static readonly ConditionalWeakTable<DrawableAvatar, AvatarState> avatars = new();
    private static readonly SemaphoreSlim downloads = new(4);
    private static readonly System.Reflection.MethodInfo getScheduler = AccessTools.PropertyGetter(typeof(Drawable), "Scheduler");

    internal static void Track(DrawableAvatar avatar, IUser? user) => avatars.GetValue(avatar, _ => new AvatarState { User = user });

    internal static string? ResolveAvatarUrl(IUser? user)
    {
        if (user == null || user.OnlineID <= 0)
            return null;

        if (SomsStealthPresentation.IsLocal(user) && Online.SomsStealthSession.Current is { Active: true, Identity: { } identity })
            return normalise(identity.AvatarUrl);

        if (SomsMapperProfiles.IsOfficial(user)) return $"https://a.ppy.sh/{user.OnlineID}";

        string? avatarUrl = (user as APIUser)?.AvatarUrl;
        // Persisted local scores carry the user ID, but do not persist AvatarUrl.
        // Resolve it on SOMS! instead of DrawableAvatar's a.ppy.sh fallback.
        if (string.IsNullOrWhiteSpace(avatarUrl))
            return GlobalConfigManager.Config.ApiUrl.TrimEnd('/') + $"/users/{user.OnlineID}/avatar";

        return normalise(avatarUrl);
    }

    private static string normalise(string url) => url.StartsWith('/')
        ? new Uri(new Uri(GlobalConfigManager.Config.ApiUrl), url).AbsoluteUri : url;

    static bool Prefix(DrawableAvatar __instance, IUser? ___user, LargeTextureStore textures)
    {
        if (!GlobalConfigManager.Patched || GlobalConfigManager.Config.DisableServerExtensions)
            return true;

        lock (registeredStores)
        {
            if (!registeredStores.TryGetValue(textures, out _))
            {
                textures.AddTextureSource(new TextureLoaderStore(new DllResourceStore(typeof(SomsAvatarPatch).Assembly)));
                registeredStores.Add(textures, new object());
            }
        }

        Track(__instance, ___user);
        avatars.GetOrCreateValue(__instance).Textures = textures;
        // Network I/O in this loader held up the entire profile/card load. Make the
        // avatar drawable ready immediately and replace its placeholder asynchronously.
        __instance.Texture = textures.Get("Resources/soms-default-avatar");
        return false;
    }

    internal static void Refresh(DrawableAvatar avatar)
    {
        if (!avatars.TryGetValue(avatar, out var state) || state.Textures == null
            || SomsDrawableLifecycle.IsDisposed(avatar)) return;
        string? url = ResolveAvatarUrl(state.User);
        if (state.Requested && state.RequestedUrl == url) return;
        if (state.RequestedUrl != url)
            avatar.Texture = state.Textures.Get("Resources/soms-default-avatar");
        state.Requested = true;
        state.RequestedUrl = url;
        int revision = ++state.Revision;
        var scheduler = (Scheduler)getScheduler.Invoke(avatar, null)!;
        var textures = state.Textures;
        if (string.IsNullOrWhiteSpace(url))
        {
            avatar.Texture = textures.Get("Resources/soms-default-avatar");
            return;
        }
        _ = Task.Run(async () =>
        {
            await downloads.WaitAsync().ConfigureAwait(false);
            Texture? texture = null;
            try
            {
                if (revision != state.Revision || SomsDrawableLifecycle.IsDisposed(avatar)) return;
                // TextureStore waits for a concurrent lookup of the same image using
                // WaitSafely, which forbids thread-pool threads (including GetAsync).
                // Bound dedicated workers just like native long-running drawable loads.
                texture = await Task.Factory.StartNew(() => textures.Get(url), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            }
            catch (Exception error) { Logger.Error(error, "SOMS!: avatar image could not be loaded."); }
            finally { downloads.Release(); }
            scheduler.Add(() =>
            {
                if (revision != state.Revision || SomsDrawableLifecycle.IsDisposed(avatar)) { texture?.Dispose(); return; }
                if (texture != null) avatar.Texture = texture;
                else state.Requested = false; // Retry on the next explicit refresh; keep the current image.
            });
        });
    }
}
