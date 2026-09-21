// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using osu.Game.Online.Multiplayer;
using MatchType = osu.Game.Online.Rooms.MatchType;
using osu.Server.Spectator.Services;
using Xunit;

namespace osu.Server.Spectator.Tests
{
    public class SharedInteropTests
    {
        [Theory]
        [InlineData(MatchType.RankedPlay, "ranked_play")]
        [InlineData(MatchType.Matchmaking, "matchmaking")]
        [InlineData(MatchType.HeadToHead, "head_to_head")]
        public async Task CreateRoomPreservesCanonicalRoomType(MatchType type, string expectedType)
        {
            string? payload = null;
            using var httpClient = new HttpClient(new DelegateHandler(async request =>
            {
                Assert.Equal("/_lio/multiplayer/rooms", request.RequestUri!.AbsolutePath);
                payload = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("77") };
            }));
            var interop = new SharedInterop(httpClient, createLoggerFactory());
            long id = await interop.CreateRoomAsync(AppSettings.BanchoBotUserId, new MultiplayerRoom(0)
            {
                Settings = { Name = "SOMS! integration", MatchType = type }
            }, false);

            using JsonDocument body = JsonDocument.Parse(payload!);
            Assert.Equal(expectedType, body.RootElement.GetProperty("type").GetString());
            Assert.Equal(AppSettings.BanchoBotUserId, body.RootElement.GetProperty("user_id").GetInt32());
            Assert.Equal(77, id);
        }

        [Fact]
        public async Task G0v0EndpointsUseExpectedPayloads()
        {
            var requests = new List<CapturedRequest>();
            using var httpClient = new HttpClient(new DelegateHandler(async request =>
            {
                requests.Add(new CapturedRequest(
                    request.Method,
                    request.RequestUri!,
                    request.Content == null ? null : await request.Content.ReadAsStringAsync(),
                    request.Headers.Contains("X-LIO-Signature")));

                string response = request.RequestUri!.AbsolutePath.EndsWith("ruleset-hashes", StringComparison.Ordinal)
                    ? "{\"custom\":{\"latest-version\":\"1.0\",\"versions\":{\"1.0\":\"hash\"}}}"
                    : string.Empty;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
            }));
            var interop = new SharedInterop(httpClient, createLoggerFactory());

            await interop.EnsureBeatmapPresentAsync(123);
            await interop.UploadReplayAsync(42, 99, 123, new MemoryStream([1, 2, 3]));
            var hashes = await interop.GetRulesetHashesAsync();

            Assert.Collection(requests,
                request =>
                {
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal("/_lio/beatmaps/ensure", request.Uri.AbsolutePath);
                    using JsonDocument body = JsonDocument.Parse(request.Body!);
                    Assert.Equal(123, body.RootElement.GetProperty("beatmap_id").GetInt32());
                },
                request =>
                {
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal("/_lio/scores/replay", request.Uri.AbsolutePath);
                    using JsonDocument body = JsonDocument.Parse(request.Body!);
                    Assert.Equal(42, body.RootElement.GetProperty("user_id").GetInt32());
                    Assert.Equal(99, body.RootElement.GetProperty("score_id").GetInt64());
                    Assert.Equal(123, body.RootElement.GetProperty("beatmap_id").GetInt32());
                    Assert.Equal(Convert.ToBase64String([1, 2, 3]), body.RootElement.GetProperty("mreplay").GetString());
                },
                request =>
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.Equal("/_lio/ruleset-hashes", request.Uri.AbsolutePath);
                });

            Assert.All(requests, request =>
            {
                Assert.Contains("timestamp=", request.Uri.Query);
                Assert.True(request.HasSignature);
            });
            Assert.Equal("hash", hashes["custom"].Versions["1.0"]);
        }

        private static ILoggerFactory createLoggerFactory()
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            return loggerFactory.Object;
        }

        [Fact]
        public async Task RankedDodgePostsTheValidatedRoomContext()
        {
            using var httpClient = new HttpClient(new DelegateHandler(async request =>
            {
                Assert.True(request.Headers.Contains("X-LIO-Signature"));
                Assert.Equal("/_lio/ranked-dodge", request.RequestUri!.AbsolutePath);
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal(77, body.RootElement.GetProperty("room_id").GetInt64());
                Assert.Equal(42, body.RootElement.GetProperty("user_id").GetInt32());
                Assert.True(body.RootElement.GetProperty("allow_missing_room").GetBoolean());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"user_id\":42,\"level\":1,\"expires_at\":\"2026-09-06T12:05:00Z\",\"account_banned\":false}")
                };
            }));
            RankedDodgeStatus status = await new SharedInterop(httpClient, createLoggerFactory()).RegisterRankedDodgeAsync(77, 42);
            Assert.Equal(42, status.UserId);
            Assert.Equal(1, status.Level);
            Assert.Equal(new DateTimeOffset(2026, 9, 6, 12, 5, 0, TimeSpan.Zero), status.ExpiresAt);
        }

        [Fact]
        public async Task HangingRankedDodgeRequestIsCancelled()
        {
            using var httpClient = new HttpClient(new HangingHandler());
            var interop = new SharedInterop(httpClient, createLoggerFactory());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => interop.RegisterRankedDodgeAsync(77, 42).WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task PartyReservationRetryKeepsRequestIdAndUsesProtectedLeaseRoutes()
        {
            var requestIds = new List<string>();
            var methods = new List<HttpMethod>();
            using var httpClient = new HttpClient(new DelegateHandler(async request =>
            {
                Assert.True(request.Headers.Contains("X-LIO-Signature"));
                methods.Add(request.Method);
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/reserve", StringComparison.Ordinal))
                {
                    Assert.Equal("/_lio/parties/42/reserve", path);
                    using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    Assert.Equal(17, body.RootElement.GetProperty("pool_id").GetInt32());
                    requestIds.Add(body.RootElement.GetProperty("request_id").GetString()!);
                    if (requestIds.Count == 1)
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("retry") };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{{\"id\":1,\"captain_id\":42,\"members\":[42,43],\"reservation_id\":\"{requestIds[0]}\"}}")
                    };
                }
                Assert.StartsWith("/_lio/party-reservations/" + requestIds[0], path);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
            }));
            var interop = new SharedInterop(httpClient, createLoggerFactory());
            RankedPartyReservation reservation = await interop.ReserveRankedPartyAsync(42, 17);
            Assert.Equal(2, requestIds.Count);
            Assert.Equal(requestIds[0], requestIds[1]);
            Assert.True(Guid.TryParse(reservation.ReservationId, out _));
            Assert.Equal(new[] { 42, 43 }, reservation.Members);
            await interop.RenewPartyReservationAsync(reservation.ReservationId);
            await interop.ReleasePartyReservationAsync(reservation.ReservationId);
            Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Post, HttpMethod.Post, HttpMethod.Delete }, methods);
        }

        private class HangingHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("The request should have been cancelled.");
            }
        }

        private record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, bool HasSignature);

        private class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
        }
    }
}
