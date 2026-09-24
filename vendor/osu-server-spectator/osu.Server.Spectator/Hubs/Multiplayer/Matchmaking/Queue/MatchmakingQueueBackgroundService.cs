// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using osu.Game.Online.API;
using osu.Game.Online.Matchmaking;
using osu.Game.Online.Matchmaking.Requests;
using osu.Game.Online.Matchmaking.Responses;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Elo;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Services;
using StatsdClient;

namespace osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Queue
{
    public class MatchmakingQueueBackgroundService : BackgroundService, IMatchmakingQueueBackgroundService
    {
        /// <summary>
        /// The rate at which the matchmaking queue is updated.
        /// </summary>
        private static readonly TimeSpan queue_update_rate = AppSettings.MatchmakingQueueUpdateRate;

        /// <summary>
        /// The rate at which users are sent lobby status updates.
        /// </summary>
        private static readonly TimeSpan lobby_update_rate = AppSettings.MatchmakingLobbyUpdateRate;

        private const string statsd_prefix = "matchmaking";
        private static string queue_ban_expiry(int userId) => $"matchmaking-ban-expiry:{userId}";
        private static string recent_duel_expiry(int userId) => $"matchmaking-recent-duel-expiry:{userId}";

        private readonly ConcurrentDictionary<int, MatchmakingLobby> poolLobbies = new ConcurrentDictionary<int, MatchmakingLobby>();
        private readonly ConcurrentDictionary<int, MatchmakingQueue> poolQueues = new ConcurrentDictionary<int, MatchmakingQueue>();
        private readonly ConcurrentDictionary<Guid, MatchmakingQueue> duelQueues = new ConcurrentDictionary<Guid, MatchmakingQueue>();
        private readonly ConcurrentDictionary<uint, MatchmakingBeatmapSelector> poolSelectors = new ConcurrentDictionary<uint, MatchmakingBeatmapSelector>();
        private readonly ConcurrentDictionary<long, int> pendingDodgePenalties = new ConcurrentDictionary<long, int>();
        private readonly object dodgeRetryLock = new object();
        private Task dodgeRetryTask = Task.CompletedTask;
        private readonly RankedDodgeOutbox? dodgeOutbox;
        private readonly ConcurrentDictionary<string, PartyLease> partyLeases = new ConcurrentDictionary<string, PartyLease>();
        private readonly object partyRefreshLock = new object();
        private Task partyRefreshTask = Task.CompletedTask;
        private readonly RankedPartyOutbox? partyOutbox;

        private sealed class PartyLease(RankedPartyReservation reservation, int poolId)
        {
            public readonly RankedPartyReservation Reservation = reservation;
            public readonly int PoolId = poolId;
            public DateTimeOffset NextRefresh = DateTimeOffset.UtcNow.AddSeconds(60);
            public volatile bool Release;
            public volatile bool InUse;
        }

        private readonly IHubContext<MultiplayerHub> hub;
        private readonly ISharedInterop sharedInterop;
        private readonly IDatabaseFactory databaseFactory;
        private readonly EntityStore<ServerMultiplayerRoom> rooms;
        private readonly IMultiplayerRoomController roomController;
        private readonly ILoggerFactory loggerFactory;
        private readonly ILogger logger;
        private readonly IMemoryCache memoryCache;
        private readonly MultiplayerEventDispatcher eventDispatcher;
        private readonly RulesetManager rulesetManager;

        private DateTimeOffset lastLobbyUpdateTime = DateTimeOffset.UnixEpoch;
        private DateTimeOffset lastQueueRefreshTime = DateTimeOffset.UnixEpoch;
        private DateTimeOffset lastPoolUpdateTime = DateTimeOffset.UnixEpoch;
        private DateTimeOffset lastPoolRefreshTime = DateTimeOffset.UnixEpoch;

        public MatchmakingQueueBackgroundService(IHubContext<MultiplayerHub> hub, ISharedInterop sharedInterop, IDatabaseFactory databaseFactory, ILoggerFactory loggerFactory,
                                                 EntityStore<ServerMultiplayerRoom> rooms, IMultiplayerRoomController roomController, IMemoryCache memoryCache,
                                                 MultiplayerEventDispatcher eventDispatcher, RulesetManager rulesetManager)
        {
            this.hub = hub;
            this.sharedInterop = sharedInterop;
            this.databaseFactory = databaseFactory;
            this.rooms = rooms;
            this.roomController = roomController;
            this.memoryCache = memoryCache;
            this.eventDispatcher = eventDispatcher;
            this.rulesetManager = rulesetManager;

            this.loggerFactory = loggerFactory;
            logger = loggerFactory.CreateLogger(nameof(MatchmakingQueueBackgroundService));

            string? outboxPath = Environment.GetEnvironmentVariable("SOMS_DODGE_OUTBOX");
            if (!string.IsNullOrWhiteSpace(outboxPath))
            {
                dodgeOutbox = new RankedDodgeOutbox(outboxPath);
                foreach ((long roomId, int userId) in dodgeOutbox.Load())
                    pendingDodgePenalties.TryAdd(roomId, userId);
                partyOutbox = new RankedPartyOutbox(Path.Combine(Path.GetDirectoryName(outboxPath)!, "ranked-party-leases"));
                foreach (RankedPartyOutbox.Entry entry in partyOutbox.Load())
                    partyLeases.TryAdd(entry.Reservation.ReservationId, new PartyLease(entry.Reservation, entry.PoolId) { Release = true });
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            partyOutbox?.Dispose();
        }

        public Task RecordMatch(int poolId, MatchRoomState status)
        {
            if (status is RankedPlayRoomState finished)
            {
                foreach (PartyLease lease in partyLeases.Values.Where(lease => lease.PoolId == poolId && lease.Reservation.Members.All(finished.Users.ContainsKey)))
                    lease.Release = true;
                startPartyRefresh();
            }
            if (!poolLobbies.TryGetValue(poolId, out MatchmakingLobby? lobby))
                return Task.CompletedTask;

            if (!poolQueues.TryGetValue(poolId, out MatchmakingQueue? queue))
                return Task.CompletedTask;

            lobby.RecordMatch(status);

            if (status is RankedPlayRoomState rpState)
            {
                int[] users = rpState.Users.Keys.ToArray();

                for (int i = 0; i < users.Length; i++)
                {
                    for (int j = i + 1; j < users.Length; j++)
                    {
                        // Team state preserves A1,B1,A2,B2 insertion order.
                        if (users.Length == 4 && queue.Pool.lobby_size == 4 && i % 2 == j % 2)
                            continue;
                        queue.MarkRecentMatchup(users[i], users[j]);
                    }
                }
            }

            return Task.CompletedTask;
        }

        public async Task RecordBeatmapResult(uint poolId, int beatmapId, APIMod[] mods, int[] scores, EloRating[] ratings)
        {
            if (!poolQueues.TryGetValue((int)poolId, out MatchmakingQueue? queue))
                return;

            if (!poolSelectors.TryGetValue(poolId, out MatchmakingBeatmapSelector? selector))
                poolSelectors[poolId] = selector = await MatchmakingBeatmapSelector.Initialise(queue.Pool, databaseFactory);

            await selector.AdjustRating(new MatchmakingBeatmapSelector.BeatmapLookupKey(beatmapId, mods.Length == 0 ? string.Empty : JsonConvert.SerializeObject(mods)), scores, ratings);
        }

        public bool IsInQueue(MultiplayerClientState state)
        {
            foreach ((_, MatchmakingQueue queue) in poolQueues)
            {
                if (queue.IsInQueue(new MatchmakingQueueUser(state.ConnectionId)))
                    return true;
            }

            return false;
        }

        public async Task AddToLobbyAsync(MultiplayerClientState state, int poolId)
        {
            // Users should only ever be in one lobby at a time.
            await RemoveFromLobbyAsync(state);

            using (var db = databaseFactory.GetInstance())
            {
                matchmaking_pool pool = await db.GetMatchmakingPoolAsync((uint)poolId) ?? throw new InvalidStateException($"Pool not found: {poolId}");

                if (!pool.active)
                    throw new InvalidStateException("The selected matchmaking pool is no longer active.");

                MatchmakingLobby lobby = poolLobbies.GetOrAdd(poolId, _ => new MatchmakingLobby(poolId, hub, databaseFactory)
                {
                    LookupQueue = id => poolQueues.GetValueOrDefault(id)
                });

                await lobby.Add(state);
            }
        }

        public async Task RemoveFromLobbyAsync(MultiplayerClientState state)
        {
            foreach ((_, MatchmakingLobby lobby) in poolLobbies)
                await lobby.Remove(state);
        }

        public async Task AddToQueueAsync(MultiplayerClientState state, int poolId)
        {
            // Users should only ever be in one queue at a time.
            await RemoveFromQueueAsync(state);

            using (var db = databaseFactory.GetInstance())
            {
                matchmaking_pool pool = await db.GetMatchmakingPoolAsync((uint)poolId) ?? throw new InvalidStateException($"Pool not found: {poolId}");

                if (!pool.active)
                    throw new InvalidStateException("The selected matchmaking pool is no longer active.");

                MatchmakingQueue queue = poolQueues.GetOrAdd(poolId, _ => new MatchmakingQueue(pool));
                await processBundle(queue.Add(await createUserAsync(state, pool)));
            }
        }

        public async Task RemoveFromQueueAsync(MultiplayerClientState state)
        {
            foreach ((_, MatchmakingQueue queue) in poolQueues)
                await processBundle(queue.Remove(new MatchmakingQueueUser(state.ConnectionId)));

            foreach ((_, MatchmakingQueue queue) in duelQueues)
                await processBundle(queue.Remove(new MatchmakingQueueUser(state.ConnectionId)));
        }

        public async Task<MatchmakingIssueDuelResponse> IssueDuelAsync(MultiplayerClientState state, MatchmakingIssueDuelRequest request)
        {
            DateTimeOffset recentDuelExpiry = memoryCache.Get<DateTimeOffset?>(recent_duel_expiry(state.UserId)) ?? DateTimeOffset.MinValue;
            if (DateTimeOffset.Now < recentDuelExpiry)
                throw new InvalidStateException("You are requesting too many duels. Slow down.");

            DateTimeOffset duelExpiry = DateTimeOffset.Now + TimeSpan.FromSeconds(30);
            memoryCache.Set(recent_duel_expiry(state.UserId), duelExpiry, new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = duelExpiry,
                Priority = CacheItemPriority.NeverRemove
            });

            // Users should only ever be in one queue at a time.
            await RemoveFromQueueAsync(state);

            using (var db = databaseFactory.GetInstance())
            {
                matchmaking_pool pool = await db.GetMatchmakingPoolAsync((uint)request.PoolId) ?? throw new InvalidStateException($"Pool not found: {request.PoolId}");
                pool.lobby_size = 2;
                pool.rating_search_radius = int.MaxValue;
                pool.rating_search_radius_max = int.MaxValue;
                pool.ranked = false;

                if (!pool.active)
                    throw new InvalidStateException("The selected matchmaking pool is no longer active.");

                MatchmakingQueue queue = new MatchmakingQueue(pool)
                {
                    SearchTimeout = TimeSpan.FromMinutes(5),
                    RequeueOnDecline = false
                };

                MatchmakingQueueUser user = await createUserAsync(state, pool);
                user.BanEndTime = DateTimeOffset.MinValue;

                // The user is added to the queue before the queue is added to the dictionary
                // so that the periodic update doesn't discard the queue due to a lack of users.
                MatchmakingQueueUpdateBundle updateBundle = queue.Add(user);

                Guid duelGuid = Guid.NewGuid();
                if (!duelQueues.TryAdd(duelGuid, queue))
                    throw new InvalidStateException("Failed to issue the duel.");

                await processBundle(updateBundle);

                await hub.Clients.User(request.UserId.ToString()).SendAsync(nameof(IMatchmakingClient.MatchmakingDuelIssued), new MatchmakingDuelIssuedParams
                {
                    Id = duelGuid,
                    Pool = pool.ToMatchmakingPool(),
                    UserId = state.UserId
                });

                return new MatchmakingIssueDuelResponse();
            }
        }

        public async Task<MatchmakingAcceptDuelResponse> AcceptDuelAsync(MultiplayerClientState state, MatchmakingAcceptDuelRequest request)
        {
            // This could happen if the challenger cancelled the request. In which case, return an empty success.
            if (!duelQueues.TryGetValue(request.Id, out MatchmakingQueue? queue))
                return new MatchmakingAcceptDuelResponse();

            // Remove the user from all matchmaking queues.
            foreach ((_, MatchmakingQueue q) in poolQueues)
                await processBundle(q.Remove(new MatchmakingQueueUser(state.ConnectionId)));

            // Remove the user from all other duel queues.
            foreach ((_, MatchmakingQueue q) in duelQueues)
            {
                if (q != queue)
                    await processBundle(q.Remove(new MatchmakingQueueUser(state.ConnectionId)));
            }

            // Add the user to the duel queue.
            MatchmakingQueueUser user = await createUserAsync(state, queue.Pool);
            user.BanEndTime = DateTimeOffset.MinValue;
            await processBundle(queue.Add(user));

            return new MatchmakingAcceptDuelResponse();
        }

        public async Task AcceptInvitationAsync(MultiplayerClientState state)
        {
            // Immediately notify the incoming user of their intent to join the match.
            await hub.Clients.Client(state.ConnectionId).SendAsync(nameof(IMatchmakingClient.MatchmakingQueueStatusChanged), new MatchmakingQueueStatus.JoiningMatch());

            foreach ((_, MatchmakingQueue queue) in poolQueues)
                await processBundle(queue.MarkInvitationAccepted(new MatchmakingQueueUser(state.ConnectionId)));

            foreach ((_, MatchmakingQueue queue) in duelQueues)
                await processBundle(queue.MarkInvitationAccepted(new MatchmakingQueueUser(state.ConnectionId)));
        }

        public async Task DeclineInvitationAsync(MultiplayerClientState state)
        {
            foreach ((_, MatchmakingQueue queue) in poolQueues)
                await processBundle(queue.MarkInvitationDeclined(new MatchmakingQueueUser(state.ConnectionId)));

            foreach ((_, MatchmakingQueue queue) in duelQueues)
                await processBundle(queue.MarkInvitationDeclined(new MatchmakingQueueUser(state.ConnectionId)));
        }

        public void BanUser(int userId, TimeSpan duration)
        {
            if (!AppSettings.MatchmakingQueueAllowBans)
                return;

            // TODO: we should probably let the players know that they have been penalised.

            DateTimeOffset expireTime = DateTimeOffset.Now + duration;
            memoryCache.Set(queue_ban_expiry(userId), expireTime, new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = expireTime,
                Priority = CacheItemPriority.NeverRemove
            });
        }

        public async Task AddReservedToQueueAsync(MultiplayerClientState[] states, int poolId, RankedPartyReservation reservation)
        {
            using var db = databaseFactory.GetInstance();
            matchmaking_pool pool = await db.GetMatchmakingPoolAsync((uint)poolId) ?? throw new InvalidStateException("Ranked pool not found.");
            if (!pool.active || pool.type != matchmaking_pool_type.ranked_play || !pool.ranked || pool.lobby_size is not (2 or 4))
                throw new InvalidStateException("This pool is not an active Ranked queue.");
            if (states.Length is < 1 or > 2 || (pool.lobby_size == 2 && states.Length != 1)
                || !states.Select(state => state.UserId).Order().SequenceEqual(reservation.Members.Order())
                || !Guid.TryParse(reservation.ReservationId, out _))
                throw new InvalidStateException("Invalid Ranked party reservation.");
            if (states.Any(state => state.CurrentRoomID != null))
                throw new InvalidStateException("Leave the current room before searching for Ranked.");

            if (poolQueues.TryGetValue(poolId, out MatchmakingQueue? existingQueue)
                && states.All(state => existingQueue.GetAllUsers().Any(user => user.UserId == state.UserId && user.ReservationId == reservation.ReservationId)))
                return;

            var users = new List<MatchmakingQueueUser>();
            foreach (MultiplayerClientState state in states)
            {
                if (await db.IsUserRestrictedAsync(state.UserId))
                    throw new InvalidStateException("A party member is restricted.");
                MatchmakingQueueUser user = await createUserAsync(state, pool);
                user.PartyId = states.Length == 2 ? reservation.ReservationId : null;
                user.ReservationId = reservation.ReservationId;
                users.Add(user);
            }
            foreach (MultiplayerClientState state in states)
                await RemoveFromQueueAsync(state);
            TrackPartyReservation(reservation, poolId);
            MatchmakingQueue queue = poolQueues.GetOrAdd(poolId, _ => new MatchmakingQueue(pool));
            try
            {
                partyLeases[reservation.ReservationId].InUse = true;
                await processBundle(queue.AddRange(users.ToArray()));
            }
            catch
            {
                foreach (MatchmakingQueueUser user in users)
                    await processBundle(queue.Remove(user));
                throw;
            }
        }

        public void TrackPartyReservation(RankedPartyReservation reservation, int poolId)
        {
            lock (partyRefreshLock)
            {
                if (partyLeases.ContainsKey(reservation.ReservationId))
                    return;
                partyOutbox?.Save(reservation, poolId);
                partyLeases.TryAdd(reservation.ReservationId, new PartyLease(reservation, poolId));
            }
        }

        public void AbandonUnusedPartyReservation(string reservationId)
        {
            if (partyLeases.TryGetValue(reservationId, out PartyLease? lease) && !lease.InUse)
                lease.Release = true;
            startPartyRefresh();
        }

        private void releaseReservations(IEnumerable<MatchmakingQueueUser> users)
        {
            foreach (string? id in users.Select(user => user.ReservationId).Where(id => id != null).Distinct())
            {
                if (partyLeases.TryGetValue(id!, out PartyLease? lease))
                    lease.Release = true;
            }
            startPartyRefresh();
        }

        private void startPartyRefresh()
        {
            lock (partyRefreshLock)
            {
                if (partyRefreshTask.IsCompleted)
                    partyRefreshTask = Task.WhenAll(partyLeases.Values.Where(lease => lease.Release || lease.NextRefresh <= DateTimeOffset.UtcNow).Select(refreshPartyLease));
            }
        }

        private async Task refreshPartyLease(PartyLease lease)
        {
            try
            {
                if (lease.Release)
                {
                    await sharedInterop.ReleasePartyReservationAsync(lease.Reservation.ReservationId);
                    partyOutbox?.Remove(lease.Reservation.ReservationId);
                    partyLeases.TryRemove(lease.Reservation.ReservationId, out _);
                }
                else
                {
                    await sharedInterop.RenewPartyReservationAsync(lease.Reservation.ReservationId);
                    lease.NextRefresh = DateTimeOffset.UtcNow.AddSeconds(60);
                }
            }
            catch (Exception ex)
            {
                lease.NextRefresh = DateTimeOffset.UtcNow.AddSeconds(5);
                logger.LogError(ex, "Failed to refresh Ranked party reservation {id}.", lease.Reservation.ReservationId);
            }
        }

        public Task RegisterRankedDodgeAsync(long roomId, int userId)
        {
            lock (dodgeRetryLock)
            {
                if (!pendingDodgePenalties.ContainsKey(roomId))
                {
                    // Finish the local durable write before ending the room.
                    // Remote HTTP runs independently of room/queue actions.
                    dodgeOutbox?.Save(roomId, userId);
                    pendingDodgePenalties.TryAdd(roomId, userId);
                }
            }
            startDodgePenaltyRetries();
            return Task.CompletedTask;
        }

        private void startDodgePenaltyRetries()
        {
            lock (dodgeRetryLock)
            {
                if (dodgeRetryTask.IsCompleted)
                    dodgeRetryTask = Task.WhenAll(pendingDodgePenalties.ToArray().Select(entry => tryPersistRankedDodge(entry.Key, entry.Value)));
            }
        }

        private async Task tryPersistRankedDodge(long roomId, int userId)
        {
            try
            {
                await persistRankedDodge(roomId, userId);
                dodgeOutbox?.Remove(roomId);
                pendingDodgePenalties.TryRemove(roomId, out _);
            }
            catch (Exception ex)
            {
                // The match still ends without Elo. Queue entry stays blocked
                // while the background worker retries the idempotent write.
                logger.LogError(ex, "Retrying Ranked dodge penalty for room {roomId}, user {userId}.", roomId, userId);
            }
        }

        private async Task persistRankedDodge(long roomId, int userId)
        {
            RankedDodgeStatus penalty = await sharedInterop.RegisterRankedDodgeAsync(roomId, userId);
            DateTimeOffset expiry = penalty.AccountBanned ? DateTimeOffset.MaxValue : penalty.ExpiresAt ?? DateTimeOffset.MinValue;
            foreach (MatchmakingQueue queue in poolQueues.Values.Concat(penalty.AccountBanned ? duelQueues.Values : []))
            {
                foreach (MatchmakingQueueUser queuedUser in queue.GetAllUsers().Where(u => u.UserId == penalty.UserId))
                    await processBundle(queue.Remove(queuedUser));
            }

            // The authoritative expiry is persisted by the app and is loaded on
            // every queue entry; the cache also covers existing spectator paths.
            if (expiry > DateTimeOffset.UtcNow)
                memoryCache.Set(queue_ban_expiry(penalty.UserId), expiry, expiry);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ExecuteOnceAsync();
                await Task.Delay(queue_update_rate, stoppingToken);
            }
        }

        /// <summary>
        /// Executes a single update of the queues.
        /// </summary>
        public async Task ExecuteOnceAsync()
        {
            // Penalty writes never block the room or other players' matchmaking.
            startDodgePenaltyRetries();
            startPartyRefresh();

            try
            {
                await updateLobbies();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to update the matchmaking lobby.");
            }

            try
            {
                await refreshQueues();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to refresh the matchmaking queue.");
            }

            await refreshPools();

            foreach ((_, MatchmakingQueue queue) in poolQueues)
            {
                DogStatsd.Gauge($"{statsd_prefix}.queue.users", queue.Count, tags: [$"queue:{queue.Pool.DisplayName}"]);

                foreach (var user in queue.GetAllUsers())
                    DogStatsd.Histogram($"{statsd_prefix}.queue.users.rating", user.Rating.Mu, tags: [$"queue:{queue.Pool.DisplayName}"]);

                try
                {
                    await processBundle(queue.Update());
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to update the matchmaking queue for pool {poolId}.", queue.Pool.id);
                }
            }

            foreach ((Guid duelGuid, MatchmakingQueue queue) in duelQueues.ToArray())
            {
                if (queue.Count == 0)
                    duelQueues.Remove(duelGuid, out _);
                else
                    await processBundle(queue.Update());
            }
        }

        private async Task updateLobbies()
        {
            if (DateTimeOffset.Now - lastLobbyUpdateTime < lobby_update_rate)
                return;

            foreach ((_, MatchmakingLobby lobby) in poolLobbies)
                await lobby.Update();

            lastLobbyUpdateTime = DateTimeOffset.Now;
        }

        private async Task refreshQueues()
        {
            if (DateTimeOffset.Now - lastQueueRefreshTime < TimeSpan.FromMinutes(1))
                return;

            using (var db = databaseFactory.GetInstance())
            {
                foreach ((_, MatchmakingQueue queue) in poolQueues)
                    await processBundle(await queue.Refresh(db));
            }

            lastQueueRefreshTime = DateTimeOffset.Now;
        }

        private async Task refreshPools()
        {
            if (DateTimeOffset.Now - lastPoolUpdateTime >= TimeSpan.FromSeconds(5))
            {
                foreach (var selector in poolSelectors.Values)
                    await selector.Update();

                lastPoolUpdateTime = DateTimeOffset.Now;
            }

            if (DateTimeOffset.Now - lastPoolRefreshTime >= TimeSpan.FromHours(1))
            {
                foreach (var selector in poolSelectors.Values)
                    await selector.Update();

                poolSelectors.Clear();

                lastPoolRefreshTime = DateTimeOffset.Now;
            }
        }

        private async Task processBundle(MatchmakingQueueUpdateBundle bundle)
        {
            releaseReservations(bundle.RemovedUsers);
            if (bundle.Queue.Pool.ranked)
            {
                foreach (var user in bundle.DeclinedUsers)
                    BanUser(user.UserId, TimeSpan.FromMinutes(1));
            }

            foreach (var user in bundle.RemovedUsers)
                await hub.Clients.Client(user.Identifier).SendAsync(nameof(IMatchmakingClient.MatchmakingQueueLeft));

            foreach (var user in bundle.AddedUsers)
            {
                await hub.Clients.Client(user.Identifier).SendAsync(nameof(IMatchmakingClient.MatchmakingQueueJoined));
                await hub.Clients.Client(user.Identifier).SendAsync(nameof(IMatchmakingClient.MatchmakingQueueStatusChanged), new MatchmakingQueueStatus.Searching());
            }

            foreach (var group in bundle.RecycledGroups)
            {
                DogStatsd.Increment($"{statsd_prefix}.groups.recycled");

                foreach (var user in group.Users)
                    await hub.Groups.RemoveFromGroupAsync(user.Identifier, group.Identifier);
            }

            foreach (var group in bundle.FormedGroups)
            {
                DogStatsd.Increment($"{statsd_prefix}.groups.formed");

                foreach (var user in group.Users)
                    await hub.Groups.AddToGroupAsync(user.Identifier, group.Identifier, CancellationToken.None);

                // Obsolete method call for older clients that support quick play.
                // It is not important that this is invoked for ranked play too, because these clients can only queue for quick play in the first place.
                await hub.Clients.Group(group.Identifier).SendAsync(nameof(IMatchmakingClient.MatchmakingRoomInvited));

                await hub.Clients.Group(group.Identifier).SendAsync(nameof(IMatchmakingClient.MatchmakingRoomInvitedWithParams), new MatchmakingRoomInvitationParams
                {
                    Type = bundle.Queue.Pool.type.ToPoolType()
                });

                await hub.Clients.Group(group.Identifier).SendAsync(nameof(IMatchmakingClient.MatchmakingQueueStatusChanged), new MatchmakingQueueStatus.MatchFound());
            }

            foreach (var group in bundle.CompletedGroups)
            {
                long? createdRoomId = null;
                try
                {
                    DogStatsd.Increment($"{statsd_prefix}.groups.completed");

                    // A long app outage may outlive a queue's lease. Never start
                    // a match using an expired reservation or a changed party.
                    foreach (string? reservationId in group.Users.Select(user => user.ReservationId).Where(id => id != null).Distinct())
                    {
                        if (!partyLeases.TryGetValue(reservationId!, out PartyLease? lease) || lease.Release)
                            throw new InvalidStateException("The party reservation is no longer active.");
                        await sharedInterop.RenewPartyReservationAsync(reservationId!);
                        lease.NextRefresh = DateTimeOffset.UtcNow.AddSeconds(60);
                    }

                    foreach (var user in group.Users)
                        DogStatsd.Timer($"{statsd_prefix}.queue.duration", (DateTimeOffset.Now - user.SearchStartTime).TotalMilliseconds, tags: [$"queue:{bundle.Queue.Pool.DisplayName}"]);

                    foreach (double rating in group.DeltaRatings())
                        DogStatsd.Histogram($"{statsd_prefix}.groups.ratingdelta", rating, tags: [$"queue:{bundle.Queue.Pool.DisplayName}"]);

                    string roomName;

                    switch (bundle.Queue.Pool.type)
                    {
                        case matchmaking_pool_type.ranked_play:
                            string userName1;
                            string userName2;

                            using (var db = databaseFactory.GetInstance())
                            {
                                userName1 = (await db.GetUsernameAsync(group.Users[0].UserId))!;
                                userName2 = (await db.GetUsernameAsync(group.Users[1].UserId))!;
                                if (group.Users.Length == 4)
                                {
                                    userName1 += " + " + await db.GetUsernameAsync(group.Users[2].UserId);
                                    userName2 += " + " + await db.GetUsernameAsync(group.Users[3].UserId);
                                }
                            }

                            roomName = $"{bundle.Queue.Pool.DisplayName}: {userName1} vs {userName2}";
                            break;

                        default:
                            roomName = bundle.Queue.Pool.DisplayName;
                            break;
                    }

                    string roomPassword = Guid.NewGuid().ToString();

                    // Re-check the effective catalogue after invitations. If local
                    // unranking removed the pool, return both players to idle.
                    if (poolSelectors.TryGetValue(bundle.Queue.Pool.id, out MatchmakingBeatmapSelector? previousSelector))
                        await previousSelector.Update();
                    MatchmakingBeatmapSelector beatmapSelector = await MatchmakingBeatmapSelector.Initialise(bundle.Queue.Pool, databaseFactory);
                    poolSelectors[bundle.Queue.Pool.id] = beatmapSelector;
                    if (bundle.Queue.Pool.type == matchmaking_pool_type.ranked_play && beatmapSelector.GlobalBeatmaps.Count < 5 * bundle.Queue.Pool.lobby_size)
                    {
                        releaseReservations(group.Users);
                        await hub.Clients.Group(group.Identifier).SendAsync(nameof(IMatchmakingClient.MatchmakingQueueLeft));
                        continue;
                    }

                    long roomId = await sharedInterop.CreateRoomAsync(AppSettings.BanchoBotUserId, new MultiplayerRoom(0)
                    {
                        Settings =
                        {
                            Name = roomName,
                            MatchType = bundle.Queue.Pool.type.ToMatchType(),
                            Password = roomPassword
                        }
                    });
                    createdRoomId = roomId;

                    // Initialise the room and users
                    using (var roomUsage = await rooms.GetForUse(roomId, true))
                    {
                        ServerMultiplayerRoom room = await ServerMultiplayerRoom.InitialiseMatchmakingRoomAsync(roomId, roomController, databaseFactory, eventDispatcher, loggerFactory, bundle.Queue.Pool,
                            group.Users, beatmapSelector, rulesetManager, this);

                        await room.SetEndDateAsync(DateTimeOffset.Now + TimeSpan.FromMinutes(5));

                        roomUsage.Item = room;
                    }

                    await eventDispatcher.PostMatchmakingRoomCreatedAsync(roomId, new MatchmakingRoomCreatedEventDetail
                    {
                        pool_id = (int)bundle.Queue.Pool.id
                    });

                    await hub.Clients.Group(group.Identifier).SendAsync(nameof(IMatchmakingClient.MatchmakingRoomReady), roomId, roomPassword);
                }
                catch (Exception exception)
                {
                    releaseReservations(group.Users);
                    // A completed group has already left the queue. Without a
                    // cancellation, the first accepter waits forever while only
                    // the second sees the failed hub invocation.
                    logger.LogError(exception, "Failed to create matchmaking room for pool {poolId}", bundle.Queue.Pool.id);
                    if (createdRoomId is long roomId)
                    {
                        try
                        {
                            using var db = databaseFactory.GetInstance();
                            await db.SetRoomEndDateAsync(new MultiplayerRoom(roomId), DateTimeOffset.Now);
                        }
                        catch (Exception cleanupException)
                        {
                            logger.LogError(cleanupException, "Failed to close incomplete matchmaking room {roomId}", roomId);
                        }
                        await rooms.Destroy(roomId);
                    }

                    await hub.Clients.Group(group.Identifier).SendAsync(nameof(IMatchmakingClient.MatchmakingQueueLeft));
                }
                finally
                {
                    foreach (var user in group.Users)
                        await hub.Groups.RemoveFromGroupAsync(user.Identifier, group.Identifier);
                }
            }
        }

        private async Task<MatchmakingQueueUser> createUserAsync(MultiplayerClientState state, matchmaking_pool pool)
        {
            if (pool.ranked && pendingDodgePenalties.Values.Contains(state.UserId))
                throw new InvalidStateException("SOMS!: сохраняется штраф за выход из Ranked. Повторите позже.");

            RankedDodgeStatus? penalty = await sharedInterop.GetRankedDodgeStatusAsync(state.UserId);
            if (penalty?.AccountBanned == true)
                throw new InvalidStateException("SOMS!: аккаунт заблокирован за повторные выходы из Ranked.");
            if (pool.ranked && penalty?.ExpiresAt > DateTimeOffset.UtcNow)
                throw new InvalidStateException($"SOMS!: поиск Ranked заблокирован до {penalty.ExpiresAt:u} (UTC), штраф {penalty.Level}/14 за выход до Replace.");

            using (var db = databaseFactory.GetInstance())
            {
                matchmaking_user_stats? stats = await db.GetMatchmakingUserStatsAsync(state.UserId, pool.id);

                if (stats == null)
                {
                    // Estimate initial elo from PP.
                    double pp = await db.GetUserPPAsync(state.UserId, pool.ruleset_id, pool.variant_id);
                    double eloEstimate = -4000 + 600 * Math.Log(pp + 4000);

                    await db.UpdateMatchmakingUserStatsAsync(stats = new matchmaking_user_stats
                    {
                        user_id = (uint)state.UserId,
                        pool_id = pool.id,
                        EloData =
                        {
                            InitialRating = new EloRating(eloEstimate),
                            Rating = new EloRating(eloEstimate)
                        }
                    });
                }

                return new MatchmakingQueueUser(state.ConnectionId)
                {
                    UserId = state.UserId,
                    Rating = stats.EloData.Rating,
                    BanEndTime = memoryCache.Get<DateTimeOffset?>(queue_ban_expiry(state.UserId)) ?? DateTimeOffset.MinValue
                };
            }
        }
    }
}
