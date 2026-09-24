// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Server.Spectator.Entities;

namespace osu.Server.Spectator.Services
{
    public class SharedInterop : ISharedInterop
    {
        private readonly HttpClient httpClient;
        private readonly ILogger logger;

        private readonly string interopDomain;
        private readonly string interopSecret;

        public SharedInterop(HttpClient httpClient, ILoggerFactory loggerFactory)
        {
            this.httpClient = httpClient;
            logger = loggerFactory.CreateLogger("LIO");

            interopDomain = AppSettings.SharedInteropDomain;
            interopSecret = AppSettings.SharedInteropSecret;
        }

        /// <summary>
        /// Runs an interop command.
        /// </summary>
        /// <param name="method">The HTTP method.</param>
        /// <param name="command">The command to run.</param>
        /// <param name="postObject">Any data to send.</param>
        private async Task<string> runCommand(HttpMethod method, string command, dynamic? postObject = null, CancellationToken cancellationToken = default)
        {
            int retryCount = 3;

            retry:

            long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string url = $"{interopDomain}/_lio/{command}{(command.Contains('?') ? "&" : "?")}timestamp={time}";

            string? serialisedPostObject;

            switch (postObject)
            {
                case null:
                    serialisedPostObject = null;
                    break;

                case string:
                    serialisedPostObject = postObject;
                    break;

                default:
                    serialisedPostObject = JsonSerializer.Serialize(postObject);
                    break;
            }

            logger.LogDebug("Performing LIO request to {method} {url} (params: {params})", method, url, serialisedPostObject);

            try
            {
                string signature = hmacEncode(url, Encoding.UTF8.GetBytes(interopSecret));

                var httpRequestMessage = new HttpRequestMessage
                {
                    RequestUri = new Uri(url),
                    Method = method,
                    Headers =
                    {
                        { "X-LIO-Signature", signature },
                        { "Accept", "application/json" },
                    },
                };

                if (serialisedPostObject != null)
                {
                    httpRequestMessage.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(serialisedPostObject));
                    httpRequestMessage.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                }

                using var response = await httpClient.SendAsync(httpRequestMessage, cancellationToken);

                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync(cancellationToken);

                throw await SharedInteropRequestFailedException.Create(url, response);
            }
            catch (Exception e)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool allowRetry = true;

                if (e is SharedInteropRequestFailedException interopException)
                {
                    switch (interopException.StatusCode)
                    {
                        // Allow retry for potentially relevant 5XX responses.
                        case HttpStatusCode.InternalServerError:
                        case HttpStatusCode.BadGateway:
                        case HttpStatusCode.ServiceUnavailable:
                        case HttpStatusCode.GatewayTimeout:
                            break;

                        default:
                            allowRetry = false;
                            break;
                    }
                }

                if (allowRetry && retryCount-- > 0)
                {
                    logger.LogError(e, "Shared interop request to {url} failed, retrying ({retries} remaining)", url, retryCount);
                    await Task.Delay(1000, cancellationToken);
                    goto retry;
                }

                logger.LogError(e, "Shared interop request to {url} failed", url);
                throw;
            }
        }

        private static string hmacEncode(string input, byte[] key)
        {
            byte[] byteArray = Encoding.ASCII.GetBytes(input);

            byte[] hashArray = HMACSHA1.HashData(key, byteArray);
            return hashArray.Aggregate(string.Empty, (s, e) => s + $"{e:x2}", s => s);
        }

        // Methods below purposefully async-await on `runCommand()` calls rather than directly returning the underlying calls.
        // This is done for better readability of exception stacks. Directly returning the tasks elides the name of the proxying method.

        public async Task<long> CreateRoomAsync(int hostUserId, MultiplayerRoom room, bool tournamentMode)
        {
            return long.Parse(await runCommand(HttpMethod.Post, "multiplayer/rooms", Newtonsoft.Json.JsonConvert.SerializeObject(new SharedInteropRoom(room)
            {
                HostUserId = hostUserId,
                TournamentMode = tournamentMode
            })));
        }

        public async Task<osu.Server.Spectator.Database.Models.matchmaking_pool[]> GetMatchmakingPoolsAsync()
            => Newtonsoft.Json.JsonConvert.DeserializeObject<osu.Server.Spectator.Database.Models.matchmaking_pool[]>(
                await runCommand(HttpMethod.Get, "matchmaking/pools")) ?? [];

        public async Task<osu.Server.Spectator.Database.Models.database_beatmap[]> GetMatchmakingBeatmapsAsync(int rulesetId, int variant, uint poolId)
            => Newtonsoft.Json.JsonConvert.DeserializeObject<osu.Server.Spectator.Database.Models.database_beatmap[]>(
                await runCommand(HttpMethod.Get, $"matchmaking/beatmaps?ruleset_id={rulesetId}&variant={variant}&pool_id={poolId}")) ?? [];

        public async Task<bool> IsMatchmakingPoolAvailableAsync(uint poolId)
            => Newtonsoft.Json.JsonConvert.DeserializeObject<bool>(await runCommand(HttpMethod.Get, $"matchmaking/pools/{poolId}/available"));

        public async Task AddUserToRoomAsync(int userId, long roomId, string password)
        {
            await runCommand(HttpMethod.Put, $"multiplayer/rooms/{roomId}/users/{userId}", new
            {
                password = password
            });
        }

        public async Task<RankedDodgeStatus> GetRankedDodgeStatusAsync(int userId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return Newtonsoft.Json.JsonConvert.DeserializeObject<RankedDodgeStatus>(
                await runCommand(HttpMethod.Get, $"ranked-dodge/{userId}", cancellationToken: timeout.Token))
                   ?? throw new InvalidOperationException("Missing Ranked penalty status.");
        }

        public async Task<RankedDodgeStatus> RegisterRankedDodgeAsync(long roomId, int userId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return Newtonsoft.Json.JsonConvert.DeserializeObject<RankedDodgeStatus>(
                await runCommand(HttpMethod.Post, "ranked-dodge", new { room_id = roomId, user_id = userId, allow_missing_room = true }, timeout.Token))
                   ?? throw new InvalidOperationException("Missing Ranked penalty status.");
        }

        public async Task RemoveUserFromRoomAsync(int userId, long roomId)
        {
            await runCommand(HttpMethod.Delete, $"multiplayer/rooms/{roomId}/users/{userId}");
        }

        public async Task<RankedPartyReservation> ReserveRankedPartyAsync(int userId, int poolId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return Newtonsoft.Json.JsonConvert.DeserializeObject<RankedPartyReservation>(
                await runCommand(HttpMethod.Post, $"parties/{userId}/reserve", new { pool_id = poolId, request_id = Guid.NewGuid().ToString() }, timeout.Token))
                   ?? throw new InvalidOperationException("Missing party reservation.");
        }

        public async Task RenewPartyReservationAsync(string reservationId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await runCommand(HttpMethod.Post, $"party-reservations/{Uri.EscapeDataString(reservationId)}/renew", cancellationToken: timeout.Token);
        }

        public async Task ReleasePartyReservationAsync(string reservationId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await runCommand(HttpMethod.Delete, $"party-reservations/{Uri.EscapeDataString(reservationId)}", cancellationToken: timeout.Token);
        }

        public async Task EnsureBeatmapPresentAsync(int beatmapId)
        {
            var payload = new { beatmap_id = beatmapId };
            await runCommand(HttpMethod.Post, "beatmaps/ensure", payload);
        }

        public async Task UploadReplayAsync(int scoreInfoUserID, long scoreInfoOnlineID, int scoreInfoBeatmapId, MemoryStream outStream)
        {
            var payload = new { score_id = scoreInfoOnlineID, user_id = scoreInfoUserID, beatmap_id = scoreInfoBeatmapId, mreplay = Convert.ToBase64String(outStream.ToArray()) };
            await runCommand(HttpMethod.Post, "scores/replay", payload);
        }

        public async Task<Dictionary<string, RulesetVersionEntry>> GetRulesetHashesAsync()
        {
            string result = await runCommand(HttpMethod.Get, "ruleset-hashes");
            return JsonSerializer.Deserialize<Dictionary<string, RulesetVersionEntry>>(result) ?? new Dictionary<string, RulesetVersionEntry>();
        }

        /// <summary>
        /// A special <see cref="Room"/> that can be serialised with Newtonsoft.Json to create rooms hosted by a given <see cref="HostUserId">user</see>.
        /// </summary>
        private class SharedInteropRoom : Room
        {
            /// <summary>
            /// The ID of the user to host the room.
            /// </summary>
            [Newtonsoft.Json.JsonProperty("user_id")]
            public required int HostUserId { get; init; }

            /// <summary>
            /// In "tournament mode" the <see cref="HostUserId"/> can have more than one realtime room open at a time.
            /// </summary>
            [Newtonsoft.Json.JsonProperty("tournament_mode")]
            public required bool TournamentMode { get; init; }

            /// <summary>
            /// Creates a <see cref="Room"/> from a <see cref="MultiplayerRoom"/>.
            /// </summary>
            public SharedInteropRoom(MultiplayerRoom room)
                : base(room)
            {
            }
        }

        [Serializable]
        private class SharedInteropRequestFailedException : HubException
        {
            public readonly HttpStatusCode StatusCode;

            private SharedInteropRequestFailedException(HttpStatusCode statusCode, string message, Exception innerException)
                : base(message, innerException)
            {
                StatusCode = statusCode;
            }

            public static async Task<SharedInteropRequestFailedException> Create(string url, HttpResponseMessage response)
            {
                string errorMessage = $"{(int)response.StatusCode}: {response.ReasonPhrase}";

                try
                {
                    APIErrorMessage? apiError = await JsonSerializer.DeserializeAsync<APIErrorMessage>(await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
                    if (!string.IsNullOrEmpty(apiError?.Error))
                        errorMessage = apiError.Error;
                }
                catch
                {
                }

                // Outer exception message is serialised to clients, inner exception is logged to the server and NOT serialised to the client.
                return new SharedInteropRequestFailedException(response.StatusCode, errorMessage, new Exception(await response.Content.ReadAsStringAsync()));
            }

            [Serializable]
            private class APIErrorMessage
            {
                [JsonPropertyName("error")]
                public string Error { get; set; } = string.Empty;
            }
        }
    }
}
