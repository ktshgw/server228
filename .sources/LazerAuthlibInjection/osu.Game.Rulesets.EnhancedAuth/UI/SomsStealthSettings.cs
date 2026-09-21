#nullable enable
using osu.Framework.Allocation;
using osu.Framework.Localisation;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays.Settings;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Online;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

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
            HintText = "Локально заменить ник, ранг и аватарку на игрока с близким PP на osu.ppy.sh. При изменении PP обновляется только ранг (оценка).",
            Current = SomsClientPreferences.Instance.StealthMode.GetBoundCopy(),
        }) { Name = "soms-stealth-mode", Keywords = new[] { "stealth", "ник", "аватар", "ранг", "маскировка" } });
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
        string status = session?.Status.Value ?? "Ожидание входа в SOMS…";
        if (lastStatus == status) return;
        lastStatus = status;
        option.Note.Value = new SettingsNote.Data(status, SettingsNote.Type.Informational);
    }
}
