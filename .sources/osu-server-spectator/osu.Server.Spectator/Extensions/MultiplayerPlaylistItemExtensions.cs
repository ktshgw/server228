// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Rulesets.Mods;
using osu.Game.Utils;
using osu.Server.Spectator.Services;

namespace osu.Server.Spectator.Extensions
{
    public static class MultiplayerPlaylistItemExtensions
    {
        /// <summary>
        /// Checks whether the given mods are compatible with the current playlist item's mods and ruleset.
        /// </summary>
        /// <param name="item">The <see cref="MultiplayerPlaylistItem"/> to validate the user mods against.</param>
        /// <param name="user">The <see cref="MultiplayerRoomUser"/> to validate the mods of.</param>
        /// <param name="proposedMods">The proposed user mods to check against the <see cref="MultiplayerPlaylistItem"/>.</param>
        /// <param name="validMods">The set of mods which _are_ valid.</param>
        /// <param name="manager">The ruleset manager.</param>
        /// <returns>Whether all user mods are valid for the <see cref="MultiplayerPlaylistItem"/>.</returns>
        public static bool ValidateUserMods(this MultiplayerPlaylistItem item, MultiplayerRoomUser user, IEnumerable<APIMod> proposedMods, [NotNullWhen(false)] out IEnumerable<APIMod>? validMods,
                                            RulesetManager manager, bool allowPersonalRates = true)
        {
            var ruleset = manager.GetRuleset(user.RulesetId ?? item.RulesetID);

            bool proposedWereValid = true;
            proposedWereValid &= ModUtils.InstantiateValidModsForRuleset(ruleset, proposedMods, out var valid);

            // Freestyle unconditionally allows all freemods.
            if (!item.Freestyle)
            {
                // check allowed by room
                foreach (var mod in valid.ToList())
                {
                    if (allowPersonalRates && item.AllowedMods.Any() && mod is ModDoubleTime or ModNightcore)
                        continue;

                    if (item.AllowedMods.All(m => !string.Equals(m.Acronym, mod.Acronym, StringComparison.OrdinalIgnoreCase)))
                    {
                        valid.Remove(mod);
                        proposedWereValid = false;
                    }
                }
            }

            // check valid as combination
            if (!ModUtils.CheckCompatibleSet(item.RequiredMods.Select(m => m.ToMod(ruleset)).Concat(valid), out var invalid))
            {
                proposedWereValid = false;
                foreach (var mod in invalid)
                    valid.Remove(mod);
            }

            validMods = valid.Select(m => new APIMod(m));

            return proposedWereValid;
        }

        /// <summary>
        /// Ensures that a <see cref="MultiplayerPlaylistItem"/>'s required and allowed mods are compatible with each other and the room's ruleset.
        /// </summary>
        /// <param name="item">The playlist item to validate.</param>
        /// <param name="manager">The ruleset manager.</param>
        /// <exception cref="InvalidStateException">If the mods are invalid.</exception>
        public static void EnsureModsValid(this MultiplayerPlaylistItem item, RulesetManager manager)
        {
            var ruleset = manager.GetRuleset(item.RulesetID);

            // defensively change all mod acronyms to uppercase.
            // while lazer client will consistently use uppercase, referee API clients are largely uncontrolled and may use any casing.
            // aside from cosmetic reasons (the acronyms are later used in this method inside error messages, and they look nicer uppercase),
            // this is also applying Postel's law - all other clients and readers will henceforth see acronyms normalised to uppercase.
            foreach (var requiredMod in item.RequiredMods)
                requiredMod.Acronym = requiredMod.Acronym.ToUpperInvariant();
            foreach (var allowedMod in item.AllowedMods)
                allowedMod.Acronym = allowedMod.Acronym.ToUpperInvariant();

            // check against ruleset
            if (!ModUtils.InstantiateValidModsForRuleset(ruleset, item.RequiredMods, out var requiredMods))
            {
                var invalidRequiredAcronyms = string.Join(',',
                    item.RequiredMods
                        .Where(m => requiredMods.All(valid => !string.Equals(valid.Acronym, m.Acronym, StringComparison.OrdinalIgnoreCase)))
                        .Select(m => m.Acronym));
                throw new InvalidStateException($"Invalid mods were selected for specified ruleset: {invalidRequiredAcronyms}");
            }

            if (!ModUtils.InstantiateValidModsForRuleset(ruleset, item.AllowedMods, out var allowedMods))
            {
                var invalidAllowedAcronyms = string.Join(',',
                    item.AllowedMods
                        .Where(m => allowedMods.All(valid => !string.Equals(valid.Acronym, m.Acronym, StringComparison.OrdinalIgnoreCase)))
                        .Select(m => m.Acronym));
                throw new InvalidStateException($"Invalid mods were selected for specified ruleset: {invalidAllowedAcronyms}");
            }

            if (!ModUtils.CheckCompatibleSet(requiredMods, out var invalid))
                throw new InvalidStateException($"Invalid combination of required mods: {string.Join(',', invalid.Select(m => m.Acronym))}");

            if (!ModUtils.CheckValidRequiredModsForMultiplayer(requiredMods, item.Freestyle, out invalid))
                throw new InvalidStateException($"Invalid required mods were selected: {string.Join(',', invalid.Select(m => m.Acronym))}");

            // SOMS! players may run their own DT/NC clock. Required speed mods
            // still participate in the compatibility checks below.
            if (!ModUtils.CheckValidAllowedModsForMultiplayer(allowedMods.Where(m => m is not ModDoubleTime && m is not ModNightcore), item.Freestyle, out invalid))
                throw new InvalidStateException($"Invalid free mods were selected: {string.Join(',', invalid.Select(m => m.Acronym))}");

            // check aggregate combinations with each allowed mod individually.
            foreach (var allowedMod in allowedMods)
            {
                if (!ModUtils.CheckCompatibleSet(requiredMods.Concat(new[] { allowedMod }), out invalid))
                    throw new InvalidStateException($"Invalid combination of required and allowed mods: {string.Join(',', invalid.Select(m => m.Acronym))}");
            }
        }

        public static async Task<Tuple<string, string>?> ValidateRuleset(this MultiplayerPlaylistItem item, RulesetManager manager, Dictionary<string, string> clientHashes)
        {
            if (!AppSettings.CheckRulesetVersion)
                return null;

            var ruleset = manager.GetRuleset(item.RulesetID);
            var clientHash = clientHashes.GetValueOrDefault(ruleset.ShortName);

            if (ruleset.IsOfficial())
                return null;

            var latestVersion = await manager.ValidateRulesetHash(ruleset, clientHash ?? string.Empty);

            if (!string.IsNullOrEmpty(latestVersion))
                return Tuple.Create(ruleset.ShortName, latestVersion);

            return null;
        }

        public static async Task<List<Tuple<string, string>>> ValidateRulesets(this IEnumerable<MultiplayerPlaylistItem> items, RulesetManager manager, Dictionary<string, string> clientHashes)
        {
            List<Tuple<string, string>> invalidRulesets = new List<Tuple<string, string>>();

            if (!AppSettings.CheckRulesetVersion)
                return invalidRulesets;

            foreach (var item in items)
            {
                var ruleset = manager.GetRuleset(item.RulesetID);

                if (ruleset.IsOfficial())
                    continue;

                var clientHash = clientHashes.GetValueOrDefault(ruleset.ShortName);
                var latestVersion = await manager.ValidateRulesetHash(ruleset, clientHash ?? string.Empty);

                if (!string.IsNullOrEmpty(latestVersion))
                    invalidRulesets.Add(Tuple.Create(ruleset.ShortName, latestVersion));
            }

            return invalidRulesets;
        }
    }
}
