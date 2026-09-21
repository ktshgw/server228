// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Threading.Tasks;
using osu.Game.Beatmaps;
using osu.Game.Scoring.Legacy;
using osu.Server.Spectator.Hubs;
using osu.Server.Spectator.Services;

namespace osu.Server.Spectator.Storage
{
    public class ServerScoreStorage : IScoreStorage
    {
        private readonly ISharedInterop sharedInterop;

        public ServerScoreStorage(ISharedInterop sharedInterop)
        {
            this.sharedInterop = sharedInterop;
        }

        public async Task WriteAsync(ScoreUploader.UploadItem item)
        {
            var bufferedScore = item.Score;
            var score = bufferedScore.Score;
            // beatmap version is required for correct encoding of replays for beatmaps with version <5
            // (see `LegacyBeatmapDecoder.EARLY_VERSION_TIMING_OFFSET`).
            var legacyEncoder = new LegacyScoreEncoder(score, new Beatmap { BeatmapVersion = bufferedScore.Beatmap.osu_file_version });

            using (var outStream = new MemoryStream())
            {
                legacyEncoder.Encode(outStream, true);
                await sharedInterop.UploadReplayAsync(score.ScoreInfo.UserID, score.ScoreInfo.OnlineID, bufferedScore.Beatmap.beatmap_id, outStream);
            }
        }
    }
}
