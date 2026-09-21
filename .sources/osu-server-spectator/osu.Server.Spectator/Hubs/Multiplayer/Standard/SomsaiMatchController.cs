using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using osu.Game.Online;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Extensions;
using osu.Server.Spectator.Services;

namespace osu.Server.Spectator.Hubs.Multiplayer.Standard
{
    /// <summary>
    /// Native gameplay transport for a durable SOMSAI draft. The app owns the
    /// roster, map selection and results; players cannot edit this managed room.
    /// </summary>
    [NonController]
    public sealed class SomsaiMatchController : IMatchController
    {
        public const string ROOM_PREFIX = "SOMSAI · ";
        private readonly ServerMultiplayerRoom room;
        private readonly IDatabaseFactory dbFactory;
        private readonly MultiplayerEventDispatcher events;
        private readonly IMultiplayerRoomController rooms;
        private readonly ISomsaiInteropClient interop;
        private readonly ILogger logger;
        private readonly bool runPump;
        private SomsaiRoomState configuration = new SomsaiRoomState();
        private TeamVersusRoomState state = TeamVersusRoomState.CreateDefault();
        private long? pendingCompletion;
        private long? pendingStart;
        private long? pendingAbort;
        private readonly SemaphoreSlim pollLock = new SemaphoreSlim(1, 1);
        private bool finishApplied;
        private DateTimeOffset lastFailureLog;
        private Task<SomsaiBotPlayback>? preparingBots;
        private SomsaiBotPlayback? botPlayback;
        private long botItemId = -1;
        private readonly SomsaiBotGameplayService? botGameplay;

        public MultiplayerPlaylistItem CurrentItem => room.Playlist.Single(item => item.ID == room.Settings.PlaylistItemId);

        public SomsaiMatchController(ServerMultiplayerRoom room, IDatabaseFactory dbFactory, MultiplayerEventDispatcher events,
                                    IMultiplayerRoomController rooms, ILoggerFactory loggerFactory,
                                    ISomsaiInteropClient? interop = null, bool runPump = true, SomsaiBotGameplayService? botGameplay = null)
        {
            this.room = room;
            this.dbFactory = dbFactory;
            this.events = events;
            this.rooms = rooms;
            this.interop = interop ?? new SomsaiInteropClient();
            this.runPump = runPump;
            this.botGameplay = botGameplay ?? events.BotGameplay;
            logger = loggerFactory.CreateLogger<SomsaiMatchController>();
        }

        public async Task Initialise()
        {
            configuration = await interop.GetRoom(room.RoomID);
            if (!configuration.Managed || configuration.Finished || configuration.Roster.Length != 2
                || configuration.Roster.Any(team => team.Length is < 1 or > 4)
                || configuration.Roster[0].Length != configuration.Roster[1].Length
                || configuration.Roster.SelectMany(team => team).Distinct().Count() != configuration.Roster.Sum(team => team.Length))
                throw new InvalidStateException("SOMSAI match is unavailable.");
            room.Host = null;
            room.Settings.AutoStartDuration = TimeSpan.Zero;
            state = TeamVersusRoomState.CreateDefault();
            state.Locked = true;
            room.MatchState = state;
            await applyConfiguration(configuration);
            foreach (var bot in configuration.Bots)
                if (room.Users.All(user => user.UserID != bot.UserId))
                    await room.AddUser(new MultiplayerRoomUser(bot.UserId) { Role = MultiplayerRoomUserRole.Player });
            await events.PostMatchRoomStateChangedAsync(room);
            if (runPump)
                _ = pump();
        }

        public async Task<bool> UserCanJoin(int userId)
        {
            var latest = await interop.GetRoom(room.RoomID);
            return latest.Contains(userId);
        }

        public async Task HandleUserJoined(MultiplayerRoomUser user)
        {
            configuration = await interop.GetRoom(room.RoomID);
            if (!configuration.Contains(user.UserID) || user.Role != MultiplayerRoomUserRole.Player)
                throw new InvalidStateException("Only the SOMSAI roster can join this room.");
            room.Host = null;
            await updateTeams();
        }

        public Task HandleUserLeft(MultiplayerRoomUser user) => updateTeams();
        public Task HandleUserStateChanged(MultiplayerRoomUser user) => Task.CompletedTask;
        public Task HandleSettingsChanged() => Task.CompletedTask;

        public Task HandleUserRequest(MultiplayerRoomUser user, MatchUserRequest request)
            => throw new InvalidStateException("SOMSAI teams and draft are managed by the server.");
        public Task AddPlaylistItem(MultiplayerPlaylistItem item, MultiplayerRoomUser user)
            => throw new InvalidStateException("Select maps in the SOMSAI draft.");
        public Task EditPlaylistItem(MultiplayerPlaylistItem item, MultiplayerRoomUser user)
            => throw new InvalidStateException("SOMSAI maps cannot be edited during a match.");
        public Task RemovePlaylistItem(long playlistItemId, MultiplayerRoomUser user)
            => throw new InvalidStateException("SOMSAI maps cannot be removed during a match.");

        public async Task HandleGameplayCompleted()
        {
            // Do not advance a normal playlist: the app waits for score submissions
            // and publishes the next selected card. Retries use the same item ID.
            pendingCompletion = CurrentItem.ID;
            using (var db = dbFactory.GetInstance())
                await db.MarkPlaylistItemAsPlayedAsync(room.RoomID, CurrentItem.ID);
            CurrentItem.Expired = true;
            await room.HandlePlaylistItemChanged(CurrentItem, false);
        }

        public MatchStartedEventDetail GetMatchDetails() => new MatchStartedEventDetail
        {
            room_type = room.Settings.MatchType.ToDatabaseMatchType(),
            teams = room.Users.Where(user => user.MatchState is TeamVersusUserState)
                        .ToDictionary(user => user.UserID, user => (room_team)((TeamVersusUserState)user.MatchState!).TeamID),
            slots = room.Users.Select((user, index) => (user, index)).ToDictionary(pair => pair.user.UserID, pair => (byte)pair.index)
        };

        private async Task updateTeams()
        {
            foreach (var user in room.Users)
            {
                int team = Array.FindIndex(configuration.Roster, members => members.Contains(user.UserID));
                if (team < 0)
                    continue;
                if (user.MatchState is TeamVersusUserState current && current.TeamID == team)
                    continue;
                user.MatchState = new TeamVersusUserState { TeamID = team };
                await events.PostMatchUserStateChangedAsync(room.RoomID, user.UserID, user.MatchState);
            }
            var slots = configuration.Roster.SelectMany(team => team)
                                     .Select(id => room.Users.Any(user => user.UserID == id) ? (int?)id : null).ToArray();
            if (state.Slots == null || !slots.SequenceEqual(state.Slots))
            {
                state.Slots = slots;
                await events.PostMatchRoomStateChangedAsync(room);
            }
        }

        private async Task applyConfiguration(SomsaiRoomState latest)
        {
            configuration = latest;
            await updateTeams();
            if (latest.Finished)
            {
                if (!finishApplied)
                {
                    stopBots();
                    if (room.State is MultiplayerRoomState.Playing or MultiplayerRoomState.WaitingForLoad)
                        await room.AbortMatch();
                    CurrentItem.Expired = true;
                    await room.HandlePlaylistItemChanged(CurrentItem, false);
                    await room.SetEndDateAsync(DateTimeOffset.UtcNow.AddMinutes(5));
                    foreach (var bot in latest.Bots)
                        if (room.Users.Any(user => user.UserID == bot.UserId)) await room.RemoveUser(bot.UserId);
                    finishApplied = true;
                }
                return;
            }
            if (room.State != MultiplayerRoomState.Open || room.Settings.PlaylistItemId == latest.PlaylistItemId)
            {
                await updateBots(latest);
                return;
            }
            MultiplayerPlaylistItem selected;
            using (var db = dbFactory.GetInstance())
                selected = (await db.GetPlaylistItemAsync(room.RoomID, latest.PlaylistItemId)).ToMultiplayerPlaylistItem();
            var previous = room.Playlist.FirstOrDefault(item => item.ID == selected.ID);
            if (previous == null)
            {
                room.Playlist.Add(selected);
                await events.PostPlaylistItemAddedAsync(room.RoomID, selected);
            }
            else
                room.Playlist[room.Playlist.IndexOf(previous)] = selected;
            room.Settings.PlaylistItemId = selected.ID;
            await room.HandleSettingsChanged(true);
            await updateBots(latest);
        }

        private void stopBots()
        {
            botPlayback?.Dispose();
            if (preparingBots != null && botPlayback == null)
                _ = preparingBots.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); }, TaskScheduler.Default);
            preparingBots = null;
            botPlayback = null;
            botItemId = -1;
        }

        private async Task updateBots(SomsaiRoomState latest)
        {
            if (latest.Bots.Length == 0) return;
            if (botItemId != latest.PlaylistItemId)
            {
                stopBots();
                if (latest.Stage != "ready") return;
                botItemId = latest.PlaylistItemId;
                preparingBots = botGameplay?.Prepare(room.RoomID, CurrentItem, latest);
                if (preparingBots == null) throw new InvalidOperationException("Bot gameplay transport is unavailable.");
            }
            if (preparingBots?.IsFaulted == true)
            {
                logger.LogWarning(preparingBots.Exception, "Could not prepare SOMSAI bot map in room {RoomId}", room.RoomID);
                pendingAbort = CurrentItem.ID;
                return;
            }
            if (botPlayback == null && preparingBots?.IsCompletedSuccessfully == true) botPlayback = preparingBots.Result;
            if (botPlayback == null) return;
            if (botPlayback.Failed) { pendingAbort = CurrentItem.ID; return; }
            foreach (var bot in latest.Bots)
            {
                var user = room.Users.FirstOrDefault(player => player.UserID == bot.UserId);
                if (user == null) continue;
                if (latest.Stage == "ready" && user.State is MultiplayerUserState.Idle or MultiplayerUserState.Results)
                {
                    await room.ChangeAndBroadcastUserState(user, MultiplayerUserState.Idle);
                    await room.ChangeUserBeatmapAvailability(user.UserID, BeatmapAvailability.LocallyAvailable());
                    await room.ChangeUserState(user.UserID, MultiplayerUserState.Ready);
                }
                else if (user.State == MultiplayerUserState.WaitingForLoad)
                {
                    await room.ChangeUserState(user.UserID, MultiplayerUserState.Loaded);
                    await room.ChangeUserState(user.UserID, MultiplayerUserState.ReadyForGameplay);
                }
                if (user.State == MultiplayerUserState.Playing)
                {
                    botPlayback.Start();
                    if (!user.VotedToSkipIntro && room.Users.Any(player => player.VotedToSkipIntro && latest.Bots.All(b => b.UserId != player.UserID)))
                        await room.VoteToSkipIntro(user.UserID);
                    if (botPlayback.Completed) await room.ChangeUserState(user.UserID, MultiplayerUserState.FinishedPlay);
                }
            }
            await room.UpdateRoomStateIfRequired();
        }

        private int[] connected() => room.Users.Where(user => user.Role == MultiplayerRoomUserRole.Player).Select(user => user.UserID).ToArray();
        private int[] ready() => room.Users.Where(user => user.State == MultiplayerUserState.Ready).Select(user => user.UserID).ToArray();
        private int[] available() => room.Users.Where(user => user.Role == MultiplayerRoomUserRole.Player
            && user.BeatmapAvailability.State == DownloadState.LocallyAvailable).Select(user => user.UserID).ToArray();

        // HTTP never holds the room lock. The start intent is kept until the app
        // acknowledges it, so a committed response lost in transit can be retried.
        internal async Task<bool> PollOnce()
        {
            await pollLock.WaitAsync();
            try
            {
                return await pollOnce();
            }
            finally
            {
                pollLock.Release();
            }
        }

        private async Task<bool> pollOnce()
        {
            int[] users;
            int[] readyUsers;
            int[] availableUsers;
            long itemId;
            string kind;
            using (var usage = await rooms.TryGetRoom(room.RoomID))
            {
                if (usage?.Item?.MatchController != this)
                {
                    stopBots();
                    return false;
                }
                users = connected();
                readyUsers = ready();
                availableUsers = available();
                kind = pendingAbort != null ? "aborted" : pendingCompletion != null ? "completed" : pendingStart != null ? "started" : "pulse";
                itemId = pendingAbort ?? pendingCompletion ?? pendingStart ?? CurrentItem.ID;
            }
            var latest = await interop.SendEvent(room.RoomID, kind, itemId, users, readyUsers, availableUsers);
            using (var usage = await rooms.TryGetRoom(room.RoomID))
            {
                if (usage?.Item?.MatchController != this)
                    return false;
                if (kind == "completed" && pendingCompletion == itemId)
                    pendingCompletion = null;
                if (kind == "aborted" && pendingAbort == itemId)
                    pendingAbort = null;
                await applyConfiguration(latest);
                if (latest.Finished)
                    return false;
                var roster = latest.Roster.SelectMany(team => team).Order().ToArray();
                if (latest.Stage == "ready" && latest.ForceStart && latest.StartAllowed && room.State == MultiplayerRoomState.Open
                    && latest.PlaylistItemId == CurrentItem.ID && roster.SequenceEqual(available().Order()))
                {
                    // The authoritative pick timer supplies readiness, but never substitutes a missing map.
                    foreach (var user in room.Users.Where(user => roster.Contains(user.UserID)
                                 && user.State is MultiplayerUserState.Idle or MultiplayerUserState.Results).ToArray())
                    {
                        if (user.State == MultiplayerUserState.Results)
                            await room.ChangeUserState(user.UserID, MultiplayerUserState.Idle);
                        await room.ChangeUserState(user.UserID, MultiplayerUserState.Ready);
                    }
                }
                bool allReady = roster.SequenceEqual(connected().Order()) && roster.SequenceEqual(ready().Order());
                if (latest.Stage == "playing" && room.State == MultiplayerRoomState.Open && !CurrentItem.Expired)
                {
                    if (pendingStart == CurrentItem.ID && latest.PlaylistItemId == CurrentItem.ID && allReady)
                    {
                        try
                        {
                            await ServerMultiplayerRoom.StartMatch(room);
                            pendingStart = null;
                        }
                        catch
                        {
                            pendingAbort = CurrentItem.ID;
                            throw;
                        }
                    }
                    else
                    {
                        // The roster changed while the acknowledgement was in
                        // flight, or the native host restarted mid-game.
                        pendingAbort = CurrentItem.ID;
                    }
                }
                else if (latest.Stage == "ready" && latest.StartAllowed && room.State == MultiplayerRoomState.Open && allReady)
                    pendingStart = CurrentItem.ID;
            }
            return true;
        }

        private async Task pump()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                try
                {
                    if (!await PollOnce())
                        return;
                }
                catch (Exception exception)
                {
                    if (DateTimeOffset.UtcNow - lastFailureLog > TimeSpan.FromSeconds(30))
                    {
                        lastFailureLog = DateTimeOffset.UtcNow;
                        logger.LogWarning(exception, "SOMSAI room {RoomId} synchronisation will retry", room.RoomID);
                    }
                }
            }
        }
    }
}
