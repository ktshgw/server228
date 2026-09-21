// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Rulesets;

namespace osu.Server.Spectator.Extensions
{
    public static class RulesetExtensions
    {
        public static bool IsOfficial(this Ruleset ruleset)
        {
            switch (ruleset.ShortName)
            {
                case "osu":
                case "taiko":
                case "fruits":
                case "catch":
                case "mania":
                    return true;

                default:
                    return false;
            }
        }

        public static bool IsOfficial(this RulesetInfo ruleset)
        {
            switch (ruleset.ShortName)
            {
                case "osu":
                case "taiko":
                case "fruits":
                case "catch":
                case "mania":
                    return true;

                default:
                    return false;
            }
        }
    }
}
