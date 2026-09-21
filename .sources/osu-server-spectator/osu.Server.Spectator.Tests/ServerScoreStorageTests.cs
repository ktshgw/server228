// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Osu;
using osu.Game.Scoring;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs;
using osu.Server.Spectator.Services;
using osu.Server.Spectator.Storage;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class ServerScoreStorageTests
    {
        [Fact]
        public async Task UploadsEncodedReplayToServer()
        {
            var uploadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseUpload = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            MemoryStream? uploadedReplay = null;

            var sharedInterop = new Mock<ISharedInterop>();
            sharedInterop.Setup(s => s.UploadReplayAsync(42, 99, 123, It.IsAny<MemoryStream>()))
                         .Callback<int, long, int, MemoryStream>((_, _, _, stream) =>
                         {
                             uploadedReplay = stream;
                             uploadStarted.SetResult(true);
                         })
                         .Returns(releaseUpload.Task);

            using var item = new ScoreUploader.UploadItem(
                7,
                new BufferedScore(
                    new Score
                    {
                        ScoreInfo =
                        {
                            User = new APIUser { Id = 42 },
                            OnlineID = 99,
                            Ruleset = new OsuRuleset().RulesetInfo,
                        }
                    },
                    new database_beatmap
                    {
                        beatmap_id = 123,
                        osu_file_version = 14,
                    }),
                new CancellationTokenSource());

            Task writeTask = new ServerScoreStorage(sharedInterop.Object).WriteAsync(item);

            Task firstCompletion = await Task.WhenAny(uploadStarted.Task, writeTask);
            if (firstCompletion == writeTask)
                await writeTask;

            await uploadStarted.Task;
            Assert.NotNull(uploadedReplay);
            Assert.NotEmpty(uploadedReplay!.ToArray());
            Assert.False(writeTask.IsCompleted);

            releaseUpload.SetResult(null);
            await writeTask;

            sharedInterop.Verify(s => s.UploadReplayAsync(42, 99, 123, It.IsAny<MemoryStream>()), Times.Once);
        }
    }
}
