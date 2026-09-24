// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Game.Online.API;
using osu.Game.Rulesets;

namespace osu.Server.Spectator.Helpers
{
    public static class GameModeHelper
    {
        public static string GameModeToStringSpecial(RulesetInfo ruleset, APIMod[] mods)
        {
            string name = ruleset.ShortName;

            if ((name != "osu" && name != "taiko" && name != "fruits") || (!AppSettings.EnableRX && !AppSettings.EnableAP))
            {
                return ruleset.ShortName;
            }

            string[] modAcronyms = mods.Select(m => m.Acronym).ToArray();

            if (AppSettings.EnableAP && modAcronyms.Contains("AP"))
            {
                return "osuap";
            }

            if (AppSettings.EnableRX && modAcronyms.Contains("RX"))
            {
                return ruleset.ShortName switch
                {
                    "osu" => "osurx",
                    "taiko" => "taikorx",
                    "fruits" => "fruitsrx",
                    _ => ruleset.ShortName
                };
            }

            return ruleset.ShortName;
        }
    }
}
