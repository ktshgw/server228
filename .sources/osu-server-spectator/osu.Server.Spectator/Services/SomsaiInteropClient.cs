using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using SomsAi.Shared;

namespace osu.Server.Spectator.Services
{
    public interface ISomsaiInteropClient
    {
        Task<SomsaiRoomState> GetRoom(long roomId);
        Task<SomsaiRoomState> SendEvent(long roomId, string kind, long itemId, int[] connected, int[] ready, int[] available);
    }

    // Separate from osu!'s shared interop contract: SOMSAI is a SOMS server mode.
    public interface ISomsaiNativeRoomInterop
    {
        Task ClaimNativeRoom(long roomId, int userId);
        Task ReleaseNativeRoom(long roomId, int userId);
    }

    public sealed class SomsaiInteropClient : ISomsaiInteropClient, ISomsaiNativeRoomInterop
    {
        private static readonly HttpClient http_client = new HttpClient();

        public Task<SomsaiRoomState> GetRoom(long roomId) => request(HttpMethod.Get, $"somsai/rooms/{roomId}");

        public static Task<SomsaiBotBeatmap> GetBotBeatmap(long roomId, long itemId)
            => requestData<SomsaiBotBeatmap>(HttpMethod.Get, $"somsai/rooms/{roomId}/bot-beatmap/{itemId}", null, 30);

        public static Task<SomsaiRoomState> SubmitBotResults(long roomId, long itemId, IEnumerable<SomsaiBotScore> scores)
            => request(HttpMethod.Post, $"somsai/rooms/{roomId}/bot-results", new { playlist_item_id = itemId, scores });

        public Task ClaimNativeRoom(long roomId, int userId) => request(HttpMethod.Post, $"somsai/native-rooms/{roomId}/users/{userId}");
        public Task ReleaseNativeRoom(long roomId, int userId) => request(HttpMethod.Delete, $"somsai/native-rooms/{roomId}/users/{userId}");

        public Task<SomsaiRoomState> SendEvent(long roomId, string kind, long itemId, int[] connected, int[] ready, int[] available)
            => request(HttpMethod.Post, $"somsai/rooms/{roomId}/events", new { @event = kind, playlist_item_id = itemId, connected, ready, available });

        private static Task<SomsaiRoomState> request(HttpMethod method, string path, object? body = null)
            => requestData<SomsaiRoomState>(method, path, body);

        private static async Task<T> requestData<T>(HttpMethod method, string path, object? body = null, int timeoutSeconds = 3) where T : class
        {
            string url = $"{AppSettings.SharedInteropDomain.TrimEnd('/')}/_lio/{path}?timestamp={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            string signature = Convert.ToHexString(HMACSHA1.HashData(Encoding.UTF8.GetBytes(AppSettings.SharedInteropSecret), Encoding.ASCII.GetBytes(url))).ToLowerInvariant();
            using var message = new HttpRequestMessage(method, url);
            message.Headers.Add("X-LIO-Signature", signature);
            if (body != null)
                message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await http_client.SendAsync(message, timeout.Token);
            response.EnsureSuccessStatusCode();
            return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(timeout.Token))
                   ?? throw new InvalidOperationException("Empty SOMSAI room response.");
        }
    }

    public sealed class SomsaiRoomState
    {
        [JsonPropertyName("managed")]
        public bool Managed { get; set; }

        [JsonPropertyName("stage")]
        public string Stage { get; set; } = string.Empty;

        [JsonPropertyName("roster")]
        public int[][] Roster { get; set; } = [];

        [JsonPropertyName("bots")]
        public SomsaiBotIdentity[] Bots { get; set; } = [];

        [JsonPropertyName("playlist_item_id")]
        public long PlaylistItemId { get; set; }

        [JsonPropertyName("start_allowed")]
        public bool StartAllowed { get; set; }

        [JsonPropertyName("force_start")]
        public bool ForceStart { get; set; }

        public bool Finished => Stage is "ended" or "cancelled";
        public bool Contains(int userId) => Managed && !Finished && Roster.Any(team => team.Contains(userId));
    }

    public sealed class SomsaiBotIdentity
    {
        [JsonPropertyName("user_id")] public int UserId { get; set; }
        [JsonPropertyName("seed")] public int Seed { get; set; }
        [JsonPropertyName("level")] public string Level { get; set; } = "medium";
        [JsonPropertyName("global_rank")] public int? GlobalRank { get; set; }
        [JsonPropertyName("skill_profile")] public BotSkillProfile? SkillProfile { get; set; }
    }

    public sealed class SomsaiBotBeatmap
    {
        [JsonPropertyName("raw")] public string Raw { get; set; } = "";
        [JsonPropertyName("checksum")] public string Checksum { get; set; } = "";
    }

    public sealed class SomsaiBotScore
    {
        [JsonPropertyName("user_id")] public int UserId { get; set; }
        [JsonPropertyName("score")] public long Score { get; set; }
        [JsonPropertyName("accuracy")] public double Accuracy { get; set; }
        [JsonPropertyName("max_combo")] public int MaxCombo { get; set; }
        [JsonPropertyName("statistics")] public Dictionary<string, int> Statistics { get; set; } = new();
        [JsonPropertyName("maximum_statistics")] public Dictionary<string, int> MaximumStatistics { get; set; } = new();
        [JsonPropertyName("rank")] public string Rank { get; set; } = "A";
        [JsonPropertyName("passed")] public bool Passed { get; set; }
    }
}
