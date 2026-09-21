#nullable enable
using System;
using System.IO;
using System.Linq;
using System.ComponentModel;
using HarmonyLib;
using Newtonsoft.Json;
using osu.Framework.Bindables;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Configuration;

namespace osu.Game.Rulesets.EnhancedAuth.Configuration;

public enum SomsSliderMissDisplay
{
    [Description("Нет")] None,
    [Description("50 и 100")] Judgements,
    [Description("По умолчанию")] Default,
}

// Values used by the previous dropdown, retained only for migration of local JSON.
internal enum SomsLeaderboardCollapse
{
    Skin,
    Collapse,
    Expand,
}

/// <summary>Local preferences separate from authentication and from editable skin files.</summary>
public sealed class SomsClientPreferences
{
    public const int SkinSlotCount = 5;
    public const string FileName = "soms_client_preferences.json";

    private static readonly Lazy<SomsClientPreferences> instance = new(create);
    public static SomsClientPreferences Instance => instance.Value;
    public static bool Enabled => GlobalConfigManager.Patched && !GlobalConfigManager.Config.NonG0V0Server;

    public readonly Bindable<bool> UseSkinLeaderboardCollapse = new(true);
    public readonly Bindable<bool> ShowSliderEndMiss = new(true);
    public readonly Bindable<SomsSliderMissDisplay> SliderMissDisplay = new(SomsSliderMissDisplay.Default);
    public readonly Bindable<bool> DrawFollowPoints = new(true);
    public readonly Bindable<bool> ForceSmoothCursorTrail = new(false);
    public readonly Bindable<bool> SuddenDeathRestart = new(false);
    public readonly Bindable<bool> ShowMapPP = new(false);
    public readonly Bindable<bool> EnhancedVolume = new(false);
    public readonly Bindable<bool> StealthMode = new(false);
    public readonly Bindable<bool> ShowSliderFollowCircle = new(true);
    public readonly Bindable<bool> LegacyInterface = new(false);
    public readonly Bindable<bool> SeparateInterfaceScales = new(false);
    public readonly BindableFloat MenuInterfaceScale = new(1) { MinValue = 0.1f, MaxValue = 2 };
    public readonly BindableFloat GameplayInterfaceScale = new(1) { MinValue = 0.1f, MaxValue = 2 };
    public readonly Bindable<Guid>[] SkinSlots = Enumerable.Range(0, SkinSlotCount).Select(_ => new Bindable<Guid>(Guid.Empty)).ToArray();
    public readonly Bindable<bool> ModSkinsEnabled = new(false);
    public readonly Bindable<bool> TeamVsInLeaderboards = new(false);
    public readonly Bindable<Guid>[] ModSkins = Enumerable.Range(0, 5).Select(_ => new Bindable<Guid>(Guid.Empty)).ToArray();

    private readonly string filePath;
    public string DirectoryPath => Path.GetDirectoryName(filePath)!;
    private readonly object fileLock = new();

    private static SomsClientPreferences create()
    {
        var config = Traverse.Create(GlobalConfigManager.GameBase).Property("LocalConfig").GetValue<OsuConfigManager>();
        var storage = Traverse.Create(config).Field("storage").GetValue<Storage>();
        return new SomsClientPreferences(storage.GetFullPath(FileName));
    }

    public SomsClientPreferences(string filePath)
    {
        this.filePath = filePath;
        try
        {
            if (File.Exists(filePath))
            {
                var data = JsonConvert.DeserializeObject<PreferencesData>(File.ReadAllText(filePath));
                if (data != null)
                {
                    UseSkinLeaderboardCollapse.Value = data.UseSkinLeaderboardCollapse
                        ?? data.LeaderboardCollapse != SomsLeaderboardCollapse.Expand;
                    SliderMissDisplay.Value = data.SliderMissDisplay is { } display && Enum.IsDefined(display)
                        ? display : data.ShowSliderEndMiss ? SomsSliderMissDisplay.Default : SomsSliderMissDisplay.None;
                    ShowSliderEndMiss.Value = SliderMissDisplay.Value != SomsSliderMissDisplay.None;
                    DrawFollowPoints.Value = data.DrawFollowPoints;
                    ForceSmoothCursorTrail.Value = data.ForceSmoothCursorTrail;
                    SuddenDeathRestart.Value = data.SuddenDeathRestart;
                    ShowMapPP.Value = data.ShowMapPP;
                    EnhancedVolume.Value = data.EnhancedVolume;
                    StealthMode.Value = data.StealthMode;
                    ShowSliderFollowCircle.Value = data.ShowSliderFollowCircle;
                    LegacyInterface.Value = data.LegacyInterface;
                    SeparateInterfaceScales.Value = data.SeparateInterfaceScales;
                    MenuInterfaceScale.Value = finiteScale(data.MenuInterfaceScale);
                    GameplayInterfaceScale.Value = finiteScale(data.GameplayInterfaceScale);
                    ModSkinsEnabled.Value = data.ModSkinsEnabled;
                    TeamVsInLeaderboards.Value = data.TeamVsInLeaderboards;
                    for (int i = 0; i < Math.Min(data.ModSkins?.Length ?? 0, ModSkins.Length); i++)
                        ModSkins[i].Value = data.ModSkins![i];
                    for (int i = 0; i < Math.Min(data.SkinSlots?.Length ?? 0, SkinSlotCount); i++)
                        SkinSlots[i].Value = data.SkinSlots![i];
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Error(e, "[SOMS!] Cannot load client preferences.");
        }

        UseSkinLeaderboardCollapse.BindValueChanged(_ => save());
        SliderMissDisplay.BindValueChanged(value => { ShowSliderEndMiss.Value = value.NewValue != SomsSliderMissDisplay.None; save(); });
        // Retain the old public binding for existing visibility components and saved preferences.
        ShowSliderEndMiss.BindValueChanged(value =>
        {
            if (!value.NewValue) SliderMissDisplay.Value = SomsSliderMissDisplay.None;
            else if (SliderMissDisplay.Value == SomsSliderMissDisplay.None) SliderMissDisplay.Value = SomsSliderMissDisplay.Default;
            save();
        });
        DrawFollowPoints.BindValueChanged(_ => save());
        ForceSmoothCursorTrail.BindValueChanged(_ => save());
        SuddenDeathRestart.BindValueChanged(_ => save());
        ShowMapPP.BindValueChanged(_ => save());
        EnhancedVolume.BindValueChanged(_ => save());
        StealthMode.BindValueChanged(_ => save());
        ShowSliderFollowCircle.BindValueChanged(_ => save());
        LegacyInterface.BindValueChanged(_ => save());
        SeparateInterfaceScales.BindValueChanged(_ => save());
        MenuInterfaceScale.BindValueChanged(_ => save());
        GameplayInterfaceScale.BindValueChanged(_ => save());
        foreach (var slot in SkinSlots)
            slot.BindValueChanged(_ => save());
        ModSkinsEnabled.BindValueChanged(_ => save());
        TeamVsInLeaderboards.BindValueChanged(_ => save());
        foreach (var slot in ModSkins)
            slot.BindValueChanged(_ => save());
    }

    private static float finiteScale(float value) => float.IsFinite(value) ? Math.Clamp(value, 0.1f, 2) : 1;

    private void save()
    {
        lock (fileLock)
        {
            try
            {
                string temporary = filePath + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(new PreferencesData
                {
                    UseSkinLeaderboardCollapse = UseSkinLeaderboardCollapse.Value,
                    ShowSliderEndMiss = ShowSliderEndMiss.Value,
                    SliderMissDisplay = SliderMissDisplay.Value,
                    DrawFollowPoints = DrawFollowPoints.Value,
                    ForceSmoothCursorTrail = ForceSmoothCursorTrail.Value,
                    SuddenDeathRestart = SuddenDeathRestart.Value,
                    ShowMapPP = ShowMapPP.Value,
                    EnhancedVolume = EnhancedVolume.Value,
                    StealthMode = StealthMode.Value,
                    ShowSliderFollowCircle = ShowSliderFollowCircle.Value,
                    LegacyInterface = LegacyInterface.Value,
                    SeparateInterfaceScales = SeparateInterfaceScales.Value,
                    MenuInterfaceScale = MenuInterfaceScale.Value,
                    GameplayInterfaceScale = GameplayInterfaceScale.Value,
                    SkinSlots = SkinSlots.Select(slot => slot.Value).ToArray(),
                    ModSkinsEnabled = ModSkinsEnabled.Value,
                    TeamVsInLeaderboards = TeamVsInLeaderboards.Value,
                    ModSkins = ModSkins.Select(slot => slot.Value).ToArray(),
                }, Formatting.Indented));
                File.Move(temporary, filePath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Logger.Error(e, "[SOMS!] Cannot save client preferences.");
            }
        }
    }

    private sealed class PreferencesData
    {
        public bool? UseSkinLeaderboardCollapse { get; set; }
        public bool ShowSliderEndMiss { get; set; } = true;
        public SomsSliderMissDisplay? SliderMissDisplay { get; set; }
        public bool DrawFollowPoints { get; set; } = true;
        public bool ForceSmoothCursorTrail { get; set; }
        public bool SuddenDeathRestart { get; set; }
        public bool ShowMapPP { get; set; }
        public bool EnhancedVolume { get; set; }
        public bool StealthMode { get; set; }
        public bool ShowSliderFollowCircle { get; set; } = true;
        public bool LegacyInterface { get; set; }
        public bool SeparateInterfaceScales { get; set; }
        public float MenuInterfaceScale { get; set; } = 1;
        public float GameplayInterfaceScale { get; set; } = 1;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public SomsLeaderboardCollapse? LeaderboardCollapse { get; set; }
        public Guid[]? SkinSlots { get; set; }
        public bool ModSkinsEnabled { get; set; }
        public bool TeamVsInLeaderboards { get; set; }
        public Guid[]? ModSkins { get; set; }
    }
}
