// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;
using osu.Server.Spectator.Database.Models;

namespace osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.RankedPlay.Stages
{
    public class ResultsStage : RankedPlayStageImplementation
    {
        /// <summary>
        /// Amount of time to wait for scores to arrive in the database before continuing.
        /// </summary>
        public TimeSpan ScoreRetrievalWaitTime { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Base amount of damage taken per round.
        /// </summary>
        public int BaseDamage { get; set; } = 50_000;

        public ResultsStage(RankedPlayMatchController controller)
            : base(controller)
        {
        }

        protected override RankedPlayStage Stage => RankedPlayStage.Results;
        protected override TimeSpan Duration => scoresProcessed ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(1);

        private int? winningUserId;
        private bool scoresProcessed;
        private DateTimeOffset retrievalDeadline;

        protected override async Task Begin()
        {
            scoresProcessed = false;
            winningUserId = null;
            foreach (var user in State.Users.Values)
                user.DamageInfo = null;
            retrievalDeadline = DateTimeOffset.UtcNow + ScoreRetrievalWaitTime;
            await tryProcessScores();
        }

        private async Task tryProcessScores()
        {
            List<SoloScore> scores;
            using (var db = DbFactory.GetInstance())
            {
                scores = (await db.GetAllScoresForPlaylistItem(Room.RoomID, Room.Settings.PlaylistItemId))
                    .Where(s => State.Users.ContainsKey((int)s.user_id))
                    .GroupBy(s => s.user_id)
                    .Select(group => group.MaxBy(s => s.total_score)!)
                    .ToList();
            }

            // Countdown callbacks acquire the room lock only for this short query.
            // Waiting inside Begin used to hold it for ten seconds, timing out
            // clients' ChangeState/Leave calls while a score was still arriving.
            if (DateTimeOffset.UtcNow < retrievalDeadline
                && State.Users.Any(u => u.Value.Life > 0 && scores.All(s => s.user_id != u.Key)))
                return;

            scoresProcessed = true;
            foreach ((int userId, RankedPlayUserInfo info) in State.Users)
            {
                // Add dummy scores for all users that did not play the map.
                if (scores.All(s => s.user_id != userId))
                    scores.Add(new SoloScore { user_id = (uint)userId });

                // Populate the models with a default damage info.
                info.DamageInfo = Controller.Damage(userId);
            }

            if (Controller.IsTeamMatch)
            {
                processTeamScores(scores);
            }
            else
            {
                int winningTotalScore = (int)scores.Select(s => s.total_score).Max();
                SoloScore[] winningScores = scores.Where(u => u.total_score == winningTotalScore).ToArray();
                winningUserId = winningScores.Length == 1 ? (int)winningScores.Single().user_id : null;

                if (winningUserId != null)
                {
                    // Winner: losing player takes damage.
                    SoloScore losingScore = scores.Single(u => u.user_id != winningUserId);

                    int attackDamage = winningTotalScore - (int)losingScore.total_score;
                    double attackMultiplier = State.DamageMultiplier + State.Users[winningUserId.Value].DamageMultiplier;

                    State.Users[(int)losingScore.user_id].DamageInfo = Controller.Damage((int)losingScore.user_id, attackDamage, attackMultiplier, BaseDamage);
                    incrementRoundsWonIfPresent(State.Users[(int)winningUserId]);
                }
            }

            if (Controller.Ranked && scores.All(s => Controller.RatingByUser.ContainsKey((int)s.user_id)))
            {
                await Controller.MatchmakingService.RecordBeatmapResult(
                    Controller.Pool.id,
                    Room.CurrentPlaylistItem.BeatmapID,
                    Room.CurrentPlaylistItem.RequiredMods.ToArray(),
                    scores.Select(s => (int)s.total_score).ToArray(),
                    scores.Select(s => Controller.RatingByUser[(int)s.user_id]).ToArray());
            }

            if (!HasGameplayRoundsRemaining())
                await Controller.HandleMatchCompleted();
        }

        protected override async Task Finish()
        {
            if (!scoresProcessed)
            {
                await tryProcessScores();
                await EventDispatcher.PostMatchRoomStateChangedAsync(Room);
                await FinishWithCountdown(Duration);
                return;
            }

            // Award the winning player with their own multiplier boost.
            if (winningUserId != null)
            {
                foreach (int member in Controller.TeamMembers(winningUserId.Value))
                    State.Users[member].DamageMultiplier += 0.5;
            }

            foreach ((_, RankedPlayUserInfo userInfo) in State.Users)
                userInfo.DamageInfo = null;

            if (HasGameplayRoundsRemaining())
                await Controller.GotoStage(RankedPlayStage.RoundWarmup);
            else
                await Controller.GotoStage(RankedPlayStage.Ended);
        }

        public override async Task HandleUserLeft(MultiplayerRoomUser user)
        {
            // Allow players to leave early without incurring a loss if they know gameplay won't continue.
            if (HasGameplayRoundsRemaining())
                await KillUser(user);

            // Remain in the results stage, which will naturally transition to the ended stage once the countdown expires.
        }

        private static void incrementRoundsWonIfPresent(RankedPlayUserInfo userInfo)
        {
            var roundsWonProperty = userInfo.GetType().GetProperty("RoundsWon");

            if (roundsWonProperty?.PropertyType != typeof(int) || !roundsWonProperty.CanRead || !roundsWonProperty.CanWrite)
                return;

            int currentValue = (int)(roundsWonProperty.GetValue(userInfo) ?? 0);
            roundsWonProperty.SetValue(userInfo, currentValue + 1);
        }

        private void processTeamScores(List<SoloScore> scores)
        {
            var totals = scores.GroupBy(score => Controller.TeamByUser[(int)score.user_id])
                .ToDictionary(group => group.Key, group => group.Sum(score => (long)score.total_score));
            if (totals[0] == totals[1])
                return;
            int winningTeam = totals[0] > totals[1] ? 0 : 1;
            int[] winners = Controller.TeamByUser.Where(entry => entry.Value == winningTeam).Select(entry => entry.Key).ToArray();
            int[] losers = Controller.TeamByUser.Where(entry => entry.Value != winningTeam).Select(entry => entry.Key).ToArray();
            winningUserId = winners[0];
            int damage = (int)Math.Min(int.MaxValue, Math.Abs(totals[0] - totals[1]));
            double multiplier = State.DamageMultiplier + winners.Average(userId => State.Users[userId].DamageMultiplier);
            RankedPlayDamageInfo damageInfo = Controller.Damage(losers[0], damage, multiplier, BaseDamage);
            foreach (int userId in losers)
                State.Users[userId].DamageInfo = damageInfo;
            foreach (int userId in winners)
                incrementRoundsWonIfPresent(State.Users[userId]);
        }
    }
}
