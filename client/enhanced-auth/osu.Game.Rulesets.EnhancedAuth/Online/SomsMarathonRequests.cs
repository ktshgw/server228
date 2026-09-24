#nullable enable
using System.Collections.Generic;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Framework.IO.Network;
using osu.Game.Online.API;

namespace osu.Game.Rulesets.EnhancedAuth.Online;

public sealed class SomsMarathonSegment
{
    [JsonProperty("beatmap_id")] public int BeatmapId { get; set; }
    [JsonProperty("beatmapset_id")] public int BeatmapSetId { get; set; }
    [JsonProperty("checksum")] public string Checksum { get; set; } = "";
    [JsonProperty("title")] public string Title { get; set; } = "";
    [JsonProperty("start_ms")] public int StartMs { get; set; }
    [JsonProperty("end_ms")] public int EndMs { get; set; }
}

public sealed class SomsMarathonDefinition
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = "Songs compilation";
    [JsonProperty("ruleset_id")] public int RulesetId { get; set; }
    [JsonProperty("compiler_version")] public int CompilerVersion { get; set; } = 1;
    [JsonProperty("owner_id")] public int OwnerId { get; set; }
    [JsonProperty("owner_name")] public string OwnerName { get; set; } = "";
    [JsonProperty("segments")] public List<SomsMarathonSegment> Segments { get; set; } = new();
}

public sealed class SomsMarathonRequest : APIRequest<JObject>
{
    private readonly string path;
    private readonly JObject? body;
    private readonly HttpMethod method;
    public SomsMarathonRequest(string path = "", JObject? body = null, HttpMethod? method = null)
        => (this.path, this.body, this.method) = (path, body, method ?? (body == null ? HttpMethod.Get : HttpMethod.Post));
    protected override string Uri => $"{API!.Endpoints.APIUrl}/api/private/marathons{path}";
    protected override string Target => string.Empty;
    protected override WebRequest CreateWebRequest()
    {
        var request = base.CreateWebRequest();
        request.Method = method;
        if (body != null) { request.ContentType = "application/json"; request.AddRaw(body.ToString(Formatting.None)); }
        return request;
    }
}
