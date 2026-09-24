using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Online.Rooms;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Spectator;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Replays.Types;
using osu.Game.Scoring;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Hubs.Spectator;
using SomsAi.Shared;

namespace osu.Server.Spectator.Services;

/// <summary>Native spectator sessions for server-owned custom opponents.</summary>
public sealed class SomsaiBotGameplayService
{
    private readonly EntityStore<SpectatorClientState> states;
    private readonly IHubContext<SpectatorHub, ISpectatorClient> hub;
    private readonly RulesetManager rulesets;
    private readonly ILogger<SomsaiBotGameplayService> logger;
    private readonly ConcurrentDictionary<int, SomsaiBotPlayback> humanClocks = new();
    internal Func<long, long, Task<SomsaiBotBeatmap>> BeatmapLoader = SomsaiInteropClient.GetBotBeatmap;
    internal Func<long, long, IEnumerable<SomsaiBotScore>, Task<SomsaiRoomState>> ResultWriter = SomsaiInteropClient.SubmitBotResults;

    public SomsaiBotGameplayService(EntityStore<SpectatorClientState> states, IHubContext<SpectatorHub, ISpectatorClient> hub,
                                   RulesetManager rulesets, ILogger<SomsaiBotGameplayService> logger)
    {
        this.states = states;
        this.hub = hub;
        this.rulesets = rulesets;
        this.logger = logger;
    }

    public void ObserveFrames(int userId, FrameDataBundle data)
    {
        if (data.Frames.Count > 0 && humanClocks.TryGetValue(userId, out var playback))
            playback.ObserveTime(data.Frames[^1].Time);
    }

    public async Task<SomsaiBotPlayback> Prepare(long roomId, MultiplayerPlaylistItem item, SomsaiRoomState config)
    {
        var file = await BeatmapLoader(roomId, item.ID);
        byte[] bytes = Encoding.UTF8.GetBytes(file.Raw);
        if (!Convert.ToHexString(MD5.HashData(bytes)).Equals(file.Checksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bot beatmap revision mismatch.");
        using var stream = new MemoryStream(bytes);
        using var reader = new LineBufferedReader(stream);
        var beatmap = osu.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        beatmap.BeatmapInfo.OnlineID = item.BeatmapID;
        var ruleset = rulesets.GetRuleset(item.RulesetID);
        var mods = item.RequiredMods.Select(mod => mod.ToMod(ruleset)).ToArray();
        var working = new FlatWorkingBeatmap(beatmap);
        var playable = working.GetPlayableBeatmap(ruleset.RulesetInfo, mods);
        var attributes = ruleset.CreateDifficultyCalculator(working).Calculate(mods);
        double aim = (attributes as OsuDifficultyAttributes)?.AimDifficulty ?? 0;
        double speed = (attributes as OsuDifficultyAttributes)?.SpeedDifficulty ?? 0;
        var playback = new SomsaiBotPlayback(roomId, item, config, states, hub, humanClocks, logger, playable, mods, attributes.StarRating, ruleset, ResultWriter,
            aim + speed > 0 ? aim / (aim + speed) : null,
            item.RulesetID == 0 ? BotTechnicalFeatures.TryParse(file.Raw) : null,
            attributes is OsuDifficultyAttributes osuAttributes ? 1 - osuAttributes.SliderFactor : null);
        return playback;
    }
}

public sealed class SomsaiBotPlayback : IDisposable
{
    public long ItemId { get; }
    public bool Completed { get; private set; }
    public bool Failed { get; private set; }
    public bool Started { get; private set; }
    private readonly long roomId;
    private readonly EntityStore<SpectatorClientState> states;
    private readonly IHubContext<SpectatorHub, ISpectatorClient> hub;
    private readonly ConcurrentDictionary<int, SomsaiBotPlayback> clocks;
    private readonly ILogger logger;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<int, (SomsAiBotSimulation Simulation, ScoreInfo Score, SpectatorState State)> players = new();
    private readonly int[] humans;
    private readonly LegacyReplayFrame[] frames;
    private readonly double endTime;
    private readonly double rate;
    private readonly Stopwatch clock = new();
    private readonly object timeLock = new();
    private double anchorTime;
    private double anchorElapsed;
    private bool synchronised;
    private int frameIndex;
    private readonly Func<long, long, IEnumerable<SomsaiBotScore>, Task<SomsaiRoomState>> resultWriter;

    internal SomsaiBotPlayback(long roomId, MultiplayerPlaylistItem item, SomsaiRoomState config,
                               EntityStore<SpectatorClientState> states, IHubContext<SpectatorHub, ISpectatorClient> hub,
                               ConcurrentDictionary<int, SomsaiBotPlayback> clocks, ILogger logger,
                               IBeatmap beatmap, Mod[] mods, double stars, osu.Game.Rulesets.Ruleset ruleset,
                               Func<long, long, IEnumerable<SomsaiBotScore>, Task<SomsaiRoomState>> resultWriter, double? aimRatio = null,
                               BotTechnicalFeatures? technical = null, double? sliderControl = null)
    {
        this.roomId = roomId;
        ItemId = item.ID;
        this.states = states;
        this.hub = hub;
        this.clocks = clocks;
        this.logger = logger;
        this.resultWriter = resultWriter;
        humans = config.Roster.SelectMany(team => team).Except(config.Bots.Select(bot => bot.UserId)).ToArray();
        rate = mods.OfType<IApplicableToRate>().Aggregate(1d, (value, mod) => mod.ApplyToRate(0, value));
        endTime = beatmap.HitObjects.Max(hit => hit.GetEndTime()) + 1500;
        var replay = ((ICreateReplayData)ruleset.GetAutoplayMod()!).CreateReplayData(beatmap, mods).Replay;
        frames = replay.Frames.Cast<IConvertibleReplayFrame>().Select(frame => frame.ToLegacy(beatmap)).OrderBy(frame => frame.Time).ToArray();
        foreach (var bot in config.Bots)
        {
            string tier = bot.Level.Equals("top1000", StringComparison.OrdinalIgnoreCase) ? "Expert" : bot.Level;
            if (!Enum.TryParse<SomsAiBotLevel>(tier, true, out var level))
                throw new InvalidDataException("Invalid bot level.");
            var profile = bot.SkillProfile ?? new BotSkillProfile { Rank = bot.GlobalRank };
            var simulation = new SomsAiBotSimulation(ruleset, beatmap, mods, level, stars, bot.Seed ^ checked((int)item.ID), profile, aimRatio, technical, sliderControl);
            var score = new ScoreInfo { User = new APIUser { Id = bot.UserId }, BeatmapInfo = beatmap.BeatmapInfo, Ruleset = ruleset.RulesetInfo, Mods = mods };
            simulation.Processor.PopulateScore(score);
            players.Add(bot.UserId, (simulation, score, new SpectatorState
            {
                BeatmapID = item.BeatmapID, RulesetID = item.RulesetID, Mods = item.RequiredMods,
                MaximumStatistics = new(score.MaximumStatistics), State = SpectatedUserState.Playing,
            }));
        }
    }

    public void Start()
    {
        if (Started) return;
        Started = true;
        clock.Start();
        foreach (int human in humans) clocks[human] = this;
        _ = run();
    }

    // Native human replay timestamps align the start and any shared intro skip.
    // Score/accuracy are always computed independently by the server.
    public void ObserveTime(double time)
    {
        if (!double.IsFinite(time) || time > endTime + 10000) return;
        lock (timeLock)
        {
            double elapsed = clock.Elapsed.TotalMilliseconds;
            double current = anchorTime + (elapsed - anchorElapsed) * rate;
            if (!synchronised || time > current + 1500)
            {
                anchorTime = time;
                anchorElapsed = elapsed;
                synchronised = true;
            }
        }
    }

    private async Task run()
    {
        var token = cancellation.Token;
        try
        {
            foreach (var (id, player) in players)
            {
                using (var usage = await states.GetForUse(id, true))
                    usage.Item = new SpectatorClientState($"somsai-bot:{roomId}:{ItemId}", id) { State = player.State };
                await hub.Clients.Group(SpectatorHub.GetGroupId(id)).UserBeganPlaying(id, player.State);
            }
            while (true)
            {
                token.ThrowIfCancellationRequested();
                double time;
                lock (timeLock) time = synchronised ? anchorTime + (clock.Elapsed.TotalMilliseconds - anchorElapsed) * rate : -2000;
                if (!synchronised)
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(15))
                        throw new InvalidOperationException("No human gameplay clock received for bot room.");
                    await Task.Delay(100, token);
                    continue;
                }
                int firstFrame = frameIndex;
                while (frameIndex < frames.Length && frames[frameIndex].Time <= time) frameIndex++;
                var batch = frames.Skip(firstFrame).Take(frameIndex - firstFrame).ToArray();
                // Native spectator consumers timestamp the score using the first replay frame.
                // Autoplay has gaps (intro, breaks and outro), but score/clock updates still need
                // a frame. Keep the last input at the CURRENT time, never reuse an old timestamp.
                if (batch.Length == 0 || batch[^1].Time < time)
                {
                    var previous = frameIndex > 0 ? frames[frameIndex - 1] : null;
                    batch = batch.Append(new LegacyReplayFrame(time, previous?.MouseX, previous?.MouseY, previous?.ButtonState ?? ReplayButtonState.None)).ToArray();
                }
                foreach (var (id, player) in players)
                {
                    player.Simulation.Advance(time);
                    player.Simulation.Processor.PopulateScore(player.Score);
                    var bundle = new FrameDataBundle(player.Score, player.Simulation.Processor, batch);
                    bundle.Header.ReceivedTime = DateTimeOffset.UtcNow;
                    await hub.Clients.Group(SpectatorHub.GetGroupId(id)).UserSentFrames(id, bundle);
                }
                if (time >= endTime) break;
                await Task.Delay(100, token);
            }
            var scores = players.Select(pair => new SomsaiBotScore { UserId = pair.Key, Score = pair.Value.Score.TotalScore,
                Accuracy = pair.Value.Score.Accuracy, MaxCombo = pair.Value.Score.MaxCombo,
                Statistics = pair.Value.Score.Statistics.ToDictionary(p => p.Key.ToString(), p => p.Value),
                MaximumStatistics = pair.Value.Score.MaximumStatistics.ToDictionary(p => p.Key.ToString(), p => p.Value),
                Rank = pair.Value.Score.Rank.ToString(), Passed = pair.Value.Score.Passed }).ToArray();
            // Retry a lost acknowledgement with the exact same immutable result.
            for (int attempt = 0; ; attempt++)
            {
                try { await resultWriter(roomId, ItemId, scores); break; }
                catch when (attempt < 4) { await Task.Delay(1000, token); }
            }
            Completed = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Failed = true;
            logger.LogWarning(exception, "SOMSAI bot playback failed in room {RoomId}, item {ItemId}", roomId, ItemId);
        }
        finally
        {
            foreach (int human in humans)
                ((ICollection<KeyValuePair<int, SomsaiBotPlayback>>)clocks).Remove(new(human, this));
            foreach (var (id, player) in players)
            {
                try
                {
                    using var usage = await states.TryGetForUse(id);
                    if (usage?.Item?.State != player.State) continue;
                    player.State.State = Completed ? SpectatedUserState.Passed : SpectatedUserState.Quit;
                    await hub.Clients.Group(SpectatorHub.GetGroupId(id)).UserFinishedPlaying(id, player.State);
                    usage.Destroy();
                }
                catch (Exception exception) { logger.LogDebug(exception, "Cleaning up bot spectator {UserId}", id); }
                player.Simulation.Processor.Dispose();
            }
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        if (!Started)
            foreach (var player in players.Values) player.Simulation.Processor.Dispose();
    }
}
