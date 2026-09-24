#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Framework.IO.Network;
using osu.Game.Online.API;

namespace osu.Game.Rulesets.EnhancedAuth.Online;

public sealed class SomsAiState
{
    [JsonProperty("ratings")] public Dictionary<string, SomsAiRating> Ratings { get; set; } = new();
    [JsonProperty("party")] public SomsParty? Party { get; set; }
    [JsonProperty("invites")] public List<SomsPartyInvite> Invites { get; set; } = new();
    [JsonProperty("queue")] public SomsAiQueue? Queue { get; set; }
    [JsonProperty("match")] public SomsAiMatch? Match { get; set; }
    [JsonProperty("customs")] public List<SomsAiCustom> Customs { get; set; } = new();
    [JsonProperty("pools")] public List<SomsAiPool> Pools { get; set; } = new();
    [JsonProperty("recent_matches")] public List<SomsAiRecentMatch> RecentMatches { get; set; } = new();
    [JsonProperty("pool_ranks")] public List<string> PoolRanks { get; set; } = new();
}

public sealed class SomsAiRecentMatch
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("format")] public string Format { get; set; } = "";
    [JsonProperty("ranked")] public bool Ranked { get; set; }
    [JsonProperty("outcome")] public string Outcome { get; set; } = "draw";
    [JsonProperty("wins")] public int[] Wins { get; set; } = new int[2];
    [JsonProperty("local_team_id")] public int? LocalTeamId { get; set; }
    [JsonProperty("opponents")] public List<string> Opponents { get; set; } = new();
    [JsonProperty("rating_delta")] public double RatingDelta { get; set; }
    [JsonProperty("ended_at")] public DateTimeOffset? EndedAt { get; set; }
}

public sealed class SomsAiPool
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("best_of")] public int BestOf { get; set; }
    [JsonProperty("average_stars")] public double AverageStars { get; set; }
    [JsonProperty("map_count")] public int MapCount { get; set; }
}

public sealed class SomsAiRating
{
    [JsonProperty("rating")] public double Rating { get; set; }
    [JsonProperty("rank")] public int? Rank { get; set; }
    [JsonProperty("wins")] public int Wins { get; set; }
    [JsonProperty("losses")] public int Losses { get; set; }
}

public sealed class SomsPlayer
{
    [JsonProperty("ratings")] public Dictionary<string, SomsAiRating> Ratings { get; set; } = new();
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("username")] public string Username { get; set; } = "";
    [JsonProperty("avatar_url")] public string? AvatarUrl { get; set; }
    [JsonProperty("country_code")] public string CountryCode { get; set; } = "XX";
    [JsonProperty("is_bot")] public bool IsBot { get; set; }
    [JsonProperty("official_id")] public int? OfficialId { get; set; }
    [JsonProperty("official_username")] public string? OfficialUsername { get; set; }
    [JsonProperty("bot_level")] public string? BotLevel { get; set; }
    [JsonProperty("rating")] public double Rating { get; set; }
    [JsonProperty("ready")] public bool Ready { get; set; }
}

public sealed class SomsAiDataRequest : APIRequest<JObject>
{
    private readonly string path;
    private readonly JObject? body;
    public SomsAiDataRequest(string path, JObject? body = null) => (this.path, this.body) = (path, body);
    protected override string Uri => $"{API!.Endpoints.APIUrl}/api/private/somsai/{path}";
    protected override string Target => string.Empty;
    protected override WebRequest CreateWebRequest()
    {
        var request = base.CreateWebRequest();
        if (body != null)
        {
            request.Method = HttpMethod.Post;
            request.ContentType = "application/json";
            request.AddRaw(body.ToString(Formatting.None));
        }
        return request;
    }
}

public sealed class SomsParty
{
    [JsonProperty("id")] public int? Id { get; set; }
    [JsonProperty("captain_id")] public int CaptainId { get; set; }
    [JsonProperty("members")] public List<SomsPlayer> Members { get; set; } = new();
    [JsonProperty("invites")] public List<SomsPartyInvite> Invites { get; set; } = new();
    [JsonProperty("outgoing_invites")] public List<SomsPartyInvite> OutgoingInvites { get; set; } = new();
    [JsonProperty("busy")] public bool Busy { get; set; }
}

public sealed class SomsPartyInvite
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("captain")] public SomsPlayer? Captain { get; set; }
    [JsonProperty("target")] public SomsPlayer? Target { get; set; }
}

public sealed class SomsAiQueue
{
    [JsonProperty("format")] public string Format { get; set; } = "";
    [JsonProperty("joined_at")] public DateTimeOffset? JoinedAt { get; set; }
    [JsonProperty("state")] public string State { get; set; } = "";
}

public sealed class SomsAiTeam
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("captain_id")] public int? CaptainId { get; set; }
    [JsonProperty("members")] public List<SomsPlayer> Members { get; set; } = new();
}

public sealed class SomsAiSlot
{
    [JsonProperty("display_stats")] public SomsAiMapStats? DisplayStats { get; set; }
    [JsonProperty("id")] public string Id { get; set; } = "";
    [JsonProperty("label")] public string Label { get; set; } = "";
    [JsonProperty("beatmap_id")] public int BeatmapId { get; set; }
    [JsonProperty("beatmapset_id")] public int BeatmapSetId { get; set; }
    [JsonProperty("category")] public string Category { get; set; } = "";
    [JsonProperty("selected_by_team")] public int? SelectedByTeam { get; set; }
    [JsonProperty("checksum")] public string Checksum { get; set; } = "";
    [JsonProperty("artist")] public string Artist { get; set; } = "";
    [JsonProperty("title")] public string Title { get; set; } = "";
    [JsonProperty("version")] public string Version { get; set; } = "";
    [JsonProperty("stars")] public double Stars { get; set; }
    [JsonProperty("difficulty_rating")] private double fallbackStars { set { if (Stars == 0) Stars = value; } }
    [JsonProperty("name")] private string? fallbackTitle { set { if (Title.Length == 0) Title = value ?? ""; } }
    [JsonProperty("mods")] public JToken? Mods { get; set; }
    [JsonProperty("status")] public string Status { get; set; } = "available";
}

public sealed class SomsAiMapStats
{
    [JsonProperty("stars")] public double Stars { get; set; }
    [JsonProperty("bpm")] public double Bpm { get; set; }
    [JsonProperty("cs")] public double Cs { get; set; }
    [JsonProperty("ar")] public double Ar { get; set; }
    [JsonProperty("od")] public double Od { get; set; }
    [JsonProperty("hp")] public double Hp { get; set; }
    [JsonProperty("length")] public double Length { get; set; }
}

public sealed class SomsAiMatch
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("owner_id")] public int? OwnerId { get; set; }
    [JsonProperty("ruleset_id")] public int RulesetId { get; set; }
    [JsonProperty("variant_id")] public int VariantId { get; set; }
    [JsonProperty("revision")] public int Revision { get; set; }
    [JsonProperty("format")] public string Format { get; set; } = "";
    [JsonProperty("ranked")] public bool Ranked { get; set; }
    [JsonProperty("stage")] public string Stage { get; set; } = "waiting";
    [JsonProperty("room_id")] public long? RoomId { get; set; }
    [JsonProperty("password")] public string? Password { get; set; }
    [JsonProperty("teams")] public List<SomsAiTeam> Teams { get; set; } = new();
    [JsonProperty("wins")] public int[] Wins { get; set; } = new int[2];
    [JsonProperty("best_of")] public int BestOf { get; set; } = 7;
    [JsonProperty("pool_selected")] public bool PoolSelected { get; set; } = true;
    [JsonProperty("pool_name")] public string PoolName { get; set; } = "";
    [JsonProperty("pool_candidates")] public List<SomsAiPool> PoolCandidates { get; set; } = new();
    [JsonProperty("pool_votes")] public Dictionary<int, int> PoolVotes { get; set; } = new();
    [JsonProperty("target_mmr")] public double? TargetMmr { get; set; }
    [JsonProperty("target_rank")] public string? TargetRank { get; set; }
    [JsonProperty("turn_user_id")] public int? TurnUserId { get; set; }
    [JsonProperty("deadline")] public DateTimeOffset? Deadline { get; set; }
    [JsonProperty("slots")] public List<SomsAiSlot> Slots { get; set; } = new();
    [JsonProperty("history")] public List<JObject> History { get; set; } = new();
    [JsonProperty("draft_history")] public List<JObject> DraftHistory { get; set; } = new();
    [JsonProperty("map_slot")] public JToken? MapSlot { get; set; }
    [JsonProperty("reason")] public string Reason { get; set; } = "";
    [JsonProperty("winner_team_id")] public int? WinnerTeamId { get; set; }
    [JsonProperty("rating_changes")] public List<SomsAiRatingChange> RatingChanges { get; set; } = new();
    public bool IsFinished => Stage is "ended" or "cancelled";
}

public sealed class SomsAiRatingChange
{
    [JsonProperty("user_id")] public int UserId { get; set; }
    [JsonProperty("before")] public double Before { get; set; }
    [JsonProperty("after")] public double After { get; set; }
    [JsonProperty("delta")] public double Delta { get; set; }
    [JsonProperty("impact")] public int Impact { get; set; }
}

public sealed class SomsAiCustom
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("format")] public string Format { get; set; } = "";
    [JsonProperty("participants")] public int Participants { get; set; }
    [JsonProperty("capacity")] public int Capacity { get; set; }
    [JsonProperty("target_mmr")] public double? TargetMmr { get; set; }
    [JsonProperty("target_rank")] public string? TargetRank { get; set; }
    [JsonProperty("teams")] public int[] Teams { get; set; } = new int[2];
}

public sealed class GetSomsAiStateRequest : APIRequest<SomsAiState>
{
    private readonly int rulesetId;
    private readonly int variantId;
    public GetSomsAiStateRequest(int rulesetId, int variantId = 0) => (this.rulesetId, this.variantId) = (rulesetId, variantId);
    protected override string Uri => $"{API!.Endpoints.APIUrl}/api/private/somsai/state?ruleset_id={rulesetId}&variant_id={variantId}";
    protected override string Target => string.Empty;
}

public sealed class ApplySomsAiActionRequest : APIRequest<JObject>
{
    private readonly JObject body;
    public ApplySomsAiActionRequest(JObject body) => this.body = body;
    protected override string Uri => $"{API!.Endpoints.APIUrl}/api/private/somsai/actions";
    protected override string Target => string.Empty;
    protected override WebRequest CreateWebRequest()
    {
        WebRequest request = base.CreateWebRequest();
        if (body.Value<string>("action") == "custom_create") request.Timeout = 45000;
        request.Method = HttpMethod.Post;
        request.ContentType = "application/json";
        request.AddRaw(body.ToString(Formatting.None));
        return request;
    }
}
