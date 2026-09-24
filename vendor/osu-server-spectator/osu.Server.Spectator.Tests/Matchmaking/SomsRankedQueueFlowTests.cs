using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Moq;
using MySqlConnector;
using osu.Game.Online.Matchmaking;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Multiplayer.MatchTypes.RankedPlay;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs.Multiplayer.Matchmaking.RankedPlay;
using osu.Server.Spectator.Tests.Multiplayer;
using Xunit;

namespace osu.Server.Spectator.Tests.Matchmaking;

// Real queue, room and RankedPlay controllers with isolated in-memory users,
// transport and persistence. No running lobby or production account is touched.
public class SomsRankedQueueFlowTests : MultiplayerTest
{
    // Run after the real Python interop API has created SOMS_TEST_ROOM_ID in
    // the disposable integration database. The explicit database-name guard
    // prevents this write test from ever targeting the normal server schema.
    [SomsMatchmakingDatabaseFact]
    public async Task ApiCreatedRankedRoomAcceptsTwoPlayersAcrossRealMySqlBoundary()
    {
        Assert.Equal("soms_matchmaking_integration", AppSettings.DatabaseName);
        long roomId = long.Parse(Environment.GetEnvironmentVariable("SOMS_TEST_ROOM_ID")!);
        using var connection = new MySqlConnection(new MySqlConnectionStringBuilder
        {
            Server = AppSettings.DatabaseHost,
            Port = (uint)AppSettings.DatabasePort,
            Database = AppSettings.DatabaseName,
            UserID = AppSettings.DatabaseUser,
            Password = AppSettings.DatabasePassword,
        }.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT @@FOREIGN_KEY_CHECKS"));
        var maps = (await connection.QueryAsync<database_beatmap>(
            "SELECT id AS beatmap_id, checksum, difficulty_rating, 0 AS playmode FROM beatmaps WHERE mode = 'OSU'")).ToArray();
        Assert.True(maps.Length >= 10);

        configure(maps.Length);
        using var realDatabase = new DatabaseAccess(LoggerFactory.Object, LegacyIO.Object, RulesetManager);
        Assert.Equal(database_match_type.ranked_play, (await realDatabase.GetRealtimeRoomAsync(roomId))!.type);
        LegacyIO.Setup(io => io.CreateRoomAsync(It.IsAny<int>(), It.IsAny<MultiplayerRoom>(), It.IsAny<bool>()))
                .ReturnsAsync(roomId);
        Database.Setup(db => db.GetRealtimeRoomAsync(roomId)).Returns(() => realDatabase.GetRealtimeRoomAsync(roomId));
        Database.Setup(db => db.GetAllPlaylistItemsAsync(roomId)).Returns(() => realDatabase.GetAllPlaylistItemsAsync(roomId));
        Database.Setup(db => db.GetMatchmakingGlobalPoolBeatmapsAsync(0, 0, It.IsAny<uint>())).ReturnsAsync(maps);
        Database.Setup(db => db.AddPlaylistItemAsync(It.IsAny<multiplayer_playlist_item>()))
                .Returns<multiplayer_playlist_item>(realDatabase.AddPlaylistItemAsync);
        Database.Setup(db => db.SetRoomEndDateAsync(It.IsAny<MultiplayerRoom>(), It.IsAny<DateTimeOffset?>()))
                .Returns<MultiplayerRoom, DateTimeOffset?>(realDatabase.SetRoomEndDateAsync);

        await MatchmakingBackgroundService.AddToQueueAsync(UserStates.GetEntityUnsafe(USER_ID)!, 1);
        await MatchmakingBackgroundService.AddToQueueAsync(UserStates.GetEntityUnsafe(USER_ID_2)!, 1);
        await MatchmakingBackgroundService.ExecuteOnceAsync();
        await MatchmakingBackgroundService.AcceptInvitationAsync(UserStates.GetEntityUnsafe(USER_ID)!);
        await MatchmakingBackgroundService.AcceptInvitationAsync(UserStates.GetEntityUnsafe(USER_ID_2)!);
        UserReceiver.Verify(u => u.MatchmakingRoomReady(roomId, It.IsAny<string>()), Times.Once);
        User2Receiver.Verify(u => u.MatchmakingRoomReady(roomId, It.IsAny<string>()), Times.Once);

        SetUserContext(ContextUser);
        await Hub.JoinRoom(roomId);
        SetUserContext(ContextUser2);
        await Hub.JoinRoom(roomId);
        using var usage = await Hub.GetRoom(roomId);
        var controller = Assert.IsType<RankedPlayMatchController>(usage.Item!.MatchController);
        Assert.Equal(RankedPlayStage.RoundWarmup, controller.State.Stage);
        Assert.Equal(5, controller.State.Users[USER_ID].Hand.Count);
        Assert.Equal(5, controller.State.Users[USER_ID_2].Hand.Count);
        var savedItem = Assert.Single(await realDatabase.GetAllPlaylistItemsAsync(roomId));
        Assert.Equal(AppSettings.BanchoBotUserId, savedItem.owner_id);
        Assert.Contains(maps, map => map.beatmap_id == savedItem.beatmap_id);

        // Prove that this fixture enforces the production constraint which
        // originally rejected the accidental Quick Play owner's default zero.
        var invalidOwnerItem = savedItem.Clone();
        invalidOwnerItem.owner_id = 0;
        var constraintError = await Assert.ThrowsAsync<MySqlException>(() => realDatabase.AddPlaylistItemAsync(invalidOwnerItem));
        Assert.Equal(1452, constraintError.Number);
        Assert.Single(await realDatabase.GetAllPlaylistItemsAsync(roomId));

        // Exercise the same native closure used after a failed creation.
        await realDatabase.SetRoomEndDateAsync(usage.Item, DateTimeOffset.Now);
        Assert.True((await realDatabase.GetRealtimeRoomAsync(roomId))!.ends_at <= DateTimeOffset.Now);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(9)]
    public async Task TwoPlayersQueueAcceptJoinAndReceiveDistinctHands(int eligibleMapCount)
    {
        configure(eligibleMapCount);

        await MatchmakingBackgroundService.AddToQueueAsync(UserStates.GetEntityUnsafe(USER_ID)!, 1);
        await MatchmakingBackgroundService.AddToQueueAsync(UserStates.GetEntityUnsafe(USER_ID_2)!, 1);
        await MatchmakingBackgroundService.ExecuteOnceAsync();
        UserReceiver.Verify(u => u.MatchmakingRoomInvitedWithParams(It.Is<MatchmakingRoomInvitationParams>(p => p.Type == MatchmakingPoolType.RankedPlay)), Times.Once);
        await MatchmakingBackgroundService.AcceptInvitationAsync(UserStates.GetEntityUnsafe(USER_ID)!);
        await MatchmakingBackgroundService.AcceptInvitationAsync(UserStates.GetEntityUnsafe(USER_ID_2)!);
        await MatchmakingBackgroundService.ExecuteOnceAsync();
        if (eligibleMapCount < 10)
        {
            // A rank/unrank change between invitation and acceptance must not
            // leave either player waiting for a room that cannot be created.
            LegacyIO.Verify(io => io.CreateRoomAsync(It.IsAny<int>(), It.IsAny<MultiplayerRoom>(), It.IsAny<bool>()), Times.Never);
            UserReceiver.Verify(u => u.MatchmakingQueueLeft(), Times.Once);
            User2Receiver.Verify(u => u.MatchmakingQueueLeft(), Times.Once);
            return;
        }
        UserReceiver.Verify(u => u.MatchmakingRoomReady(0, It.IsAny<string>()), Times.Once);
        User2Receiver.Verify(u => u.MatchmakingRoomReady(0, It.IsAny<string>()), Times.Once);

        SetUserContext(ContextUser);
        await Hub.JoinRoom(0);
        SetUserContext(ContextUser2);
        await Hub.JoinRoom(0);
        using var usage = await Hub.GetRoom(0);
        var controller = Assert.IsType<RankedPlayMatchController>(usage.Item!.MatchController);
        Assert.Equal(RankedPlayStage.RoundWarmup, controller.State.Stage);
        Assert.Equal(5, controller.State.Users[USER_ID].Hand.Count);
        Assert.Equal(5, controller.State.Users[USER_ID_2].Hand.Count);
        Assert.Empty(controller.State.Users[USER_ID].Hand.Intersect(controller.State.Users[USER_ID_2].Hand));
        Assert.NotEqual(0, controller.CurrentItem.BeatmapID);
        Assert.Equal(AppSettings.BanchoBotUserId, controller.CurrentItem.OwnerID);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("catalogue")]
    [InlineData("playlist")]
    [InlineData("type")]
    public async Task FailedRoomCreationCancelsBothPlayersAndAllowsRequeue(string failure)
    {
        configure(50);
        switch (failure)
        {
            case "api":
                LegacyIO.Setup(io => io.CreateRoomAsync(It.IsAny<int>(), It.IsAny<MultiplayerRoom>(), It.IsAny<bool>()))
                        .ThrowsAsync(new InvalidOperationException("Synthetic API failure"));
                break;
            case "catalogue":
                Database.Setup(db => db.GetMatchmakingGlobalPoolBeatmapsAsync(0, 0, It.IsAny<uint>()))
                        .ThrowsAsync(new InvalidOperationException("Synthetic catalogue failure"));
                break;
            case "playlist":
                Database.Setup(db => db.AddPlaylistItemAsync(It.IsAny<multiplayer_playlist_item>()))
                        .ThrowsAsync(new InvalidOperationException("Synthetic playlist failure"));
                break;
            case "type":
                Database.Setup(db => db.GetRealtimeRoomAsync(0)).ReturnsAsync(new multiplayer_room
                {
                    id = 0,
                    type = database_match_type.matchmaking,
                    host_id = AppSettings.BanchoBotUserId,
                });
                break;
        }

        await MatchmakingBackgroundService.AddToQueueAsync(UserStates.GetEntityUnsafe(USER_ID)!, 1);
        await MatchmakingBackgroundService.AddToQueueAsync(UserStates.GetEntityUnsafe(USER_ID_2)!, 1);
        await MatchmakingBackgroundService.ExecuteOnceAsync();
        await MatchmakingBackgroundService.AcceptInvitationAsync(UserStates.GetEntityUnsafe(USER_ID)!);
        await MatchmakingBackgroundService.AcceptInvitationAsync(UserStates.GetEntityUnsafe(USER_ID_2)!);

        UserReceiver.Verify(u => u.MatchmakingQueueLeft(), Times.Once);
        User2Receiver.Verify(u => u.MatchmakingQueueLeft(), Times.Once);
        UserReceiver.Verify(u => u.MatchmakingRoomReady(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        User2Receiver.Verify(u => u.MatchmakingRoomReady(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        Assert.Null(Rooms.GetEntityUnsafe(0));
        Assert.False(MatchmakingBackgroundService.IsInQueue(UserStates.GetEntityUnsafe(USER_ID)!));
        Assert.False(MatchmakingBackgroundService.IsInQueue(UserStates.GetEntityUnsafe(USER_ID_2)!));
        Database.Verify(db => db.SetRoomEndDateAsync(It.Is<MultiplayerRoom>(r => r.RoomID == 0),
            It.Is<DateTimeOffset?>(date => date.HasValue && date <= DateTimeOffset.Now)),
            failure is "playlist" or "type" ? Times.Once() : Times.Never());
        if (failure == "type")
            Database.Verify(db => db.AddPlaylistItemAsync(It.IsAny<multiplayer_playlist_item>()), Times.Never);

        configure(50);
        await MatchmakingBackgroundService.AddToQueueAsync(UserStates.GetEntityUnsafe(USER_ID)!, 1);
        await MatchmakingBackgroundService.AddToQueueAsync(UserStates.GetEntityUnsafe(USER_ID_2)!, 1);
        await MatchmakingBackgroundService.ExecuteOnceAsync();
        UserReceiver.Verify(u => u.MatchmakingRoomInvitedWithParams(It.IsAny<MatchmakingRoomInvitationParams>()), Times.Exactly(2));
        User2Receiver.Verify(u => u.MatchmakingRoomInvitedWithParams(It.IsAny<MatchmakingRoomInvitationParams>()), Times.Exactly(2));
    }

    private void configure(int eligibleMapCount)
    {
        Database.Setup(db => db.GetMatchmakingPoolAsync(1)).ReturnsAsync(new matchmaking_pool
        {
            id = 1,
            name = "SOMS! test",
            type = matchmaking_pool_type.ranked_play,
            active = true,
            ranked = true,
            lobby_size = 2,
            rating_search_radius = 9999,
        });
        Database.Setup(db => db.GetRealtimeRoomAsync(0)).ReturnsAsync(new multiplayer_room
        {
            id = 0,
            type = database_match_type.ranked_play,
            ends_at = DateTimeOffset.Now.AddMinutes(5),
            host_id = AppSettings.BanchoBotUserId,
        });
        Database.Setup(db => db.GetAllPlaylistItemsAsync(0)).ReturnsAsync([]);
        Database.Setup(db => db.GetMatchmakingGlobalPoolBeatmapsAsync(0, 0, It.IsAny<uint>())).ReturnsAsync(
            Enumerable.Range(1000, eligibleMapCount).Select(id => new database_beatmap
            {
                beatmap_id = id,
                checksum = id.ToString("x32"),
                difficulty_rating = 4,
                playmode = 0,
            }).ToArray());
    }
}

public sealed class SomsMatchmakingDatabaseFactAttribute : FactAttribute
{
    public SomsMatchmakingDatabaseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SOMS_MATCHMAKING_DB_TEST") != "1")
            Skip = "Requires an isolated soms_matchmaking_integration database and an API-created test room.";
    }
}
