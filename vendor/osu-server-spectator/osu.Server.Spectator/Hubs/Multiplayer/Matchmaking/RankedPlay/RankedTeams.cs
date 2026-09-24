// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Game.Online.Multiplayer;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.Queue;

namespace osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.RankedPlay
{
    public static class RankedTeams
    {
        /// <summary>Balance four players without separating a pre-made party; return A1,B1,A2,B2.</summary>
        public static MatchmakingQueueUser[] Arrange(MatchmakingQueueUser[] users)
            => TryArrange(users) ?? throw new InvalidStateException("A Ranked party must have exactly two players on the same team.");

        public static MatchmakingQueueUser[]? TryArrange(MatchmakingQueueUser[] users, Func<int, int, bool>? canOppose = null)
        {
            if (users.Length != 4 || users.Select(u => u.UserId).Distinct().Count() != 4)
                throw new InvalidStateException("A Ranked team match requires four different players.");

            MatchmakingQueueUser[] sorted = users.OrderBy(u => u.Rating.Mu).ThenBy(u => u.UserId).ToArray();
            MatchmakingQueueUser[][]? best = null;
            double difference = double.MaxValue;
            for (int i = 1; i < sorted.Length; i++)
            {
                MatchmakingQueueUser[] teamA = [sorted[0], sorted[i]];
                MatchmakingQueueUser[] teamB = sorted.Except(teamA).ToArray();
                if (users.Where(u => u.PartyId != null).GroupBy(u => u.PartyId).Any(p =>
                        p.Count() != 2 || (p.Any(teamA.Contains) && p.Any(teamB.Contains))))
                    continue;
                if (canOppose != null && teamA.Any(a => teamB.Any(b => !canOppose(a.UserId, b.UserId))))
                    continue;
                double candidateDifference = Math.Abs(teamA.Sum(u => u.Rating.Mu) - teamB.Sum(u => u.Rating.Mu));
                if (candidateDifference < difference)
                {
                    difference = candidateDifference;
                    best = [teamA, teamB];
                }
            }
            if (best == null)
                return null;
            return [best[0][0], best[1][0], best[0][1], best[1][1]];
        }
    }
}
