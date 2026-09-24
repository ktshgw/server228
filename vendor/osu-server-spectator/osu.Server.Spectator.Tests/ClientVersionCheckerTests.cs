// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using osu.Game.Online;
using osu.Server.Spectator.Database;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class ClientVersionCheckerTests
    {
        [Fact]
        public async Task VersionHashIsCachedForConnectionLifetime()
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var checker = new ClientVersionChecker(
                new Mock<IDatabaseFactory>().Object,
                cache,
                new Mock<ILoggerFactory>().Object);

            var context = new Mock<HubCallerContext>();
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers[HubClientConnector.VERSION_HASH_HEADER] = "client-hash";
            var feature = new Mock<IHttpContextFeature>();
            feature.SetupGet(f => f.HttpContext).Returns(httpContext);
            context.SetupGet(c => c.ConnectionId).Returns("connection-id");
            context.Setup(c => c.Features.Get<IHttpContextFeature>()).Returns(feature.Object);

            var lifetimeContext = new HubLifetimeContext(context.Object, new Mock<IServiceProvider>().Object, new Mock<Hub>().Object);
            bool connected = false;

            await checker.OnConnectedAsync(lifetimeContext, _ =>
            {
                connected = true;
                return Task.CompletedTask;
            });

            Assert.True(connected);
            Assert.Equal("client-hash", cache.Get<string?>($"{HubClientConnector.VERSION_HASH_HEADER}#connection-id"));

            await checker.OnDisconnectedAsync(lifetimeContext, null, (_, _) => Task.CompletedTask);

            Assert.Null(cache.Get<string?>($"{HubClientConnector.VERSION_HASH_HEADER}#connection-id"));
        }
    }
}
