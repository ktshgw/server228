// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using osu.Server.Spectator.Services;
using Xunit;

namespace osu.Server.Spectator.Tests.RankedPlay
{
    public class RankedDodgeOutboxTests
    {
        [Fact]
        public void PenaltySurvivesRestartUntilAcknowledged()
        {
            string path = Path.Combine(Path.GetTempPath(), "soms-dodge-test-" + Guid.NewGuid());
            try
            {
                new RankedDodgeOutbox(path).Save(123, 456);
                var restarted = new RankedDodgeOutbox(path);
                Assert.Equal(456, restarted.Load()[123]);
                // A process killed before atomic rename cannot leave a partial
                // JSON entry in the set that is replayed on startup.
                File.WriteAllText(Path.Combine(path, "124.json.tmp"), "{");
                Assert.Single(restarted.Load());
                restarted.Remove(123);
                Assert.Empty(new RankedDodgeOutbox(path).Load());
            }
            finally
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
