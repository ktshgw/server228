// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;
using osu.Game.Online.RankedPlay;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs.Multiplayer;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Elo;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Queue;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.RankedPlay;
using osu.Server.Spectator.Tests.Multiplayer;
using Xunit;

namespace osu.Server.Spectator.Tests.RankedPlay
{
    /// <summary>
    /// Abstract class for testing a <see cref="RankedPlayStageImplementation"/>.
    /// </summary>
    [Collection("Tests that adjust global AppSettings")]
    public abstract class RankedPlayStageImplementationTest : MultiplayerTest, IAsyncLifetime
    {
        protected ServerMultiplayerRoom Room { get; private set; } = null!;
        protected RankedPlayMatchController MatchController => (RankedPlayMatchController)Room.MatchController;

        protected RankedPlayRoomState RoomState => (RankedPlayRoomState)Room.MatchState!;
        protected RankedPlayUserInfo UserState => RoomState.Users[USER_ID];
        protected RankedPlayUserInfo User2State => RoomState.Users[USER_ID_2];
        protected virtual bool Ranked => true;
        protected virtual int[] MatchUserIds => [USER_ID, USER_ID_2];
        protected virtual uint PoolId => 0;
        protected Mock<IMatchmakingQueueBackgroundService> QueueService { get; } = new Mock<IMatchmakingQueueBackgroundService>();

        private readonly RankedPlayStage stage;

        protected RankedPlayStageImplementationTest(RankedPlayStage stage)
        {
            this.stage = stage;

            AppSettings.MatchmakingRoomRounds = 2;
            AppSettings.MatchmakingRoomAllowSkip = true;

            Database.Setup(db => db.GetRealtimeRoomAsync(ROOM_ID))
                    .Callback<long>(roomId => InitialiseRoom(roomId, 20))
                    .ReturnsAsync(() => new multiplayer_room
                    {
                        type = database_match_type.ranked_play,
                        ends_at = DateTimeOffset.Now.AddMinutes(5),
                        user_id = int.Parse(Hub.Context.UserIdentifier!),
                    });

            Database.Setup(db => db.GetMatchmakingUserStatsAsync(It.IsAny<int>(), It.IsAny<uint>()))
                    .Returns<int, uint>((userId, poolId) => Task.FromResult<matchmaking_user_stats?>(new matchmaking_user_stats
                    {
                        user_id = (uint)userId,
                        pool_id = poolId
                    }));

            Database.Setup(db => db.GetAllScoresForPlaylistItem(It.IsAny<long>()))
                    .Returns<long>(_ => Task.FromResult<IEnumerable<SoloScore>>(
                    [
                        new SoloScore { user_id = USER_ID, total_score = 1_000_000 },
                        new SoloScore { user_id = USER_ID_2, total_score = 1_000_000 },
                    ]));
        }

        public async Task InitializeAsync()
        {
            using (var room = await Rooms.GetForUse(ROOM_ID, true))
            {
                room.Item = await ServerMultiplayerRoom.InitialiseMatchmakingRoomAsync(ROOM_ID, RoomController, DatabaseFactory.Object, EventDispatcher, LoggerFactory.Object,
                    new matchmaking_pool { id = PoolId, type = matchmaking_pool_type.ranked_play, ranked = Ranked, lobby_size = MatchUserIds.Length },
                    MatchUserIds.Select(u => new MatchmakingQueueUser(u.ToString()) { UserId = u, Rating = new EloRating(1500) }).ToArray(),
                    new MatchmakingBeatmapSelector(new matchmaking_pool(), Enumerable.Range(1, 50).Select(i => new matchmaking_pool_beatmap
                    {
                        id = (uint)i,
                        beatmap_id = i
                    }).ToArray(), new Mock<IDatabaseFactory>().Object), QueueService.Object);

                Room = room.Item;
            }

            await JoinUsers();
            await SetupForEnter();

            if (RoomState.Stage != stage)
                await MatchController.GotoStage(stage);
        }

        protected virtual async Task JoinUsers()
        {
            await Hub.JoinRoom(ROOM_ID);
            SetUserContext(ContextUser2);
            await Hub.JoinRoom(ROOM_ID);
            SetUserContext(ContextUser);
        }

        protected virtual Task SetupForEnter()
        {
            return Task.CompletedTask;
        }

        protected async Task FinishCountdown()
        {
            await Room.SkipToEndOfCountdown(Room.FindCountdownOfType<RankedPlayStageCountdown>());
        }

        public Task DisposeAsync()
        {
            return Task.CompletedTask;
        }
    }
}
