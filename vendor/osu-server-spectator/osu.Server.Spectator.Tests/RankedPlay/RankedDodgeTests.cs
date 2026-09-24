// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Moq;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;
using osu.Server.Spectator.Database.Models;
using Xunit;

namespace osu.Server.Spectator.Tests.RankedPlay
{
    public class RankedDodgeTests : RankedPlayStageImplementationTest
    {
        protected override bool Ranked => true;

        public RankedDodgeTests() : base(RankedPlayStage.CardDiscard)
        {
        }

        [Theory]
        [InlineData(RankedPlayStage.WaitForJoin)]
        [InlineData(RankedPlayStage.RoundWarmup)]
        [InlineData(RankedPlayStage.CardDiscard)]
        public async Task EarlyExitCancelsWithoutAnyRatingWrite(RankedPlayStage stage)
        {
            if (stage == RankedPlayStage.RoundWarmup)
                RoomState.CurrentRound = 0;
            await MatchController.GotoStage(stage);
            await Hub.LeaveRoom();

            Assert.True(MatchController.CancelledByDodge);
            Assert.Equal(RankedPlayStage.Ended, RoomState.Stage);
            Assert.Null(RoomState.WinningUserId);
            Assert.Equal(UserState.Life, User2State.Life);
            Assert.Equal(UserState.Rating, UserState.RatingAfter);
            Assert.Equal(User2State.Rating, User2State.RatingAfter);
            QueueService.Verify(q => q.RegisterRankedDodgeAsync(ROOM_ID, USER_ID), Times.Once);
            Database.Verify(db => db.UpdateMatchmakingUserStatsAsync(It.IsAny<matchmaking_user_stats>()), Times.Never);

            SetUserContext(ContextUser2);
            await Hub.LeaveRoom();
            await MatchController.HandleMatchCompleted();
            QueueService.Verify(q => q.RegisterRankedDodgeAsync(It.IsAny<long>(), It.IsAny<int>()), Times.Once);
            Database.Verify(db => db.UpdateMatchmakingUserStatsAsync(It.IsAny<matchmaking_user_stats>()), Times.Never);
        }

        [Fact]
        public async Task ExitAfterOwnReplaceStillLoses()
        {
            // Confirming an unchanged hand is still confirmation.
            await Hub.DiscardCards([]);
            await Hub.LeaveRoom();
            Assert.False(MatchController.CancelledByDodge);
            Assert.Equal(0, UserState.Life);
            Assert.Equal(USER_ID_2, RoomState.WinningUserId);
            QueueService.Verify(q => q.RegisterRankedDodgeAsync(It.IsAny<long>(), It.IsAny<int>()), Times.Never);
            Database.Verify(db => db.UpdateMatchmakingUserStatsAsync(It.IsAny<matchmaking_user_stats>()), Times.Exactly(2));
        }

        [Fact]
        public async Task OpponentsReplaceDoesNotConfirmYourHand()
        {
            SetUserContext(ContextUser2);
            await Hub.DiscardCards([]);
            SetUserContext(ContextUser);
            await Hub.LeaveRoom();
            Assert.True(MatchController.CancelledByDodge);
            Assert.Null(RoomState.WinningUserId);
        }

        [Fact]
        public async Task ExpiredReplaceWindowStillLoses()
        {
            await FinishCountdown();
            await Hub.LeaveRoom();
            Assert.False(MatchController.CancelledByDodge);
            Assert.Equal(USER_ID_2, RoomState.WinningUserId);
        }

        [Fact]
        public async Task InvalidReplaceDoesNotConsumeDodge()
        {
            await Assert.ThrowsAsync<InvalidStateException>(() => Hub.DiscardCards([new RankedPlayCardItem()]));
            await Hub.LeaveRoom();
            Assert.True(MatchController.CancelledByDodge);
        }

        [Fact]
        public async Task DisconnectionBeforeReplaceAlsoCancels()
        {
            await Hub.OnDisconnectedAsync(null);
            Assert.True(MatchController.CancelledByDodge);
            Database.Verify(db => db.UpdateMatchmakingUserStatsAsync(It.IsAny<matchmaking_user_stats>()), Times.Never);
        }

        [Fact]
        public async Task PersistenceFailureCannotTurnCancellationIntoLoss()
        {
            QueueService.Setup(q => q.RegisterRankedDodgeAsync(ROOM_ID, USER_ID)).ThrowsAsync(new InvalidOperationException("App unavailable"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Hub.LeaveRoom());
            Assert.True(MatchController.CancelledByDodge);
            Assert.Equal(RankedPlayStage.Ended, RoomState.Stage);
            await MatchController.HandleMatchCompleted();
            Database.Verify(db => db.UpdateMatchmakingUserStatsAsync(It.IsAny<matchmaking_user_stats>()), Times.Never);
        }
    }

    public class DuelDodgeTests : RankedPlayStageImplementationTest
    {
        protected override bool Ranked => false;

        public DuelDodgeTests() : base(RankedPlayStage.CardDiscard)
        {
        }

        [Fact]
        public async Task DuelExitDoesNotGetDodgeOrQueuePenalty()
        {
            await Hub.LeaveRoom();
            Assert.False(MatchController.CancelledByDodge);
            QueueService.Verify(q => q.RegisterRankedDodgeAsync(It.IsAny<long>(), It.IsAny<int>()), Times.Never);
            QueueService.Verify(q => q.BanUser(It.IsAny<int>(), It.IsAny<TimeSpan>()), Times.Never);
            Database.Verify(db => db.UpdateMatchmakingUserStatsAsync(It.IsAny<matchmaking_user_stats>()), Times.Never);
        }
    }
}
