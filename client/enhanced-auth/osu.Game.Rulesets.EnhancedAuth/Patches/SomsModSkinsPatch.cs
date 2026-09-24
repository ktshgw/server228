#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Containers;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Play;
using osu.Game.Skinning;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

public static class SomsModSkins
{
    // NM, HD, HR, DT/NC, EZ. Classic and unrelated mods do not change the group.
    public static int Category(IEnumerable<string> mods)
    {
        var selected = mods.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Contains("EZ")) return selected.Contains("HD") ? 1 : 4;
        if (selected.Contains("DT") || selected.Contains("NC")) return 3;
        if (selected.Contains("HR")) return 2;
        if (selected.Contains("HD")) return 1;
        return 0;
    }

    public static void Apply(SkinManager skins, IReadOnlyList<Mod> mods)
    {
        if (!SomsClientPreferences.Enabled || !SomsClientPreferences.Instance.ModSkinsEnabled.Value || skins.CurrentSkinInfo.Disabled) return;
        Guid target = SomsClientPreferences.Instance.ModSkins[Category(mods.Select(mod => mod.Acronym))].Value;
        if (target == Guid.Empty || target == skins.CurrentSkinInfo.Value.ID) return;
        var skin = skins.GetAllUsableSkins().FirstOrDefault(item => item.ID == target);
        if (skin == null) return;
        if (SomsSkinHotkeys.IsRandomSkin(target)) skins.SelectRandomSkin();
        else skins.CurrentSkinInfo.Value = skin;
    }
}

// Observe menu mod changes, coalescing quick toggles into one skin load per frame.
public partial class SomsModSkinController : CompositeDrawable
{
    private IBindable<IReadOnlyList<Mod>> mods = null!;
    private Bindable<bool> enabled = null!;
    private Bindable<Guid>[] slots = Array.Empty<Bindable<Guid>>();
    [Resolved] private SkinManager skins { get; set; } = null!;

    [BackgroundDependencyLoader]
    private void load(IBindable<IReadOnlyList<Mod>> selected)
    {
        AlwaysPresent = true;
        mods = selected.GetBoundCopy();
        mods.BindValueChanged(_ => Scheduler.AddOnce(apply));
        enabled = SomsClientPreferences.Instance.ModSkinsEnabled.GetBoundCopy();
        enabled.BindValueChanged(_ => Scheduler.AddOnce(apply), true);
        slots = SomsClientPreferences.Instance.ModSkins.Select(slot => slot.GetBoundCopy()).ToArray();
        foreach (var slot in slots) slot.BindValueChanged(_ => Scheduler.AddOnce(apply));
    }

    private void apply() { if (!IsDisposed) SomsModSkins.Apply(skins, mods.Value); }

    protected override void Dispose(bool isDisposing)
    {
        mods?.UnbindAll();
        enabled?.UnbindAll();
        foreach (var slot in slots) slot.UnbindAll();
        base.Dispose(isDisposing);
    }
}

[HarmonyPatch(typeof(OsuGame), "LoadComplete")]
public static class SomsModSkinControllerPatch
{
    static void Postfix(OsuGame __instance)
    {
        if (SomsClientPreferences.Enabled) __instance.Add(new SomsModSkinController());
    }
}

// A Player's final mods may differ from menu mods (multiplayer, SOMSAI, retries).
// LoadComplete runs on the update thread, before the first gameplay frame.
[HarmonyPatch(typeof(Player), "LoadComplete")]
public static class SomsModSkinPlayerPatch
{
    static void Prefix(Player __instance)
    {
        if (!SomsClientPreferences.Enabled) return;
        var skins = Traverse.Create(GlobalConfigManager.GameBase).Property("SkinManager").GetValue<SkinManager>();
        if (skins != null) SomsModSkins.Apply(skins, __instance.Mods.Value);
    }
}
