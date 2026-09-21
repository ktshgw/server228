// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Extensions;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Spectator;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Spectator.Authentication;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Extensions;
using osu.Server.Spectator.Helpers;
using osu.Server.Spectator.Services;
using StackExchange.Redis;

namespace osu.Server.Spectator.Hubs.Spectator
{
    [Authorize]
    public class SpectatorHub : StatefulUserHub<ISpectatorClient, SpectatorClientState>, ISpectatorServer
    {
        /// <summary>
        /// Minimum beatmap status to save replays for.
        /// </summary>
        private const BeatmapOnlineStatus min_beatmap_status_for_replays = BeatmapOnlineStatus.Ranked;

        /// <summary>
        /// Maximum beatmap status to save replays for.
        /// </summary>
        private const BeatmapOnlineStatus max_beatmap_status_for_replays = BeatmapOnlineStatus.Loved;

        private readonly IDatabaseFactory databaseFactory;
        private readonly ScoreBuffer scoreBuffer;
        private readonly ScoreUploader scoreUploader;
        private readonly IScoreProcessedSubscriber scoreProcessedSubscriber;
        private readonly RulesetManager manager;
        private readonly IConnectionMultiplexer redis;
        private readonly SomsaiBotGameplayService? botGameplay;

        public SpectatorHub(
            ILoggerFactory loggerFactory,
            EntityStore<SpectatorClientState> users,
            IDatabaseFactory databaseFactory,
            ScoreBuffer scoreBuffer,
            ScoreUploader scoreUploader,
            IScoreProcessedSubscriber scoreProcessedSubscriber,
            RulesetManager manager,
            IConnectionMultiplexer redis,
            SomsaiBotGameplayService? botGameplay = null)
            : base(loggerFactory, users)
        {
            this.databaseFactory = databaseFactory;
            this.scoreBuffer = scoreBuffer;
            this.scoreUploader = scoreUploader;
            this.scoreProcessedSubscriber = scoreProcessedSubscriber;
            this.manager = manager;
            this.redis = redis;
            this.botGameplay = botGameplay;
        }

        #region V1 (obsoleted)

        [Obsolete("New clients should use BeginPlaySessionV2.")] // can remove method 20270102
        public async Task BeginPlaySession(long? scoreToken, SpectatorState state)
        {
            ArgumentNullException.ThrowIfNull(state.MaximumStatistics);
            ArgumentNullException.ThrowIfNull(state.Mods);

            foreach (var val in state.MaximumStatistics.Keys)
                val.ThrowIfInvalid();

            state.State.ThrowIfInvalid();

            int userId = Context.GetUserId();

            using (var usage = await GetOrCreateLocalUserState())
            {
                var clientState = (usage.Item ??= new SpectatorClientState(Context.ConnectionId, userId));

                if (clientState.State != null)
                {
                    // Previous session never received EndPlaySession call.
                    // Should probably be handled in some way.
                }

                using (var db = databaseFactory.GetInstance())
                {
                    if (scoreToken != null)
                    {
                        int? userIdFromToken = await db.GetUserIdFromScoreTokenAsync(scoreToken.Value);

                        if (userIdFromToken != userId)
                            throw new InvalidOperationException($"User id:{userId} attempted to start play with token not owned by them (token:{scoreToken} userId:{userIdFromToken})");
                    }

                    clientState.State = state;
                    clientState.ScoreToken = scoreToken;

                    if (state.RulesetID == null)
                        return;

                    if (state.BeatmapID == null)
                        return;

                    database_beatmap? beatmap = await db.GetBeatmapOrFetchAsync(state.BeatmapID.Value);
                    string? username = await db.GetUsernameAsync(userId);

                    if (string.IsNullOrEmpty(username))
                        throw new ArgumentException(nameof(username));

                    if (string.IsNullOrEmpty(beatmap?.checksum))
                        return;

                    clientState.Beatmap = beatmap;
                    var score = new Score
                    {
                        ScoreInfo =
                        {
                            APIMods = state.Mods.ToArray(),
                            User = new APIUser
                            {
                                Id = userId,
                                Username = username,
                            },
                            Ruleset = manager.GetRuleset(state.RulesetID.Value).RulesetInfo,
                            BeatmapInfo = new BeatmapInfo
                            {
                                OnlineID = state.BeatmapID.Value,
                                MD5Hash = beatmap.checksum,
                                Status = beatmap.approved
                            },
                            MaximumStatistics = state.MaximumStatistics
                        }
                    };

                    if (scoreToken != null)
                        await scoreBuffer.TryAddAsync(scoreToken.Value, score, beatmap);
                }
            }

            await Clients.Group(GetGroupId(userId)).UserBeganPlaying(userId, state);
        }

        [Obsolete("New clients should use SendFrameDataV2.")] // can remove method 20270102
        public async Task SendFrameData(FrameDataBundle data)
        {
            ArgumentNullException.ThrowIfNull(data.Header);
            ArgumentNullException.ThrowIfNull(data.Header.ScoreProcessorStatistics);
            ArgumentNullException.ThrowIfNull(data.Header.Statistics);
            ArgumentNullException.ThrowIfNull(data.Header.Mods);
            ArgumentNullException.ThrowIfNull(data.Frames);

            using (var usage = await GetOrCreateLocalUserState())
            {
                if (usage.Item?.ScoreToken != null)
                    await scoreBuffer.UpdateAsync(usage.Item.ScoreToken.Value, data);

                botGameplay?.ObserveFrames(Context.GetUserId(), data);
                await Clients.Group(GetGroupId(Context.GetUserId())).UserSentFrames(Context.GetUserId(), data);
            }
        }

        [Obsolete("New clients should use EndPlaySessionV2.")] // can remove method 20270102
        public async Task EndPlaySession(SpectatorState state)
        {
            ArgumentNullException.ThrowIfNull(state.MaximumStatistics);
            ArgumentNullException.ThrowIfNull(state.Mods);
            state.State.ThrowIfInvalid();

            using (var usage = await GetOrCreateLocalUserState())
            {
                try
                {
                    long? scoreToken = usage.Item?.ScoreToken;

                    // Score may be null if the BeginPlaySession call failed but the client is still sending frame data.
                    // For now it's safe to drop these frames.
                    // Note that this *intentionally* skips the `endPlaySession()` call at the end of method.
                    if (scoreToken == null || usage.Item?.Beatmap == null)
                        return;

                    var score = await scoreBuffer.DequeueAsync(scoreToken.Value);
                    if (score == null)
                        return;

                    await processScore(scoreToken.Value, score);

                    int exitTime = (int)Math.Round((score.Score.Replay.Frames.LastOrDefault()?.Time ?? 0) / 1000);

                    if (state.State == SpectatedUserState.Failed || state.State == SpectatedUserState.Quit)
                        await processFailtime(score.Beatmap, exitTime, state.State);

                    await editPlayTime(score.Score, exitTime);
                }
                finally
                {
                    if (usage.Item != null)
                    {
                        usage.Item.State = null;
                        usage.Item.Beatmap = null;
                        usage.Item.ScoreToken = null;
                    }
                }
            }

            await endPlaySession(Context.GetUserId(), state);
        }

        #endregion

        #region V2

        public async Task BeginPlaySessionV2(long? scoreToken, SpectatorState state)
        {
            ArgumentNullException.ThrowIfNull(state.MaximumStatistics);
            ArgumentNullException.ThrowIfNull(state.Mods);

            foreach (var val in state.MaximumStatistics.Keys)
                val.ThrowIfInvalid();

            state.State.ThrowIfInvalid();

            int userId = Context.GetUserId();

            using (var usage = await GetOrCreateLocalUserState())
            using (var db = databaseFactory.GetInstance())
            {
                if (scoreToken != null)
                {
                    int? userIdFromToken = await db.GetUserIdFromScoreTokenAsync(scoreToken.Value);

                    if (userIdFromToken != userId)
                        throw new InvalidOperationException($"User id:{userId} attempted to start play with token not owned by them (token:{scoreToken} userId:{userIdFromToken})");
                }

                var clientState = (usage.Item ??= new SpectatorClientState(Context.ConnectionId, userId));

                clientState.State = state;

                if (state.RulesetID == null)
                    return;

                if (state.BeatmapID == null)
                    return;

                database_beatmap? beatmap = await db.GetBeatmapOrFetchAsync(state.BeatmapID.Value);
                string? username = await db.GetUsernameAsync(userId);

                if (string.IsNullOrEmpty(username))
                    throw new ArgumentException(nameof(username));

                if (string.IsNullOrEmpty(beatmap?.checksum))
                    return;

                clientState.Beatmap = beatmap;
                var score = new Score
                {
                    ScoreInfo =
                    {
                        APIMods = state.Mods.ToArray(),
                        User = new APIUser
                        {
                            Id = userId,
                            Username = username,
                        },
                        Ruleset = manager.GetRuleset(state.RulesetID.Value).RulesetInfo,
                        BeatmapInfo = new BeatmapInfo
                        {
                            OnlineID = state.BeatmapID.Value,
                            MD5Hash = beatmap.checksum,
                            Status = beatmap.approved
                        },
                        MaximumStatistics = state.MaximumStatistics
                    }
                };

                if (scoreToken != null)
                {
                    if (!usage.Item.ScoreTokens.Contains(scoreToken.Value))
                    {
                        while (usage.Item.ScoreTokens.Count >= SpectatorClientState.MAX_STARTED_SCORES)
                        {
                            long expiredToken = usage.Item.ScoreTokens[0];
                            usage.Item.ScoreTokens.RemoveAt(0);
                            var expiredScore = await scoreBuffer.DequeueAsync(expiredToken);
                            if (expiredScore != null)
                                await processScore(expiredToken, expiredScore);
                            Log($"Score for token {expiredToken} was dropped from buffer due to exceeding limit", LogLevel.Warning);
                        }

                        usage.Item.ScoreTokens.Add(scoreToken.Value);
                    }

                    await scoreBuffer.TryAddAsync(scoreToken.Value, score, beatmap);
                }
            }

            await Clients.Group(GetGroupId(userId)).UserBeganPlaying(userId, state);
        }

        public async Task SendFrameDataV2(long? scoreToken, FrameDataBundle data)
        {
            ArgumentNullException.ThrowIfNull(data.Header);
            ArgumentNullException.ThrowIfNull(data.Header.ScoreProcessorStatistics);
            ArgumentNullException.ThrowIfNull(data.Header.Statistics);
            ArgumentNullException.ThrowIfNull(data.Header.Mods);
            ArgumentNullException.ThrowIfNull(data.Frames);

            using (var usage = await GetOrCreateLocalUserState())
            {
                if (scoreToken != null)
                {
                    if (usage.Item?.ScoreTokens.Contains(scoreToken.Value) == false)
                        throw new InvalidOperationException("Incorrect score token supplied.");

                    await scoreBuffer.UpdateAsync(scoreToken.Value, data);
                }

                botGameplay?.ObserveFrames(Context.GetUserId(), data);
                await Clients.Group(GetGroupId(Context.GetUserId())).UserSentFrames(Context.GetUserId(), data);
            }
        }

        public async Task EndPlaySessionV2(long? scoreToken, SpectatedUserState finalState)
        {
            finalState.ThrowIfInvalid();

            bool shouldBroadcastEnd = false;

            using (var usage = await GetOrCreateLocalUserState())
            {
                try
                {
                    shouldBroadcastEnd = scoreToken == null || (scoreToken == usage.Item?.ScoreTokens.LastOrDefault());

                    if (scoreToken != null)
                    {
                        if (usage.Item?.ScoreTokens.Remove(scoreToken.Value) == false)
                            throw new InvalidOperationException("Incorrect score token supplied.");

                        var score = await scoreBuffer.DequeueAsync(scoreToken.Value);
                        if (score == null)
                            return;

                        await processScore(scoreToken.Value, score);

                        int exitTime = (int)Math.Round((score.Score.Replay.Frames.LastOrDefault()?.Time ?? 0) / 1000);

                        if (finalState == SpectatedUserState.Failed || finalState == SpectatedUserState.Quit)
                            await processFailtime(score.Beatmap, exitTime, finalState);

                        await editPlayTime(score.Score, exitTime);
                    }

                    if (usage.Item?.State != null && shouldBroadcastEnd)
                    {
                        usage.Item.State.State = finalState;
                        await endPlaySession(Context.GetUserId(), usage.Item.State);
                    }
                }
                finally
                {
                    if (usage.Item != null && shouldBroadcastEnd)
                    {
                        usage.Item.State = null;
                        usage.Item.Beatmap = null;
                    }
                }
            }
        }

        #endregion

        private async Task processScore(long scoreToken, BufferedScore buffered)
        {
            Debug.Assert(buffered != null);

            if (!AppSettings.EnableAllBeatmapLeaderboard)
            {
                // Do nothing with scores on unranked beatmaps.
                var status = buffered.Score.ScoreInfo.BeatmapInfo!.Status;
                if (status < min_beatmap_status_for_replays || status > max_beatmap_status_for_replays)
                    return;
            }

            if (!buffered.Score.ScoreInfo.Passed)
                return;

            // if the user never hit anything, further processing that depends on the score existing can be waived because the client won't have submitted the score anyway.
            // see: https://github.com/ppy/osu/blob/a47ccb8edd2392258b6b7e176b222a9ecd511fc0/osu.Game/Screens/Play/SubmittingPlayer.cs#L281
            if (!buffered.Score.ScoreInfo.Statistics.Any(s => s.Key.IsHit() && s.Value > 0))
                return;

            buffered.Score.ScoreInfo.Date = DateTimeOffset.UtcNow;
            // this call is a little expensive due to reflection usage, so only run it at the end of score processing
            // even though in theory the rank could be recomputed after every replay frame.
            buffered.Score.ScoreInfo.Rank = StandardisedScoreMigrationTools.ComputeRank(buffered.Score.ScoreInfo);

            await scoreUploader.EnqueueAsync(scoreToken, buffered);
            await scoreProcessedSubscriber.RegisterForSingleScoreAsync(Context.ConnectionId, Context.GetUserId(), scoreToken);
        }

        private async Task processFailtime(database_beatmap beatmap, int exitTime, SpectatedUserState state)
        {
            int beatmapId = beatmap.beatmap_id;
            int totalLength = beatmap.total_length;

            if (totalLength <= 0 || exitTime <= 0)
                return;

            int numSections = 100;
            double sectionLength = (double)totalLength / numSections;

            int sectionIndex = (int)Math.Min(Math.Floor(exitTime / sectionLength), numSections - 1);

            using (var db = databaseFactory.GetInstance())
            {
                var failTime = await db.GetBeatmapFailTimeAsync(beatmapId);

                if (failTime == null)
                {
                    failTime = new fail_time
                    {
                        beatmap_id = beatmapId,
                        exit = new byte[numSections * 4],
                        fail = new byte[numSections * 4],
                    };
                }

                int[] exitArray = BlobHelper.ParseBlobToIntArray(failTime.exit);
                int[] failArray = BlobHelper.ParseBlobToIntArray(failTime.fail);

                if (exitArray.Length < numSections || failArray.Length < numSections)
                    return;

                if (state == SpectatedUserState.Quit)
                    exitArray[sectionIndex]++;
                else if (state == SpectatedUserState.Failed)
                    failArray[sectionIndex]++;

                failTime.exit = BlobHelper.IntArrayToBlob(exitArray);
                failTime.fail = BlobHelper.IntArrayToBlob(failArray);

                await db.UpdateFailTimeAsync(failTime);
            }
        }

        private async Task editPlayTime(Score score, int exitTime)
        {
            if (exitTime <= 0)
                return;

            var ruleset = score.ScoreInfo.Ruleset;

            if (ruleset == null)
                return;

            int userId = score.ScoreInfo.UserID;
            string gameMode = GameModeHelper.GameModeToStringSpecial(ruleset, score.ScoreInfo.APIMods);

            using (var db = databaseFactory.GetInstance())
            {
                int? currentPlayTime = await db.GetUserPlaytimeAsync(gameMode, userId);

                if (currentPlayTime == null)
                    return;

                await db.UpdateUserPlaytimeAsync(gameMode, userId, currentPlayTime.Value + exitTime);
            }
        }

        public async Task StartWatchingUser(int userId)
        {
            using (var db = databaseFactory.GetInstance())
            {
                if (await db.IsUserRestrictedAsync(Context.GetUserId()))
                    throw new InvalidStateException("Can't spectate a user when restricted.");
            }

            Log($"Watching {userId}");

            try
            {
                SpectatorState? spectatorState;

                // send the user's state if exists
                using (var usage = await GetStateFromUser(userId))
                    spectatorState = usage.Item?.State;

                if (spectatorState != null)
                    await Clients.Caller.UserBeganPlaying(userId, spectatorState);
            }
            catch (KeyNotFoundException)
            {
                // user isn't tracked.
            }

            using (var state = await GetOrCreateLocalUserState())
            {
                var clientState = state.Item ??= new SpectatorClientState(Context.ConnectionId, Context.GetUserId());
                clientState.WatchedUsers.Add(userId);
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, GetGroupId(userId));

            int watcherId = Context.GetUserId();
            string? watcherUsername;
            using (var db = databaseFactory.GetInstance())
                watcherUsername = await db.GetUsernameAsync(watcherId);

            if (watcherUsername == null)
                return;

            var watcher = new SpectatorUser
            {
                OnlineID = watcherId,
                Username = watcherUsername,
            };

            await Clients.User(userId.ToString()).UserStartedWatching([watcher]);
        }

        public async Task EndWatchingUser(int userId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetGroupId(userId));

            using (var state = await GetOrCreateLocalUserState())
            {
                var clientState = state.Item ??= new SpectatorClientState(Context.ConnectionId, Context.GetUserId());
                clientState.WatchedUsers.Remove(userId);
            }

            int watcherId = Context.GetUserId();

            await Clients.User(userId.ToString()).UserEndedWatching(watcherId);
        }

        protected override async Task CleanUpState(ItemUsage<SpectatorClientState> state)
        {
            Debug.Assert(state.Item != null);

            if (state.Item.State != null)
                await endPlaySession(state.Item.UserId, state.Item.State);

            foreach (int watchedUserId in state.Item.WatchedUsers)
                await Clients.User(watchedUserId.ToString()).UserEndedWatching(state.Item.UserId);

            await base.CleanUpState(state);
        }

        public static string GetGroupId(int userId) => $"watch:{userId}";

        private async Task endPlaySession(int userId, SpectatorState state)
        {
            // Ensure that the state is no longer playing (e.g. if client crashes).
            if (state.State == SpectatedUserState.Playing)
                state.State = SpectatedUserState.Quit;

            await Clients.Group(GetGroupId(userId)).UserFinishedPlaying(userId, state);
        }
    }
}
