// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Threading.Tasks;
using Moq;
using osu.Game.Online.Multiplayer;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Services;
using osu.Server.Spectator.Tests.Multiplayer;
using Xunit;

namespace osu.Server.Spectator.Tests.Matchmaking
{
    public class RankedPartyQueueTests : MultiplayerTest
    {
        private readonly string reservationId = Guid.NewGuid().ToString();

        public RankedPartyQueueTests()
        {
            Database.Setup(db => db.GetMatchmakingPoolAsync(It.IsAny<uint>())).Returns<uint>(poolId => Task.FromResult<matchmaking_pool?>(new matchmaking_pool
            {
                id = poolId, type = matchmaking_pool_type.ranked_play, active = true, ranked = true,
                lobby_size = poolId == 1 ? 2 : 4, rating_search_radius = 1000, rating_search_radius_max = 1000
            }));
            LegacyIO.Setup(io => io.ReserveRankedPartyAsync(USER_ID, It.IsAny<int>())).Returns<int, int>((_, poolId) => Task.FromResult(new RankedPartyReservation
            {
                CaptainId = USER_ID, Members = poolId == 1 ? [USER_ID] : [USER_ID, USER_ID_2], ReservationId = reservationId
            }));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task DisabledRankedCannotReserveOrQueue(int poolId)
        {
            Assert.Equal("Иди в обычный лазер", (await Assert.ThrowsAsync<InvalidStateException>(() => Hub.MatchmakingJoinQueue(poolId))).Message);
            await Assert.ThrowsAsync<InvalidStateException>(() => Hub.SomsRankedQueue(poolId));
            await Assert.ThrowsAsync<InvalidStateException>(() => Hub.MatchmakingAcceptInvitation());
            LegacyIO.Verify(io => io.ReserveRankedPartyAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
            UserReceiver.Verify(client => client.MatchmakingQueueJoined(), Times.Never);
            User2Receiver.Verify(client => client.MatchmakingQueueJoined(), Times.Never);
        }

        [Fact]
        public async Task DuelsAndDirectRoomCreationCannotBypassDisabledRanked()
        {
            await Assert.ThrowsAsync<InvalidStateException>(() => Hub.MatchmakingIssueDuel(new osu.Game.Online.Matchmaking.Requests.MatchmakingIssueDuelRequest { PoolId = 1, UserId = USER_ID_2 }));
            await Assert.ThrowsAsync<InvalidStateException>(() => Hub.MatchmakingAcceptDuel(new osu.Game.Online.Matchmaking.Requests.MatchmakingAcceptDuelRequest()));
            await Assert.ThrowsAsync<InvalidStateException>(() => Hub.CreateRoom(new MultiplayerRoom(42)
            {
                Settings = new MultiplayerRoomSettings { MatchType = osu.Game.Online.Rooms.MatchType.RankedPlay }
            }));
            await Assert.ThrowsAsync<InvalidStateException>(() => Hub.ChangeSettings(new MultiplayerRoomSettings { MatchType = osu.Game.Online.Rooms.MatchType.RankedPlay }));
            Database.Setup(db => db.GetRoomAsync(42)).ReturnsAsync(new multiplayer_room { id = 42, type = database_match_type.ranked_play });
            await Assert.ThrowsAsync<InvalidStateException>(() => Hub.JoinRoomExplicit(42));
            LegacyIO.Verify(io => io.CreateRoomAsync(It.IsAny<int>(), It.IsAny<MultiplayerRoom>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public void RestartOutboxRetainsOnlyItsOwnLeasesAndRejectsConcurrentOwner()
        {
            string path = Path.Combine(Path.GetTempPath(), "soms-party-test-" + Guid.NewGuid());
            try
            {
                using (var first = new RankedPartyOutbox(path))
                {
                    first.Save(new RankedPartyReservation { CaptainId = USER_ID, Members = [USER_ID], ReservationId = reservationId }, 2);
                    Assert.Throws<IOException>(() => new RankedPartyOutbox(path));
                }
                using var restarted = new RankedPartyOutbox(path);
                Assert.Equal(reservationId, Assert.Single(restarted.Load()).Reservation.ReservationId);
                restarted.Remove(reservationId);
                Assert.Empty(restarted.Load());
            }
            finally
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
