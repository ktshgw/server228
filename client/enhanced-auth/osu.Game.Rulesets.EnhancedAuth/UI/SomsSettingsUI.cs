#nullable enable
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Screens;
using osu.Framework.Threading;
using osu.Game.Database;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Graphics;
using osu.Game.Input.Bindings;
using osu.Game.Localisation;
using osu.Game.Overlays.Mods.Input;
using osu.Game.Overlays.Mods;
using osu.Game.Overlays.Settings.Sections.Audio;
using osu.Game.Overlays.Settings.Sections.Graphics;
using osu.Game.Overlays.Settings.Sections.Input;
using osu.Game.Overlays.Settings.Sections.UserInterface;
using osu.Game.Overlays.Settings.Sections;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.SkinEditor;
using osu.Game.Overlays;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Online;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Play;
using osu.Game.Skinning;
using osuTK.Input;
using Realms;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public partial class SomsModHotkeysSection : SettingsSection
{
    internal int? RulesetId { get; }
    public SomsModHotkeysSection(int? rulesetId = null) => RulesetId = rulesetId;
    private static string ModeName(int id) => id switch { 0 => "osu!", 1 => "osu!taiko", 2 => "osu!catch", 3 => "osu!mania", _ => "SOMS!" };
    public override LocalisableString Header => LocalisableString.Interpolate($"Mods {(RulesetId is int id ? ModeName(id) : "SOMS!")} (F1)");
    public override Drawable CreateIcon() => new SpriteIcon { Icon = FontAwesome.Solid.Keyboard };
    public override IEnumerable<LocalisableString> FilterTerms => base.FilterTerms.Concat(new LocalisableString[] { "mods", "hotkeys" });
    private bool loaded;

    [BackgroundDependencyLoader]
    private void load(RulesetStore rulesets, RealmAccess realm)
    {
        if (loaded) return;
        loaded = true;
        foreach (var info in rulesets.AvailableRulesets.Where(r => r.Available && r.OnlineID is >= 0 and < 4 && (RulesetId == null || r.OnlineID == RulesetId)).OrderBy(r => r.OnlineID))
        {
            var mods = info.CreateInstance().CreateAllMods().Where(mod => mod.Acronym.Length is > 0 and <= 3)
                .DistinctBy(mod => mod.Acronym).ToArray();
            int mode = info.OnlineID;
            realm.Write(r =>
            {
                var existing = r.All<RealmKeyBinding>().Where(binding => binding.RulesetName == SomsModHotkeys.Scope).ToArray();
                foreach (var mod in mods)
                {
                    int action = SomsModHotkeys.ActionId(mod.Acronym);
                    if (existing.Any(binding => binding.ActionInt == action && binding.Variant == mode)) continue;
                    var previous = existing.FirstOrDefault(binding => binding.ActionInt == action && binding.Variant == null);
                    r.Add(new RealmKeyBinding(action, previous?.KeyCombination ?? new KeyCombination(InputKey.None), SomsModHotkeys.Scope, mode));
                }
            });
            foreach (var category in mods.GroupBy(mod => mod.Type).OrderBy(group => group.Key))
                Add(new ModBindingsSubsection(mode, RulesetId == null
                    ? LocalisableString.Interpolate($"{ModeName(mode)} / {CategoryName(category.Key)}")
                    : CategoryName(category.Key),
                    category.Select(mod => new KeyBinding(InputKey.None, SomsModHotkeys.ActionId(mod.Acronym)))));
        }
    }

    private static LocalisableString CategoryName(ModType type) => type switch
    {
        ModType.DifficultyReduction => "Difficulty reduction",
        ModType.DifficultyIncrease => "Difficulty increase",
        ModType.Conversion => "Conversion",
        ModType.Automation => "Automation",
        ModType.Fun => "Fun",
        ModType.System => "System",
        _ => type.ToString()
    };

    private partial class ModBindingsSubsection : KeyBindingsSubsection
    {
        private readonly int mode;
        private readonly LocalisableString title;
        protected override LocalisableString Header => LocalisableString.Interpolate($"{title} | unassigned uses the default key");
        public ModBindingsSubsection(int mode, LocalisableString title, IEnumerable<KeyBinding> defaults)
        {
            this.mode = mode;
            this.title = title;
            Defaults = defaults;
        }
        protected override IEnumerable<RealmKeyBinding> GetKeyBindings(Realm realm) => realm.All<RealmKeyBinding>()
            .Where(binding => (binding.RulesetName == SomsModHotkeys.Scope && binding.Variant == mode) || (binding.RulesetName == null && binding.Variant == null));
    }
}


// Merged from SomsModSkinSettings.cs
public partial class SomsModSkinSettings : SettingsSubsection
{
    protected override LocalisableString Header => string.Empty;
    protected override Drawable CreateHeader() => new osu.Framework.Graphics.Containers.Container();
    private readonly string[] categories = { "NM", "HD", "HR", "DT", "EZ" };
    private SomsSkinHotkeysSection.SkinSlotDropdown[] dropdowns = Array.Empty<SomsSkinHotkeysSection.SkinSlotDropdown>();
    private SettingsItemV2[] rows = Array.Empty<SettingsItemV2>();
    private Bindable<bool> enabled = null!;
    private IDisposable? subscription;
    private ScheduledDelegate? refresh;
    [Resolved] private SkinManager skins { get; set; } = null!;
    [Resolved] private RealmAccess realm { get; set; } = null!;

    [BackgroundDependencyLoader]
    private void load()
    {
        var preferences = SomsClientPreferences.Instance;
        enabled = preferences.ModSkinsEnabled.GetBoundCopy();
        Add(new SettingsItemV2(new FormCheckBox
        {
            Caption = "Mod Skin Matching",
            HintText = "Automatically select the assigned skin based on mods before playing.",
            Current = enabled,
        }));
        dropdowns = categories.Select((category, index) => new SomsSkinHotkeysSection.SkinSlotDropdown(skins, preferences.ModSkins[index].Value)
        {
            Caption = category,
            Current = preferences.ModSkins[index].GetBoundCopy(),
            AlwaysShowSearchBar = true,
            AllowNonContiguousMatching = true,
        }).ToArray();
        rows = dropdowns.Select(dropdown => new SettingsItemV2(dropdown)).ToArray();
        foreach (var row in rows) Add(row);
        enabled.BindValueChanged(_ => { foreach (var row in rows) row.Alpha = enabled.Value ? 1 : 0; }, true);
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        subscription = realm.RegisterForNotifications(r => r.All<SkinInfo>(), (_, _) =>
        {
            refresh?.Cancel();
            refresh = Scheduler.Add(() =>
            {
                if (!IsDisposed) foreach (var dropdown in dropdowns) dropdown.Refresh(skins, dropdown.Current.Value);
            });
        });
    }

    protected override void Dispose(bool isDisposing)
    {
        subscription?.Dispose();
        refresh?.Cancel();
        enabled?.UnbindAll();
        base.Dispose(isDisposing);
    }
}

// Merged from SomsSkinHotkeysSection.cs
public partial class SomsSkinHotkeysSection : SettingsSection
{
    public override LocalisableString Header => "SOMS! Skins";
    public override Drawable CreateIcon() => new SpriteIcon { Icon = OsuIcon.SkinB };
    public override IEnumerable<LocalisableString> FilterTerms => base.FilterTerms.Concat(new LocalisableString[] { "skins", "hotkeys", "leaderboard", "collapse" });

    public SomsSkinHotkeysSection()
    {
        ChildrenEnumerable = Enumerable.Range(0, SomsClientPreferences.SKIN_SLOT_COUNT)
            .Select(slot => (SettingsSubsection)new SkinSlotSubsection(slot))
            .Append(new LeaderboardSubsection()).Append(new TrainingSubsection());
    }

    private partial class LeaderboardSubsection : KeyBindingsSubsection
    {
        protected override LocalisableString Header => "Leaderboard";

        public LeaderboardSubsection()
        {
            Defaults = SomsSkinHotkeys.Defaults.Where(binding => (int)binding.Action == (int)SomsSkinAction.ToggleLeaderboardCollapse);
        }

        protected override IEnumerable<RealmKeyBinding> GetKeyBindings(Realm realm) => realm.All<RealmKeyBinding>()
            .Where(binding => binding.RulesetName == null && binding.Variant == null);
    }

    private partial class TrainingSubsection : KeyBindingsSubsection
    {
        protected override LocalisableString Header => "Practice Rewind";
        public TrainingSubsection() => Defaults = SomsSkinHotkeys.Defaults.Where(binding => (int)binding.Action == (int)SomsSkinAction.GameplaySeek);
        protected override IEnumerable<RealmKeyBinding> GetKeyBindings(Realm realm) => realm.All<RealmKeyBinding>()
            .Where(binding => binding.RulesetName == null && binding.Variant == null);
    }

    private partial class SkinSlotSubsection : KeyBindingsSubsection
    {
        private readonly int slot;
        private SkinSlotDropdown dropdown = null!;
        private IDisposable? skinSubscription;
        private ScheduledDelegate? pendingRefresh;
        private bool loaded;
        private volatile bool disposed;

        [Resolved]
        private RealmAccess realm { get; set; } = null!;

        [Resolved]
        private SkinManager skins { get; set; } = null!;

        protected override LocalisableString Header => LocalisableString.Interpolate($"Skin {slot + 1}");

        public SkinSlotSubsection(int slot)
        {
            this.slot = slot;
            Defaults = SomsSkinHotkeys.Defaults.Where(binding => SomsSkinHotkeys.SlotIndex((int)binding.Action) == slot);
        }

        // Include all global actions so native conflict handling also detects collisions
        // with normal shortcuts, and preserves the user's chosen resolution.
        protected override IEnumerable<RealmKeyBinding> GetKeyBindings(Realm realm) => realm.All<RealmKeyBinding>()
            .Where(binding => binding.RulesetName == null && binding.Variant == null);

        [BackgroundDependencyLoader]
        private void load()
        {
            if (dropdown != null || disposed)
                return;

            dropdown = new SkinSlotDropdown(skins, SomsClientPreferences.Instance.SkinSlots[slot].Value)
            {
                Caption = "Skin Hotkey",
                HintText = "Select a skin and assign a key or key combination above. Assignments are saved automatically.",
                Current = SomsClientPreferences.Instance.SkinSlots[slot].GetBoundCopy(),
                AlwaysShowSearchBar = true,
                AllowNonContiguousMatching = true,
            };
            Add(new SettingsItemV2(dropdown));
        }

        protected override void LoadComplete()
        {
            if (loaded || disposed)
                return;
            loaded = true;
            base.LoadComplete();
            skinSubscription = realm.RegisterForNotifications(r => r.All<SkinInfo>().Where(skin => !skin.DeletePending), (sender, _) =>
            {
                if (disposed || !sender.Any())
                    return;

                pendingRefresh?.Cancel();
                pendingRefresh = Schedule(() =>
                {
                    pendingRefresh = null;
                    if (!disposed)
                        dropdown.Refresh(skins, dropdown.Current.Value);
                });
            });
        }

        protected override void Dispose(bool isDisposing)
        {
            disposed = true;
            skinSubscription?.Dispose();
            skinSubscription = null;
            pendingRefresh?.Cancel();
            pendingRefresh = null;
            base.Dispose(isDisposing);
        }
    }

    internal partial class SkinSlotDropdown : FormDropdown<Guid>
    {
        private readonly Dictionary<Guid, LocalisableString> names = new();

        public SkinSlotDropdown(SkinManager skins, Guid selectedId)
        {
            Refresh(skins, selectedId);
        }

        public void Refresh(SkinManager skins, Guid selectedId)
        {
            if (IsDisposed)
                return;
            names.Clear();
            names.Add(Guid.Empty, "Unassigned");
            foreach (var skin in skins.GetAllUsableSkins())
                names[skin.ID] = SomsSkinHotkeys.IsRandomSkin(skin.ID)
                    ? "Random skin"
                    : skin.ToString() ?? "Skin";
            if (!names.ContainsKey(selectedId))
                names[selectedId] = "Skin unavailable - choose another";
            Items = names.Keys;
        }

        protected override LocalisableString GenerateItemText(Guid item) => names.TryGetValue(item, out LocalisableString name)
            ? name
            : item.ToString();
    }
}

// Merged from SomsStealthSettings.cs
public sealed partial class SomsStealthSettings : SettingsSubsection
{
    protected override LocalisableString Header => "Stealth";
    private SettingsItemV2 option = null!;
    private SettingsButtonV2 reroll = null!;
    private string lastStatus = "";

    [BackgroundDependencyLoader]
    private void load()
    {
        Add(option = new SettingsItemV2(new FormCheckBox
        {
            Caption = "Stealth mode",
            HintText = "Locally replace the username, rank, and avatar with a player of similar PP on osu.ppy.sh. When PP changes, only the rank is updated.",
            Current = SomsClientPreferences.Instance.StealthMode.GetBoundCopy(),
        }) { Name = "soms-stealth-mode", Keywords = new[] { "stealth", "nick", "avatar", "rank", "mask" } });
        Add(reroll = new SettingsButtonV2
        {
            Name = "soms-stealth-reroll", Text = "Reroll stealth",
            Action = () => SomsStealthSession.Current?.Reroll(),
        });
    }

    protected override void Update()
    {
        base.Update();
        var session = SomsStealthSession.Current;
        reroll.Enabled.Value = session != null && SomsClientPreferences.Instance.StealthMode.Value && !session.Busy.Value;
        string status = session?.Status.Value ?? "Waiting for SOMS login...";
        if (lastStatus == status) return;
        lastStatus = status;
        option.Note.Value = new SettingsNote.Data(status, SettingsNote.Type.Informational);
    }
}


public sealed partial class SomsSkinDebugSettings : SettingsSubsection
{
    protected override LocalisableString Header => "Skin";

    [BackgroundDependencyLoader]
    private void load()
    {
        Add(new SettingsItemV2(new FormCheckBox
        {
            Caption = "Legacy Interface",
            HintText = "osu!stable interface compatible with legacy skins",
            Current = SomsClientPreferences.Instance.LegacyInterface.GetBoundCopy(),
        })
        {
            Name = "soms-debug-skin-legacy-interface",
            Keywords = new[] { "SOMS", "skin", "legacy", "interface" },
        });
    }
}

public sealed partial class SomsMirrorDebugSettings : SettingsSubsection
{
    protected override LocalisableString Header => "Mirrors";

    [BackgroundDependencyLoader]
    private void load()
    {
        Add(new SettingsItemV2(new FormDropdown<SomsBeatmapMirror>
        {
            Caption = "Beatmap downloads",
            HintText = "Default uses the normal osu!lazer download path. Other choices always use the selected mirror.",
            Items = Enum.GetValues<SomsBeatmapMirror>(),
            Current = SomsClientPreferences.Instance.BeatmapMirror.GetBoundCopy(),
        })
        {
            Name = "soms-debug-beatmap-mirror",
            Keywords = new[] { "SOMS", "beatmap", "download", "mirror", "Beatconnect", "Mino", "Nerinyan", "Hinamizawa", "OsuDirect" },
        });
    }
}
