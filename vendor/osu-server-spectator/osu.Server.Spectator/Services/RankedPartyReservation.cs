// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Newtonsoft.Json;

namespace osu.Server.Spectator.Services
{
    public class RankedPartyReservation
    {
        [JsonProperty("id")]
        public int? Id { get; set; }
        [JsonProperty("captain_id")]
        public int? CaptainId { get; set; }
        [JsonProperty("members")]
        public int[] Members { get; set; } = [];
        [JsonProperty("reservation_id")]
        public string ReservationId { get; set; } = string.Empty;
    }
}
