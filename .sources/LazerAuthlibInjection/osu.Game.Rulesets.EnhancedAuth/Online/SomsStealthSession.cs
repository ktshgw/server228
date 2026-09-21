#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.IO.Network;
using osu.Game.Online;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Patches;

namespace osu.Game.Rulesets.EnhancedAuth.Online;

public sealed class SomsStealthIdentity
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("username")] public string Username { get; set; } = "";
    [JsonProperty("avatar_url")] public string AvatarUrl { get; set; } = "";
    [JsonProperty("pp")] public double PP { get; set; }
    [JsonProperty("global_rank")] public int GlobalRank { get; set; }
    [JsonProperty("country_code")] public string CountryCode { get; set; } = "";
    [JsonProperty("country_rank")] public int? CountryRank { get; set; }
}

public sealed class SomsStealthResponse
{
    [JsonProperty("candidates")] public List<SomsStealthIdentity> Candidates { get; set; } = new();
    [JsonProperty("global_rank")] public int GlobalRank { get; set; }
    [JsonProperty("estimated")] public bool Estimated { get; set; }
    [JsonProperty("country_code")] public string? CountryCode { get; set; }
    [JsonProperty("country_rank")] public int? CountryRank { get; set; }
    [JsonProperty("country_rank_estimated")] public bool CountryRankEstimated { get; set; }
}

public sealed class GetSomsStealthRequest(string mode, double pp, string? country = null) : APIRequest<SomsStealthResponse>
{
    protected override string Target => string.Empty;
    protected override string Uri => $"{API!.Endpoints.APIUrl}/api/private/stealth/nearby?mode={mode}&pp={pp.ToString("0.00", CultureInfo.InvariantCulture)}"
                                    + (string.IsNullOrEmpty(country) ? "" : "&country=" + System.Uri.EscapeDataString(country));
    protected override WebRequest CreateWebRequest()
    {
        var request = base.CreateWebRequest();
        request.Timeout = 60000;
        return request;
    }
}

/// <summary>Session-only display identity. Never replaces API.LocalUser, IDs, credentials or cached statistics.</summary>
public sealed partial class SomsStealthSession : Component
{
    public static SomsStealthSession? Current { get; private set; }
    public readonly Bindable<string> Status = new("Stealth mode выключен.");
    public readonly Bindable<bool> Busy = new(false);
    public SomsStealthIdentity? Identity { get; private set; }
    public int? DisplayRank { get; private set; }
    public int? DisplayCountryRank { get; private set; }
    public bool CountryRankEstimated { get; private set; }
    public bool Active => enabled?.Value == true && Identity != null && api.IsLoggedIn && SomsClientPreferences.Enabled;
    public APIUser? RealUser => api.LocalUser.Value;
    public string Mode => normalMode(ruleset?.Value.ShortName);
    internal void Enqueue(Action action) => Schedule(() => { if (!disposed) action(); });

    private IAPIProvider api = null!;
    private LocalUserStatisticsProvider statistics = null!;
    private IBindable<RulesetInfo>? ruleset;
    private IBindable<APIUser>? user;
    private IBindable<bool>? enabled;
    private GetSomsStealthRequest? pending;
    private int generation;
    private int previousIdentity;
    private double nextCheck, lastPP = double.NaN;
    private double rankPP;
    private string rankMode = "";
    private string lastMode = "";
    private bool needsIdentity;
    private bool disposed;

    [BackgroundDependencyLoader]
    private void load(IAPIProvider api, LocalUserStatisticsProvider statistics, IBindable<RulesetInfo> ruleset)
    {
        this.api = api;
        this.statistics = statistics;
        this.ruleset = ruleset.GetBoundCopy();
        user = api.LocalUser.GetBoundCopy();
        enabled = SomsClientPreferences.Instance.StealthMode.GetBoundCopy();
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        Current = this;
        enabled!.BindValueChanged(_ => Reroll(), true);
        user!.BindValueChanged(_ => { lastPP = double.NaN; nextCheck = 0; SomsStealthPresentation.Refresh(); });
    }

    public void Reroll()
    {
        cancel();
        previousIdentity = Identity?.Id ?? previousIdentity;
        Identity = null;
        DisplayRank = null;
        DisplayCountryRank = null;
        CountryRankEstimated = false;
        needsIdentity = enabled?.Value == true;
        lastPP = double.NaN;
        nextCheck = 0;
        Status.Value = needsIdentity ? "Ожидание PP аккаунта…" : "Stealth mode выключен.";
        SomsStealthPresentation.Refresh();
    }

    protected override void Update()
    {
        base.Update();
        if (enabled?.Value != true || !api.IsLoggedIn || Clock.CurrentTime < nextCheck) return;
        nextCheck = Clock.CurrentTime + 500;
        var stats = ruleset?.Value is { } r ? statistics.GetStatisticsFor(r) : null;
        if (stats?.PP is not { } pp) return;
        double target = Math.Max(0, (double)pp);
        string mode = Mode;
        if (!needsIdentity && Identity == null) return; // A failed roll is retried only by an explicit roll.
        if (Math.Abs(target - lastPP) < 0.005 && mode == lastMode) return;
        cancel();
        int revision = generation;
        lastPP = target;
        lastMode = mode;
        bool selecting = needsIdentity;
        Busy.Value = true;
        Status.Value = selecting ? "Подбор игрока с близким PP…" : "Обновление ранга…";
        pending = new GetSomsStealthRequest(mode, target, selecting ? null : Identity?.CountryCode);
        pending.Success += result => Schedule(() =>
        {
            if (disposed || revision != generation || enabled?.Value != true) return;
            Busy.Value = false;
            pending = null;
            if (selecting)
            {
                var candidates = result.Candidates.Where(c => c.Id > 1 && c.GlobalRank > 0 && !string.IsNullOrWhiteSpace(c.Username)).ToArray();
                var alternatives = candidates.Where(c => c.Id != previousIdentity).ToArray();
                if (alternatives.Length > 0) candidates = alternatives;
                if (candidates.Length == 0) { failed(); return; }
                Identity = candidates[Random.Shared.Next(candidates.Length)];
                needsIdentity = false;
                DisplayRank = Identity.GlobalRank;
                DisplayCountryRank = Identity.CountryRank is > 0 ? Identity.CountryRank : null;
                // Older official ranking responses can omit country_rank. Keep the
                // selected identity and fetch its country once, without another roll.
                if (DisplayCountryRank == null && !string.IsNullOrEmpty(Identity.CountryCode))
                {
                    lastPP = double.NaN;
                    nextCheck = Clock.CurrentTime + 2100;
                }
            }
            else
            {
                int updated = Math.Max(1, result.GlobalRank);
                // Country samples only estimate the rank. Small sampling differences
                // must never make a PP gain look like a rank loss (or vice versa).
                if (rankMode == mode && DisplayRank is { } before)
                    updated = target > rankPP ? Math.Min(before, updated) : target < rankPP ? Math.Max(before, updated) : before;
                DisplayRank = updated;
                if (string.Equals(result.CountryCode, Identity!.CountryCode, StringComparison.OrdinalIgnoreCase)
                    && result.CountryRank is > 0)
                {
                    int countryRank = result.CountryRank.Value;
                    if (rankMode == mode && DisplayCountryRank is { } previous)
                        countryRank = target > rankPP ? Math.Min(previous, countryRank) : target < rankPP ? Math.Max(previous, countryRank) : countryRank;
                    DisplayCountryRank = countryRank;
                    CountryRankEstimated = result.CountryRankEstimated;
                }
            }
            rankPP = target;
            rankMode = mode;
            Status.Value = $"{Identity!.Username} · #{DisplayRank:N0} · {target:N0} pp" +
                           (selecting ? $" (у игрока {Identity.PP:N0} pp)" : result.Estimated ? " · ранг ≈" : "");
            SomsStealthPresentation.Refresh();
        });
        pending.Failure += _ => Schedule(() => { if (!disposed && revision == generation) failed(); });
        api.Queue(pending);
    }

    private void failed()
    {
        Busy.Value = false;
        pending = null;
        needsIdentity = false;
        Status.Value = Identity == null ? "Не удалось подобрать игрока. Нажмите Reroll stealth для повтора."
            : "Ранг не обновлён: сервер недоступен. Выбранный игрок сохранён.";
        if (Identity != null) { lastPP = double.NaN; nextCheck = Clock.CurrentTime + 15000; }
    }

    private void cancel()
    {
        generation++;
        pending?.Cancel();
        pending = null;
        Busy.Value = false;
    }

    private static string normalMode(string? mode) => mode switch
    {
        "taiko" or "taikorx" => "taiko", "fruits" or "fruitsrx" => "fruits", "mania" => "mania", _ => "osu",
    };

    protected override void Dispose(bool isDisposing)
    {
        disposed = true;
        cancel();
        enabled?.UnbindAll(); user?.UnbindAll(); ruleset?.UnbindAll();
        if (ReferenceEquals(Current, this)) Current = null;
        base.Dispose(isDisposing);
    }
}
