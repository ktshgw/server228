// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Game.Online.API;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Taiko;
using osu.Server.Spectator.Helpers;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class HelperTests : IDisposable
    {
        private bool enableAp;
        private bool enableRx;

        public HelperTests()
        {
            enableAp = AppSettings.EnableAP;
            enableRx = AppSettings.EnableRX;
        }

        public void Dispose()
        {
            AppSettings.EnableAP = enableAp;
            AppSettings.EnableRX = enableRx;
        }

        [Fact]
        public void TestBlobHelperRoundTrip()
        {
            int[] values = [0, 1, -1, int.MinValue, int.MaxValue];

            Assert.Equal(values, BlobHelper.ParseBlobToIntArray(BlobHelper.IntArrayToBlob(values)));
        }

        [Fact]
        public void TestTimeHelperRoundTrip()
        {
            var time = new DateTimeOffset(2026, 8, 7, 12, 34, 56, TimeSpan.Zero);

            Assert.Equal(time, TimeHelper.ToDateTimeOffset(TimeHelper.ToMappedInt(time)));
        }

        [Fact]
        public void TestTimeHelperRejectsOutOfRangeTimestamp()
        {
            Assert.Throws<OverflowException>(() => TimeHelper.ToMappedInt(long.MaxValue));
        }

        [Theory]
        [InlineData("osu", "osurx")]
        [InlineData("taiko", "taikorx")]
        [InlineData("fruits", "fruitsrx")]
        public void TestGameModeHelperWithRelax(string rulesetName, string expected)
        {
            AppSettings.EnableRX = true;

            var ruleset = rulesetName switch
            {
                "taiko" => new TaikoRuleset().RulesetInfo,
                "fruits" => new CatchRuleset().RulesetInfo,
                _ => new OsuRuleset().RulesetInfo
            };

            Assert.Equal(expected, GameModeHelper.GameModeToStringSpecial(ruleset, [new APIMod { Acronym = "RX" }]));
        }

        [Fact]
        public void TestGameModeHelperWithAutopilot()
        {
            AppSettings.EnableAP = true;

            Assert.Equal("osuap", GameModeHelper.GameModeToStringSpecial(new OsuRuleset().RulesetInfo, [new APIMod { Acronym = "AP" }]));
        }

        [Fact]
        public void TestGameModeHelperIgnoresDisabledSpecialMods()
        {
            AppSettings.EnableAP = false;
            AppSettings.EnableRX = false;

            Assert.Equal("osu", GameModeHelper.GameModeToStringSpecial(new OsuRuleset().RulesetInfo, [new APIMod { Acronym = "RX" }]));
        }
    }
}
