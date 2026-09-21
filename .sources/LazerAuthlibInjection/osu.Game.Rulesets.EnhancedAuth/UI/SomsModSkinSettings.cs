#nullable enable
using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Framework.Threading;
using osu.Game.Database;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays.Settings;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Skinning;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

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
            Caption = "Скины под моды",
            HintText = "Автоматически выбирать назначенный скин по модам перед игрой.",
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
