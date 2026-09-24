// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using osu.Game.Online.Multiplayer;
using osu.Server.Spectator.Extensions;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.RankedPlay;

namespace osu.Server.Spectator.Hubs.Multiplayer
{
    public partial class MultiplayerHub
    {
        public Task SomsRankedQueue(int poolId)
            => throw new InvalidStateException("Иди в обычный лазер");

        // JSON strings keep the extension independent of osu!'s fixed MessagePack
        // union and allow 1v1 clients to continue using the existing protocol.
        public async Task<string> SomsRankedTeamState(long roomId)
        {
            using var user = await GetOrCreateLocalUserState();
            using var room = await getLocalUserRoom(user.Item!);
            if (room.Item?.RoomID != roomId || room.Item.MatchController is not RankedPlayMatchController controller)
                throw new InvalidStateException("Not in this Ranked room.");
            return JsonConvert.SerializeObject(new
            {
                room_id = roomId,
                team_size = controller.IsTeamMatch ? 2 : 1,
                max_life = controller.MaximumLife,
                teams = controller.TeamByUser.GroupBy(entry => entry.Value).OrderBy(group => group.Key).Select(group => new
                {
                    id = group.Key,
                    user_ids = group.Select(entry => entry.Key).ToArray(),
                    life = controller.State.Users[group.First().Key].Life
                }).ToArray(),
                turn_order = controller.UserIdsByTurnOrder,
                winning_team_id = controller.WinningTeamId,
                cancelled = controller.CancelledByDodge
            });
        }
    }
}
