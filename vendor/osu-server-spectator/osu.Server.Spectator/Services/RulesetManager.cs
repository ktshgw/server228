// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using osu.Game.Online.Multiplayer;
using Microsoft.Extensions.Logging;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;
using osu.Server.Spectator.Entities;

namespace osu.Server.Spectator.Services
{
    public class RulesetManager
    {
        private readonly ILogger<RulesetManager> _logger;
        private readonly IMemoryCache cache;
        private readonly ISharedInterop sharedInterop;

        private const string ruleset_library_prefix = "osu.Game.Rulesets";
        private const string cache_key = "ruleset-hashes";

        private readonly Dictionary<string, Ruleset> _rulesets = new Dictionary<string, Ruleset>();
        private readonly Dictionary<int, Ruleset> _rulesetsById = new Dictionary<int, Ruleset>();

        public RulesetManager(ILogger<RulesetManager> logger, IMemoryCache cache, ISharedInterop sharedInterop)
        {
            _logger = logger;
            this.cache = cache;
            this.sharedInterop = sharedInterop;

            loadOfficialRulesets();
            loadFromDisk();
        }

        private void addRuleset(Ruleset ruleset)
        {
            if (!_rulesets.TryAdd(ruleset.ShortName, ruleset))
            {
                _logger.LogWarning("Ruleset with short name {shortName} already exists, skipping.", ruleset.ShortName);
                return;
            }

            if (ruleset is not ILegacyRuleset legacyRuleset)
            {
                return;
            }

            if (!_rulesetsById.TryAdd(legacyRuleset.LegacyID, ruleset))
            {
                _logger.LogWarning("Ruleset with ID {id} already exists, skipping.", legacyRuleset.LegacyID);
            }
        }

        private void loadOfficialRulesets()
        {
            foreach (Ruleset ruleset in (List<Ruleset>)
                     [new OsuRuleset(), new TaikoRuleset(), new CatchRuleset(), new ManiaRuleset()])
            {
                addRuleset(ruleset);
            }

            _rulesets["catch"] = _rulesets["fruits"];
        }

        private void loadFromDisk()
        {
            if (!Directory.Exists(AppSettings.RulesetsPath))
            {
                return;
            }

            string[] rulesets = Directory.GetFiles(AppSettings.RulesetsPath, $"{ruleset_library_prefix}.*.dll");

            foreach (string ruleset in rulesets.Where(f => !f.Contains(@"Tests")))
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(ruleset);
                    Type? rulesetType = assembly.GetTypes()
                                                .FirstOrDefault(t => t.IsSubclassOf(typeof(Ruleset)) && !t.IsAbstract);

                    if (rulesetType == null)
                    {
                        continue;
                    }

                    Ruleset instance = (Ruleset)Activator.CreateInstance(rulesetType)!;
                    _logger.LogInformation("Loading ruleset {ruleset}", ruleset);
                    addRuleset(instance);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to load ruleset from {ruleset}: {ex}", ruleset, ex);
                }
            }
        }

        public Ruleset GetRuleset(int rulesetId)
        {
            return _rulesetsById.TryGetValue(rulesetId, out Ruleset? ruleset)
                ? ruleset
                : throw new InvalidStateException("Invalid ruleset ID provided.");
        }

        public Ruleset GetRuleset(string shortName)
        {
            return _rulesets.TryGetValue(shortName, out Ruleset? ruleset)
                ? ruleset
                : throw new ArgumentException("Invalid ruleset name provided.");
        }

        public async Task InitializeHashes()
        {
            await cache.GetOrCreateAsync<Dictionary<string, RulesetVersionEntry>>(cache_key, e => sharedInterop.GetRulesetHashesAsync());
        }

        public async Task<string> ValidateRulesetHash(string rulesetName, string clientHash)
        {
            if (rulesetName == "osu" || rulesetName == "taiko" || rulesetName == "fruits" || rulesetName == "mania")
                return string.Empty;

            var rulesetHashes = await cache.GetOrCreateAsync<Dictionary<string, RulesetVersionEntry>>(cache_key, e => sharedInterop.GetRulesetHashesAsync());

            if (!rulesetHashes!.TryGetValue(rulesetName, out RulesetVersionEntry? entry))
            {
                _logger.LogWarning("No hash entry found for ruleset {ruleset}", rulesetName);
                return "server-not-supported";
            }

            string currentHash = entry.Versions[entry.LatestVersion];

            if (currentHash == clientHash)
            {
                return string.Empty;
            }

            _logger.LogWarning("Hash mismatch for ruleset {ruleset}: server hash {currentHash}, client hash {clientHash}", rulesetName, currentHash, clientHash);
            return entry.LatestVersion;
        }

        public Task<string> ValidateRulesetHash(Ruleset ruleset, string clientHash)
        {
            return ValidateRulesetHash(ruleset.ShortName, clientHash);
        }

        public Task<string> ValidateRulesetHash(int rulesetId, string clientHash)
        {
            return ValidateRulesetHash(GetRuleset(rulesetId), clientHash);
        }
    }
}
