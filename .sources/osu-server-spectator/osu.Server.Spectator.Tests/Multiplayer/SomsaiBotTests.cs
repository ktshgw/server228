using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.TeamVersus;
using osu.Game.Online.Rooms;
using osu.Game.Online.Spectator;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Spectator.Entities;
using osu.Server.Spectator.Hubs.Multiplayer;
using osu.Server.Spectator.Hubs.Multiplayer.Standard;
using osu.Server.Spectator.Hubs.Spectator;
using osu.Server.Spectator.Services;
using Xunit;

namespace osu.Server.Spectator.Tests.Multiplayer;

public class SomsaiBotTests : MultiplayerTest
{
    private const string raw = "osu file format v14\n[General]\nAudioFilename: audio.mp3\nMode: 0\n[Metadata]\nTitle: Bot fixture\nArtist: Test\nCreator: Test\nVersion: Test\nBeatmapID: 1234\n[Difficulty]\nHPDrainRate: 5\nCircleSize: 4\nOverallDifficulty: 5\nApproachRate: 5\nSliderMultiplier: 1.4\nSliderTickRate: 1\n[TimingPoints]\n0,500,4,2,1,50,1,0\n[HitObjects]\n100,100,1000,1,0,0:0:0:0:\n200,200,1500,1,0,0:0:0:0:\n300,100,2000,1,0,0:0:0:0:\n100,200,2500,1,0,0:0:0:0:\n";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task BotsJoinNativeTeamReadyLoadStreamAndFinish(int size)
    {
        await Hub.CreateRoom(new MultiplayerRoom(ROOM_ID));
        var humans = new List<Mock<HubCallerContext>> { ContextUser };
        for (int i = 1; i < size; i++)
        {
            CreateUser(4000 + i, out var context, out _);
            humans.Add(context);
            SetUserContext(context);
            await Hub.JoinRoom(ROOM_ID);
        }
        var room = Rooms.GetEntityUnsafe(ROOM_ID)!;
        var bots = Enumerable.Range(0, size).Select(i => new SomsaiBotIdentity { UserId = 8000 + i, Seed = i, Level = "mrekk" }).ToArray();
        foreach (var bot in bots)
            Clients.Setup(c => c.User(bot.UserId.ToString())).Returns(new Mock<DelegatingMultiplayerClient>().Object);
        var config = new SomsaiRoomState { Managed = true, Stage = "ready", StartAllowed = true,
            PlaylistItemId = room.CurrentPlaylistItem.ID, Bots = bots,
            Roster = [humans.Select(c => int.Parse(c.Object.UserIdentifier!)).ToArray(), bots.Select(b => b.UserId).ToArray()] };
        var interop = new Mock<ISomsaiInteropClient>();
        interop.Setup(i => i.GetRoom(ROOM_ID)).ReturnsAsync(config);
        interop.Setup(i => i.SendEvent(ROOM_ID, It.IsAny<string>(), It.IsAny<long>(), It.IsAny<int[]>(), It.IsAny<int[]>(), It.IsAny<int[]>()))
            .Returns<long, string, long, int[], int[], int[]>((_, kind, _, connected, ready, available) =>
            {
                if (kind == "started")
                {
                    Assert.Equal(2 * size, connected.Length);
                    Assert.Equal(2 * size, ready.Length);
                    config.Stage = "playing";
                }
                if (kind == "completed") config.Stage = "results";
                return Task.FromResult(config);
            });
        var states = new EntityStore<SpectatorClientState>();
        var spectator = new Mock<ISpectatorClient>();
        var received = new ConcurrentQueue<(int UserId, FrameDataBundle Bundle)>();
        spectator.Setup(s => s.UserSentFrames(It.IsAny<int>(), It.IsAny<FrameDataBundle>()))
            .Callback<int, FrameDataBundle>((id, bundle) => received.Enqueue((id, bundle))).Returns(Task.CompletedTask);
        var hubClients = new Mock<IHubClients<ISpectatorClient>>();
        hubClients.Setup(h => h.Group(It.IsAny<string>())).Returns(spectator.Object);
        var hub = new Mock<IHubContext<SpectatorHub, ISpectatorClient>>();
        hub.SetupGet(h => h.Clients).Returns(hubClients.Object);
        var service = new SomsaiBotGameplayService(states, hub.Object, RulesetManager, NullLogger<SomsaiBotGameplayService>.Instance);
        service.BeatmapLoader = (_, _) => Task.FromResult(new SomsaiBotBeatmap { Raw = raw, Checksum = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw))) });
        SomsaiBotScore[]? final = null;
        service.ResultWriter = (_, _, scores) => { final = scores.ToArray(); return Task.FromResult(config); };
        var controller = new SomsaiMatchController(room, DatabaseFactory.Object, EventDispatcher, RoomController, LoggerFactory.Object,
                                                   interop.Object, runPump: false, botGameplay: service);
        await room.ChangeMatchType(controller);
        Assert.Equal(2 * size, room.Users.Count);
        Assert.All(room.Users.Where(u => bots.Any(b => b.UserId == u.UserID)), u => Assert.Equal(1, ((TeamVersusUserState)u.MatchState!).TeamID));
        foreach (var human in humans) { SetUserContext(human); await MarkCurrentUserReadyAndAvailable(); }
        for (int i = 0; i < 20 && room.State == MultiplayerRoomState.Open; i++) { await controller.PollOnce(); await Task.Delay(10); }
        Assert.Equal(MultiplayerRoomState.WaitingForLoad, room.State);
        foreach (var human in humans)
        {
            SetUserContext(human);
            await Hub.ChangeState(MultiplayerUserState.Loaded);
            await Hub.ChangeState(MultiplayerUserState.ReadyForGameplay);
        }
        await controller.PollOnce();
        await controller.PollOnce();
        Assert.Equal(MultiplayerRoomState.Playing, room.State);
        SetUserContext(ContextUser);
        await Hub.VoteToSkipIntro();
        await controller.PollOnce();
        Assert.All(room.Users.Where(u => bots.Any(b => b.UserId == u.UserID)), u => Assert.True(u.VotedToSkipIntro));
        await Task.Delay(250);
        Assert.Empty(received); // No unsynchronised/empty packets while the human is loading.
        service.ObserveFrames(USER_ID, new FrameDataBundle(new FrameHeader(new ScoreInfo(), new ScoreProcessorStatistics()), new[] { new LegacyReplayFrame(-1500, 0, 0, ReplayButtonState.None) }));
        await Task.Delay(350); // Intro with no autoplay input: heartbeat frames must advance.
        service.ObserveFrames(USER_ID, new FrameDataBundle(new FrameHeader(new ScoreInfo(), new ScoreProcessorStatistics()), new[] { new LegacyReplayFrame(2000, 0, 0, ReplayButtonState.None) }));
        await Task.Delay(150);
        spectator.Verify(s => s.UserSentFrames(It.IsAny<int>(), It.Is<FrameDataBundle>(b => b.Frames.Count > 0 && b.Header.TotalScore > 0)), Times.AtLeastOnce);
        service.ObserveFrames(USER_ID, new FrameDataBundle(new FrameHeader(new ScoreInfo(), new ScoreProcessorStatistics()), new[] { new LegacyReplayFrame(5000, 0, 0, ReplayButtonState.None) }));
        for (int i = 0; i < 50 && final == null; i++) await Task.Delay(20);
        Assert.NotNull(final);
        Assert.Equal(size, final.Length);
        Assert.All(final, result => { Assert.Equal(4, result.MaxCombo); Assert.InRange(result.Accuracy, .8, 1); });
        foreach (var bot in bots)
        {
            var bundles = received.Where(item => item.UserId == bot.UserId).Select(item => item.Bundle).ToArray();
            Assert.True(bundles.Length >= 3);
            Assert.All(bundles, bundle => Assert.NotEmpty(bundle.Frames));
            var timestamps = bundles.SelectMany(bundle => bundle.Frames).Select(frame => frame.Time).ToArray();
            Assert.True(timestamps.Zip(timestamps.Skip(1), (a, b) => b >= a).All(ordered => ordered));
            Assert.True(timestamps.Last() >= 4000); // Outro frame carries the final score too.
            Assert.Equal(final.Single(result => result.UserId == bot.UserId).Score, bundles.Last().Header.TotalScore);
        }
        await controller.PollOnce();
        foreach (var human in humans) { SetUserContext(human); await Hub.ChangeState(MultiplayerUserState.FinishedPlay); }
        await controller.PollOnce();
        Assert.Equal("results", config.Stage);
        Assert.All(bots, bot => Assert.Null(states.GetEntityUnsafe(bot.UserId)));
        config.Stage = "ended";
        await controller.PollOnce();
        Assert.DoesNotContain(room.Users, user => bots.Any(bot => bot.UserId == user.UserID));
    }
}
