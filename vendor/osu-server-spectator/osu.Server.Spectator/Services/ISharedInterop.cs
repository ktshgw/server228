// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using osu.Game.Online.Multiplayer;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Hubs.Referee;

namespace osu.Server.Spectator.Services
{
    public interface ISharedInterop
    {
        Task<osu.Server.Spectator.Database.Models.matchmaking_pool[]> GetMatchmakingPoolsAsync();
        Task<osu.Server.Spectator.Database.Models.database_beatmap[]> GetMatchmakingBeatmapsAsync(int rulesetId, int variant, uint poolId);
        Task<bool> IsMatchmakingPoolAvailableAsync(uint poolId);
        Task<RankedDodgeStatus> GetRankedDodgeStatusAsync(int userId);
        Task<RankedDodgeStatus> RegisterRankedDodgeAsync(long roomId, int userId);
        Task<RankedPartyReservation> ReserveRankedPartyAsync(int userId, int poolId);
        Task RenewPartyReservationAsync(string reservationId);
        Task ReleasePartyReservationAsync(string reservationId);

        /// <summary>
        /// Creates an osu!web room.
        /// </summary>
        /// <remarks>
        /// This does not join the creating user to the room. A subsequent call to <see cref="AddUserToRoomAsync"/> should be made if required.
        /// </remarks>
        /// <param name="hostUserId">The ID of the user that wants to create the room.</param>
        /// <param name="room">The room.</param>
        /// <param name="tournamentMode">Used by <see cref="RefereeHub"/> to exercise less stringent limits on number of simultaneously active rooms.</param>
        /// <returns>The room's ID.</returns>
        Task<long> CreateRoomAsync(int hostUserId, MultiplayerRoom room, bool tournamentMode = false);

        /// <summary>
        /// Adds a user to an osu!web room.
        /// </summary>
        /// <remarks>
        /// This performs setup tasks like adding the user to the relevant chat channel.
        /// </remarks>
        /// <param name="userId">The ID of the user wanting to join the room.</param>
        /// <param name="roomId">The ID of the room to join.</param>
        /// <param name="password">The room's password.</param>
        Task AddUserToRoomAsync(int userId, long roomId, string password);

        /// <summary>
        /// Parts an osu!web room.
        /// </summary>
        /// <remarks>
        /// This performs setup tasks like removing the user from any relevant chat channels.
        /// </remarks>
        /// <param name="userId">The ID of the user wanting to part the room.</param>
        /// <param name="roomId">The ID of the room to part.</param>
        Task RemoveUserFromRoomAsync(int userId, long roomId);

        /// <summary>
        /// Ensures a beatmap is present in the database by requesting the server to fetch it if missing.
        /// </summary>
        Task EnsureBeatmapPresentAsync(int beatmapId);

        /// <summary>
        /// Uploads a replay to the server.
        /// </summary>
        Task UploadReplayAsync(int scoreInfoUserID, long scoreInfoOnlineID, int scoreInfoBeatmapId, MemoryStream outStream);

        /// <summary>
        /// Retrieves the ruleset hashes from the server.
        /// </summary>
        Task<Dictionary<string, RulesetVersionEntry>> GetRulesetHashesAsync();
    }
}
