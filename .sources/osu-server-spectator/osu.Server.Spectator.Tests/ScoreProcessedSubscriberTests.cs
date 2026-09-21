// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using osu.Game.Online.Metadata;
using osu.Server.Spectator.Database;
using osu.Server.Spectator.Database.Models;
using osu.Server.Spectator.Hubs.Metadata;
using osu.Server.Spectator.Hubs.Spectator;
using StackExchange.Redis;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class ScoreProcessedSubscriberTests
    {
        [Fact]
        public async Task MultiplayerScoreUsesPlaylistSpecificBestAndRankQueries()
        {
            var database = new Mock<IDatabaseAccess>();
            var databaseFactory = new Mock<IDatabaseFactory>();
            databaseFactory.Setup(factory => factory.GetInstance()).Returns(database.Object);

            database.Setup(db => db.GetRoomAsync(10)).ReturnsAsync(new multiplayer_room { type = database_match_type.playlists });
            database.Setup(db => db.GetMultiplayerRoomIdForScoreAsync(99)).ReturnsAsync((10L, 20L));
            database.Setup(db => db.GetScoreAsync(99)).ReturnsAsync(new SoloScore
            {
                id = 99,
                user_id = 7,
                passed = true,
                total_score = 123456
            });
            database.Setup(db => db.GetUserBestScoreAsync(10, 20, 7)).ReturnsAsync(new playlist_best_score { score_id = 99 });
            database.Setup(db => db.GetUserRankInRoomAsync(10, 20, 99)).ReturnsAsync(3);

            Action<RedisChannel, RedisValue>? messageHandler = null;
            var redisSubscriber = new Mock<ISubscriber>();
            redisSubscriber.Setup(s => s.Subscribe(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
                           .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>((_, handler, _) => messageHandler = handler);
            var redis = new Mock<IConnectionMultiplexer>();
            redis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(redisSubscriber.Object);

            var delivered = new TaskCompletionSource<MultiplayerRoomScoreSetEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var metadataClient = new Mock<IClientProxy>();
            metadataClient.Setup(client => client.SendCoreAsync(nameof(IMetadataClient.MultiplayerRoomScoreSet), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                          .Callback<string, object?[], CancellationToken>((_, args, _) => delivered.TrySetResult((MultiplayerRoomScoreSetEvent)args[0]!))
                          .Returns(Task.CompletedTask);
            var metadataClients = new Mock<IHubClients>();
            metadataClients.Setup(clients => clients.Group(MetadataHub.MultiplayerRoomWatchersGroup(10))).Returns(metadataClient.Object);
            var metadataHub = new Mock<IHubContext<MetadataHub>>();
            metadataHub.SetupGet(hub => hub.Clients).Returns(metadataClients.Object);

            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

            using var subscriber = new ScoreProcessedSubscriber(
                databaseFactory.Object,
                redis.Object,
                new Mock<IHubContext<SpectatorHub>>().Object,
                metadataHub.Object,
                loggerFactory.Object);

            await subscriber.RegisterForMultiplayerRoomAsync(7, 10);
            Assert.NotNull(messageHandler);
            messageHandler!(default, "{\"ScoreId\":99}");

            MultiplayerRoomScoreSetEvent result = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(10, result.RoomID);
            Assert.Equal(20, result.PlaylistItemID);
            Assert.Equal(99, result.ScoreID);
            Assert.Equal(3, result.NewRank);
            database.Verify(db => db.GetUserBestScoreAsync(10, 20, 7), Times.Once);
            database.Verify(db => db.GetUserRankInRoomAsync(10, 20, 99), Times.Once);
        }
    }
}
