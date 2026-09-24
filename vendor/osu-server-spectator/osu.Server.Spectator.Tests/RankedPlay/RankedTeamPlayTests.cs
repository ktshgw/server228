// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Newtonsoft.Json.Linq;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;
using osu.Server.Spectator.Database.Models;
using Xunit;

namespace osu.Server.Spectator.Tests.RankedPlay
{
    public class RankedTeamPlayTests : RankedPlayStageImplementationTest
    {
        protected override int[] MatchUserIds => [USER_ID, USER_ID_2, 3456, 4567];
        protected override uint PoolId => 17;
        private readonly Mock<HubCallerContext> third;
        private readonly Mock<HubCallerContext> fourth;

        public RankedTeamPlayTests() : base(RankedPlayStage.CardDiscard)
        {
            CreateUser(3456, out third, out _);
            CreateUser(4567, out fourth, out _);
        }

        protected override async Task JoinUsers()
        {
            await base.JoinUsers();
            SetUserContext(third);
            await Hub.JoinRoom(ROOM_ID);
            SetUserContext(fourth);
            await Hub.JoinRoom(ROOM_ID);
            SetUserContext(ContextUser);
        }

        [Fact]
        public async Task FourPrivateHandsAndTwoSharedHealthBars()
        {
            Assert.True(MatchController.IsTeamMatch);
            Assert.All(RoomState.Users.Values, user => Assert.Equal(2_000_000, user.Life));
            Assert.All(RoomState.Users.Values, user => Assert.Equal(5, user.Hand.Count));
            Assert.Equal(20, RoomState.Users.Values.SelectMany(user => user.Hand).Select(card => card.ID).Distinct().Count());
            JObject state = JObject.Parse(await Hub.SomsRankedTeamState(ROOM_ID));
            Assert.Equal(2, state["team_size"]!.Value<int>());
            Assert.Equal(2_000_000, state["max_life"]!.Value<int>());
            Assert.Equal(2, state["teams"]!.Count());
        }

        [Fact]
        public async Task PicksAlternateTeamsAndVisitAllFourPlayers()
        {
            Assert.Equal(new[] { 0, 1, 0, 1 }, MatchController.UserIdsByTurnOrder.Select(id => MatchController.TeamByUser[id]));
            var seen = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                seen.Add(RoomState.ActiveUserId!.Value);
                await MatchController.GotoStage(RankedPlayStage.RoundWarmup);
            }
            Assert.Equal(MatchController.UserIdsByTurnOrder, seen);
            Assert.Equal(seen[0], RoomState.ActiveUserId);
            Assert.Equal(6, RoomState.Users[seen[0]].Hand.Count);
            Assert.All(seen.Skip(1), id => Assert.Equal(5, RoomState.Users[id].Hand.Count));
        }

        [Fact]
        public async Task TeamScoreSumWinsEvenAgainstHighestIndividualScore()
        {
            int[] losing = MatchController.TeamMembers(USER_ID);
            int[] winning = MatchUserIds.Except(losing).ToArray();
            Database.Setup(db => db.GetAllScoresForPlaylistItem(It.IsAny<long>(), It.IsAny<long>())).ReturnsAsync(new[]
            {
                new SoloScore { user_id = (uint)losing[0], total_score = 1_000_000 },
                new SoloScore { user_id = (uint)losing[1], total_score = 100_000 },
                new SoloScore { user_id = (uint)winning[0], total_score = 600_000 },
                new SoloScore { user_id = (uint)winning[1], total_score = 600_000 }
            });
            await MatchController.GotoStage(RankedPlayStage.Results);
            Assert.All(losing, id => Assert.Equal(1_850_000, RoomState.Users[id].Life));
            Assert.All(winning, id => Assert.Equal(2_000_000, RoomState.Users[id].Life));
            Assert.Same(RoomState.Users[losing[0]].DamageInfo, RoomState.Users[losing[1]].DamageInfo);
            await FinishCountdown();
            Assert.All(winning, id => Assert.Equal(1, RoomState.Users[id].DamageMultiplier));
            Assert.All(losing, id => Assert.Equal(0.5, RoomState.Users[id].DamageMultiplier));
        }

        [Fact]
        public async Task ResultsWaitForFourthPlayerBeforeCalculatingTeamDamage()
        {
            SoloScore[] scores = MatchUserIds.Select(id => new SoloScore { user_id = (uint)id, total_score = 600_000 }).ToArray();
            Database.Setup(db => db.GetAllScoresForPlaylistItem(It.IsAny<long>(), It.IsAny<long>())).ReturnsAsync(scores.Take(3).ToArray());
            await MatchController.GotoStage(RankedPlayStage.Results);
            Assert.All(RoomState.Users.Values, user => Assert.Null(user.DamageInfo));
            Assert.All(RoomState.Users.Values, user => Assert.Equal(2_000_000, user.Life));
            Database.Setup(db => db.GetAllScoresForPlaylistItem(It.IsAny<long>(), It.IsAny<long>())).ReturnsAsync(scores);
            await FinishCountdown();
            Assert.All(RoomState.Users.Values, user => Assert.NotNull(user.DamageInfo));
            Assert.All(RoomState.Users.Values, user => Assert.Equal(2_000_000, user.Life));
        }

        [Fact]
        public async Task ConfirmedExitForfeitsTeamAndUpdatesAllFourRatingsInTeamPoolOnly()
        {
            int[] losing = MatchController.TeamMembers(USER_ID);
            await Hub.DiscardCards([]);
            await Hub.LeaveRoom();
            Assert.False(MatchController.CancelledByDodge);
            Assert.All(losing, id => Assert.Equal(0, RoomState.Users[id].Life));
            Assert.All(losing, id => Assert.True(RoomState.Users[id].RatingAfter < RoomState.Users[id].Rating));
            Assert.All(MatchUserIds.Except(losing), id => Assert.True(RoomState.Users[id].RatingAfter > RoomState.Users[id].Rating));
            Database.Verify(db => db.UpdateMatchmakingUserStatsAsync(It.Is<matchmaking_user_stats>(stats => stats.pool_id == 17)), Times.Exactly(4));
            QueueService.Verify(service => service.RegisterRankedDodgeAsync(It.IsAny<long>(), It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public async Task EarlyDodgeCancelsAllFourWithoutRatingChanges()
        {
            await Hub.LeaveRoom();
            Assert.True(MatchController.CancelledByDodge);
            Assert.Null(RoomState.WinningUserId);
            Assert.Null(MatchController.WinningTeamId);
            Assert.All(RoomState.Users.Values, user => Assert.Equal(user.Rating, user.RatingAfter));
            Database.Verify(db => db.UpdateMatchmakingUserStatsAsync(It.IsAny<matchmaking_user_stats>()), Times.Never);
            QueueService.Verify(service => service.RegisterRankedDodgeAsync(ROOM_ID, USER_ID), Times.Once);
        }
    }
}
