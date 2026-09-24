// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace osu.Server.Spectator.Entities
{
    // Hash format shared with the client ruleset metadata generator.
    public class RulesetVersionEntry
    {
        [JsonPropertyName("latest-version")]
        public string LatestVersion { get; set; } = "";

        [JsonPropertyName("versions")]
        public Dictionary<string, string> Versions { get; set; } = new Dictionary<string, string>();
    }
}
