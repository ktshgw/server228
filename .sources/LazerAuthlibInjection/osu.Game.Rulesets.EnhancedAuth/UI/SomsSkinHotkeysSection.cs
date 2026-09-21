#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Framework.Threading;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Input.Bindings;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.Settings.Sections.Input;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Skinning;
using Realms;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

public partial class SomsSkinHotkeysSection : SettingsSection
{
    public override LocalisableString Header => "Скины SOMS!";
    public override Drawable CreateIcon() => new SpriteIcon { Icon = OsuIcon.SkinB };
    public override IEnumerable<LocalisableString> FilterTerms => base.FilterTerms.Concat(new LocalisableString[] { "skins", "hotkeys", "скин", "горячие клавиши", "таблица рекордов", "сворачивание", "leaderboard" });

    public SomsSkinHotkeysSection()
    {
        ChildrenEnumerable = Enumerable.Range(0, SomsClientPreferences.SkinSlotCount)
            .Select(slot => (SettingsSubsection)new SkinSlotSubsection(slot))
            .Append(new LeaderboardSubsection()).Append(new TrainingSubsection());
    }

    private partial class LeaderboardSubsection : KeyBindingsSubsection
    {
        protected override LocalisableString Header => "Таблица рекордов";

        public LeaderboardSubsection()
        {
            Defaults = SomsSkinHotkeys.Defaults.Where(binding => (int)binding.Action == (int)SomsSkinAction.ToggleLeaderboardCollapse);
        }

        protected override IEnumerable<RealmKeyBinding> GetKeyBindings(Realm realm) => realm.All<RealmKeyBinding>()
            .Where(binding => binding.RulesetName == null && binding.Variant == null);
    }

    private partial class TrainingSubsection : KeyBindingsSubsection
    {
        protected override LocalisableString Header => "Тренировка — перемотка";
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

        protected override LocalisableString Header => $"Скин {slot + 1}";

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
                Caption = "Скин для этой клавиши",
                HintText = "Выберите скин и назначьте клавишу или сочетание выше. Назначения сохраняются автоматически.",
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
        private readonly Dictionary<Guid, string> names = new();

        public SkinSlotDropdown(SkinManager skins, Guid selectedId)
        {
            Refresh(skins, selectedId);
        }

        public void Refresh(SkinManager skins, Guid selectedId)
        {
            if (IsDisposed)
                return;
            names.Clear();
            names.Add(Guid.Empty, "Не назначен");
            foreach (var skin in skins.GetAllUsableSkins())
                names[skin.ID] = SomsSkinHotkeys.IsRandomSkin(skin.ID) ? "Случайный скин" : skin.ToString() ?? "Скин";
            if (!names.ContainsKey(selectedId))
                names[selectedId] = "Скин недоступен — выберите другой";
            Items = names.Keys;
        }

        protected override LocalisableString GenerateItemText(Guid item) => names.TryGetValue(item, out string? name) ? name : "Скин недоступен";
    }
}
