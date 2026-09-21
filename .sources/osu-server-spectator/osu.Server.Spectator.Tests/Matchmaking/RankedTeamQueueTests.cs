// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Microsoft.Extensions.Internal;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Elo;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Queue;
using Xunit;

namespace osu.Server.Spectator.Tests.Matchmaking
{
    public class RankedTeamQueueTests
    {
        private readonly MatchmakingQueue queue = new MatchmakingQueue(new matchmaking_pool
        {
            type = matchmaking_pool_type.ranked_play, lobby_size = 4, rating_search_radius = 1000, rating_search_radius_max = 1000
        });

        private static MatchmakingQueueUser user(int id, double rating = 1500, string? party = null) => new MatchmakingQueueUser(id.ToString())
        {
            UserId = id, Rating = new EloRating(rating), PartyId = party
        };

        [Fact]
        public void FourSoloPlayersAreBalancedIntoTwoTeams()
        {
            queue.AddRange([user(1, 1000), user(2, 1300), user(3, 1700), user(4, 2000)]);
            MatchmakingQueueUser[] matched = Assert.Single(queue.Update().FormedGroups).Users;
            Assert.Equal(matched[0].Rating.Mu + matched[2].Rating.Mu, matched[1].Rating.Mu + matched[3].Rating.Mu);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PartyIsNeverSplit(bool secondParty)
        {
            queue.AddRange([user(1, 1000, "a"), user(2, 1100, "a"), user(3, 1200, secondParty ? "b" : null), user(4, 1300, secondParty ? "b" : null)]);
            MatchmakingQueueUser[] matched = Assert.Single(queue.Update().FormedGroups).Users;
            int first = Array.FindIndex(matched, entry => entry.UserId == 1);
            int second = Array.FindIndex(matched, entry => entry.UserId == 2);
            Assert.Equal(first % 2, second % 2);
        }

        [Fact]
        public void IncompletePartyCannotEnterMatch()
        {
            queue.AddRange([user(1, party: "a"), user(2), user(3), user(4)]);
            Assert.Empty(queue.Update().FormedGroups);
        }

        [Fact]
        public void PartyMemberLeavingRemovesBothAndOnlyPenalisesDecliner()
        {
            queue.AddRange([user(1, party: "a"), user(2, party: "a"), user(3), user(4)]);
            Assert.Single(queue.Update().FormedGroups);
            var removed = queue.MarkInvitationDeclined(user(2));
            Assert.Equal(new[] { 1, 2 }, removed.RemovedUsers.Select(entry => entry.UserId).Order());
            Assert.Equal(2, Assert.Single(removed.DeclinedUsers).UserId);
            Assert.Equal(new[] { 3, 4 }, removed.AddedUsers.Select(entry => entry.UserId).Order());
        }

        [Fact]
        public void TimedOutPartyMemberDoesNotStrandAcceptedTeammate()
        {
            var clock = new TestClock();
            queue.Clock = clock;
            queue.InviteTimeout = TimeSpan.FromSeconds(1);
            queue.AddRange([user(1, party: "a"), user(2, party: "a"), user(3), user(4)]);
            queue.Update();
            queue.MarkInvitationAccepted(user(1));
            queue.MarkInvitationAccepted(user(3));
            queue.MarkInvitationAccepted(user(4));
            clock.UtcNow += TimeSpan.FromSeconds(2);
            var update = queue.Update();
            Assert.Equal(new[] { 1, 2 }, update.RemovedUsers.Select(entry => entry.UserId).Order());
            Assert.Equal(2, Assert.Single(update.DeclinedUsers).UserId);
            Assert.Equal(new[] { 3, 4 }, queue.GetAllUsers().Select(entry => entry.UserId).Order());
        }

        private class TestClock : ISystemClock
        {
            public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        }
    }
}
