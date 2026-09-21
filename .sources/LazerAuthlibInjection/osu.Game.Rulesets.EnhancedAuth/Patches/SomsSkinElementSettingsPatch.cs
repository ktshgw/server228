#nullable enable
using System.Linq;
using HarmonyLib;
using osu.Framework.Bindables;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.Settings.Sections;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.UI;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

[HarmonyPatch(typeof(SkinSection), "load")]
public static class SomsSkinElementSettingsPatch
{
    static void Postfix(SkinSection __instance)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance))
            return;

        var preferences = SomsClientPreferences.Instance;
        if (!__instance.Children.Any(child => child is SomsModSkinSettings))
            __instance.Add(new SomsModSkinSettings());
        add("legacy-interface", "Legacy interface",
            "Отдельный интерфейс в стиле osu!stable: меню, выбор карт, результаты, моды и пауза. Использует элементы текущего скина; недостающие элементы имеют встроенное оформление.", preferences.LegacyInterface);
        if (!__instance.Children.Any(child => child.Name == "soms-skin-sliderendmiss"))
            __instance.Add(new SettingsItemV2(new FormDropdown<SomsSliderMissDisplay>
            {
                Caption = "Показывать промахи слайдеров",
                HintText = "Нет — скрывать sliderendmiss и slidertickmiss. 50 и 100 — показывать hit50 вместо slidertickmiss и hit100 вместо sliderendmiss. По умолчанию — оформление скина.",
                Items = System.Enum.GetValues<SomsSliderMissDisplay>(),
                Current = preferences.SliderMissDisplay.GetBoundCopy(),
            }) { Name = "soms-skin-sliderendmiss", Keywords = new[] { "sliderendmiss", "slidertickmiss", "слайдер", "промах" } });
        add("followpoints", "Draw followpoints", "Показывать соединяющие объекты точки из скина.", preferences.DrawFollowPoints);
        add("sliderfollowcircle", "Показывать круг сопровождения слайдера (sliderfollowcircle)",
            "Показывать круг вокруг движущегося шарика слайдера. Применяется ко всем скинам.", preferences.ShowSliderFollowCircle);
        add("leaderboard-collapse", "Разрешать сворачивание таблицы рекордов",
            "Включено — использовать правило текущего скина. Выключено — не сворачивать таблицу во всех скинах. Горячая клавиша: назначение клавиш → Скины SOMS! → Таблица рекордов.", preferences.UseSkinLeaderboardCollapse);

        void add(string key, string caption, string hint, Bindable<bool> current)
        {
            string name = "soms-skin-" + key;
            if (__instance.Children.Any(child => child.Name == name))
                return;

            __instance.Add(new SettingsItemV2(new FormCheckBox
            {
                Caption = caption,
                HintText = hint,
                Current = current.GetBoundCopy(),
            })
            {
                Name = name,
                Keywords = new[] { "SOMS", "skin", "скин", key, "слайдер", "таблица", "рекорды" },
            });
        }
    }
}
