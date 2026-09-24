// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Newtonsoft.Json;

namespace osu.Server.Spectator.Services
{
    public class RankedDodgeStatus
    {
        [JsonProperty("user_id")]
        public int UserId { get; set; }

        [JsonProperty("level")]
        public int Level { get; set; }

        [JsonProperty("expires_at")]
        public DateTimeOffset? ExpiresAt { get; set; }

        [JsonProperty("account_banned")]
        public bool AccountBanned { get; set; }
    }
}
