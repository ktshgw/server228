#nullable enable
using System.Net.Http;
using Newtonsoft.Json;
using osu.Framework.IO.Network;
using osu.Game.Online.API;

namespace osu.Game.Rulesets.EnhancedAuth.Online;

public sealed class BeatmapModerationState
{
    [JsonProperty("allowed")]
    public bool Allowed { get; set; }

    [JsonProperty("beatmapset_id")]
    public int BeatmapSetId { get; set; }

    [JsonProperty("beatmap_id")]
    public int? BeatmapId { get; set; }

    [JsonProperty("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonProperty("status")]
    public int Status { get; set; }

    [JsonProperty("status_name")]
    public string StatusName { get; set; } = string.Empty;

    [JsonProperty("source")]
    public string Source { get; set; } = string.Empty;

    [JsonProperty("leaderboard_enabled")]
    public bool LeaderboardEnabled { get; set; }

    [JsonProperty("pp_enabled")]
    public bool PerformancePointsEnabled { get; set; }

    [JsonProperty("can_rank")]
    public bool CanRank { get; set; }

    [JsonProperty("can_unrank")]
    public bool CanUnrank { get; set; }

    [JsonProperty("can_love")]
    public bool CanLove { get; set; }

    [JsonProperty("applied_action")]
    public string? AppliedAction { get; set; }
}

public sealed class GetBeatmapModerationStateRequest : APIRequest<BeatmapModerationState>
{
    private readonly int beatmapSetId;

    public GetBeatmapModerationStateRequest(int beatmapSetId)
    {
        this.beatmapSetId = beatmapSetId;
    }

    protected override string Uri => $"{API!.Endpoints.APIUrl}/api/private/client/beatmap-ranking/{beatmapSetId}";

    protected override string Target => string.Empty;
}

public sealed class ApplyBeatmapModerationActionRequest : APIRequest<BeatmapModerationState>
{
    private readonly int beatmapSetId;
    private readonly string action;
    private readonly int? beatmapId;

    public ApplyBeatmapModerationActionRequest(int beatmapSetId, string action, int? beatmapId = null)
    {
        this.beatmapSetId = beatmapSetId;
        this.action = action;
        this.beatmapId = beatmapId;
    }

    protected override WebRequest CreateWebRequest()
    {
        WebRequest request = base.CreateWebRequest();
        request.Method = HttpMethod.Post;
        request.ContentType = "application/json";
        request.AddRaw(JsonConvert.SerializeObject(new { action, beatmap_id = beatmapId }));
        return request;
    }

    protected override string Uri => $"{API!.Endpoints.APIUrl}/api/private/client/beatmap-ranking/{beatmapSetId}";

    protected override string Target => string.Empty;
}
