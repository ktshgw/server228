// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using osu.Game.Online.Multiplayer;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Services;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class RulesetManagerTests
    {
        [Fact]
        public void OfficialRulesetsCanBeResolvedByIdAndName()
        {
            var manager = createManager(new Mock<ISharedInterop>());

            Assert.Equal("osu", manager.GetRuleset(0).ShortName);
            Assert.Equal("fruits", manager.GetRuleset("catch").ShortName);
            Assert.Throws<InvalidStateException>(() => manager.GetRuleset(99));
            Assert.Throws<ArgumentException>(() => manager.GetRuleset("missing"));
        }

        [Fact]
        public async Task CustomRulesetHashesAreValidatedAndCached()
        {
            var sharedInterop = new Mock<ISharedInterop>();
            sharedInterop.Setup(i => i.GetRulesetHashesAsync()).ReturnsAsync(new Dictionary<string, RulesetVersionEntry>
            {
                ["custom"] = new RulesetVersionEntry
                {
                    LatestVersion = "2.0",
                    Versions = { ["2.0"] = "current-hash" }
                }
            });
            var manager = createManager(sharedInterop);

            await manager.InitializeHashes();

            Assert.Equal(string.Empty, await manager.ValidateRulesetHash("osu", "ignored"));
            Assert.Equal(string.Empty, await manager.ValidateRulesetHash("custom", "current-hash"));
            Assert.Equal("2.0", await manager.ValidateRulesetHash("custom", "old-hash"));
            Assert.Equal("server-not-supported", await manager.ValidateRulesetHash("missing", "hash"));
            sharedInterop.Verify(i => i.GetRulesetHashesAsync(), Times.Once);
        }

        private static RulesetManager createManager(Mock<ISharedInterop> sharedInterop)
            => new RulesetManager(
                new Mock<ILogger<RulesetManager>>().Object,
                new MemoryCache(new MemoryCacheOptions()),
                sharedInterop.Object);
    }
}
