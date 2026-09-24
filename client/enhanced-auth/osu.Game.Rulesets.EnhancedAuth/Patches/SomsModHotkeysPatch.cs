#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Game.Database;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Input.Bindings;
using osu.Game.Overlays;
using osu.Game.Overlays.Mods;
using osu.Game.Overlays.Mods.Input;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.Settings.Sections.Input;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using Realms;
using osuTK.Input;

namespace osu.Game.Rulesets.EnhancedAuth.Patches;

internal static partial class SomsModHotkeys
{
    internal const string Scope = "soms-mod-hotkeys";
    // Stable acronym encoding: introducing a new mod never shifts saved IDs.
    internal static int ActionId(string acronym) => acronym.Length is > 0 and <= 3
        ? 0x54000000 | (acronym[0] << 16) | (acronym.Length > 1 ? acronym[1] << 8 : 0) | (acronym.Length > 2 ? acronym[2] : 0)
        : throw new ArgumentException("Unsupported mod acronym", nameof(acronym));
    internal static bool IsAction(int action) => (action & unchecked((int)0xff000000)) == 0x54000000;
    internal static string Acronym(int action) => new(new[] { (char)((action >> 16) & 255), (char)((action >> 8) & 255), (char)(action & 255) }.Where(c => c != 0).ToArray());
    internal static readonly ConditionalWeakTable<ModSelectOverlay, BindingObserver> Observers = new();

    internal partial class BindingObserver : Component
    {
        internal volatile Dictionary<int, Dictionary<int, KeyCombination>> Bindings = new();
        internal Dictionary<int, KeyCombination> ForRuleset(int mode)
        {
            var snapshot = Bindings;
            var result = snapshot.TryGetValue(-1, out var legacy) ? new Dictionary<int, KeyCombination>(legacy) : new();
            if (snapshot.TryGetValue(mode, out var overrides))
                foreach (var pair in overrides) result[pair.Key] = pair.Value;
            return result.Where(pair => pair.Value.Keys.Any(key => key != InputKey.None)).ToDictionary(pair => pair.Key, pair => pair.Value);
        }
        private IDisposable? subscription;
        [BackgroundDependencyLoader]
        private void load(RealmAccess realm)
        {
            subscription = realm.RegisterForNotifications(r => r.All<RealmKeyBinding>().Where(b => b.RulesetName == Scope), (bindings, _) =>
            {
                // Copy values while Realm is valid; no managed records survive the callback.
                // Preserve empty per-mode assignments: clearing one must not revive a legacy binding.
                Bindings = bindings.GroupBy(b => b.Variant ?? -1).ToDictionary(mode => mode.Key,
                    mode => mode.GroupBy(b => b.ActionInt).ToDictionary(group => group.Key, group => group.First().KeyCombination));
            });
        }
        protected override void Dispose(bool isDisposing)
        {
            subscription?.Dispose();
            subscription = null;
            base.Dispose(isDisposing);
        }
    }
}

[HarmonyPatch(typeof(ModSelectOverlay), "load")]
public static class SomsModBindingObserverPatch
{
    static void Postfix(ModSelectOverlay __instance)
    {
        if (!SomsClientPreferences.Enabled || SomsModHotkeys.Observers.TryGetValue(__instance, out _)) return;
        var observer = new SomsModHotkeys.BindingObserver();
        SomsModHotkeys.Observers.Add(__instance, observer);
        AccessTools.Method(typeof(CompositeDrawable), "AddInternal", [typeof(Drawable)]).Invoke(__instance, [observer]);
    }
}

[HarmonyPatch(typeof(ModColumn), "OnKeyDown")]
public static class SomsModKeyPressPatch
{
    static bool Prefix(ModColumn __instance, KeyDownEvent e, IModHotkeyHandler ___hotkeyHandler, ref bool __result)
    {
        if (!SomsClientPreferences.Enabled || e.Repeat) return true;
        ModSelectOverlay? overlay = null;
        for (var parent = __instance.Parent; parent != null; parent = parent.Parent)
            if (parent is ModSelectOverlay found) { overlay = found; break; }
        if (overlay == null || !SomsModHotkeys.Observers.TryGetValue(overlay, out var observer) || observer.Bindings.Count == 0) return true;
        var bindings = observer.ForRuleset(overlay.Ruleset.Value?.OnlineID ?? -1);
        if (bindings.Count == 0) return true;
        __result = Handle(e, overlay.AvailableMods.Value.Values.SelectMany(mods => mods), __instance.AvailableMods, ___hotkeyHandler, bindings);
        return false;
    }

    internal static bool Handle(KeyDownEvent e, IEnumerable<ModState> allMods, IEnumerable<ModState> columnMods,
        IModHotkeyHandler native, IReadOnlyDictionary<int, KeyCombination> bindings)
    {
        if (e.Repeat) return false;
        var pressed = KeyCombination.FromInputState(e.CurrentState);
        bool Assigned(ModState mod) => bindings.ContainsKey(SomsModHotkeys.ActionId(mod.Mod.Acronym));
        foreach (var mod in allMods.Where(mod => mod.Visible))
        {
            if (!bindings.TryGetValue(SomsModHotkeys.ActionId(mod.Mod.Acronym), out var combination)
                || !combination.IsPressed(pressed, e.CurrentState, KeyCombinationMatchingMode.Exact)) continue;
            mod.Active.Toggle();
            return true;
        }
        if (e.ControlPressed || e.AltPressed || e.SuperPressed) return false;
        if (native is SequentialModHotkeyHandler)
        {
            var keys = (Key[])AccessTools.Field(typeof(SequentialModHotkeyHandler), "toggleKeys").GetValue(native)!;
            int index = Array.IndexOf(keys, e.Key);
            var target = index < 0 ? null : columnMods.Where(mod => mod.Visible).ElementAtOrDefault(index);
            if (target == null || Assigned(target)) return false;
            target.Active.Toggle();
            return true;
        }
        // Classic matching is by mod type, so excluding overridden mods keeps
        // native cycles (DT/NC, SD/PF, etc.) intact for the remaining defaults.
        return native.HandleModHotkeyPressed(e, columnMods.Where(mod => !Assigned(mod)));
    }
}

[HarmonyPatch(typeof(KeyBindingPanel), "load")]
public static class SomsModHotkeySettingsPatch
{
    static void Postfix(KeyBindingPanel __instance)
    {
        if (!SomsClientPreferences.Enabled || SomsDrawableLifecycle.IsDisposed(__instance)) return;
        var sections = (List<SettingsSection>)AccessTools.Field(typeof(SettingsPanel), "loadableSections").GetValue(__instance)!;
        for (int mode = 0; mode < 4; mode++)
            if (!sections.OfType<SomsModHotkeysSection>().Any(section => section.RulesetId == mode))
                AccessTools.Method(typeof(SettingsPanel), "AddSection").Invoke(__instance, [new SomsModHotkeysSection(mode)]);
    }
}

[HarmonyPatch(typeof(RealmKeyBinding), nameof(RealmKeyBinding.GetAction))]
public static class SomsModBindingDescriptionPatch
{
    static bool Prefix(RealmKeyBinding __instance, ref object __result)
    {
        if (__instance.RulesetName != SomsModHotkeys.Scope) return true;
        __result = $"Мод {SomsModHotkeys.Acronym(__instance.ActionInt)}";
        return false;
    }
}

[HarmonyPatch(typeof(KeyBindingRow), "load")]
public static class SomsModBindingCaptionPatch
{
    static void Postfix(KeyBindingRow __instance, FormFieldCaption ___caption)
    {
        if (__instance.Action is int action && SomsModHotkeys.IsAction(action))
            ___caption.Caption = $"{SomsModHotkeys.Acronym(action)} — переключить мод";
    }
}

public partial class SomsModHotkeysSection : SettingsSection
{
    internal int? RulesetId { get; }
    public SomsModHotkeysSection(int? rulesetId = null) => RulesetId = rulesetId;
    private static string ModeName(int id) => id switch { 0 => "osu!", 1 => "osu!taiko", 2 => "osu!catch", 3 => "osu!mania", _ => "SOMS!" };
    public override LocalisableString Header => RulesetId is int id ? $"Моды {ModeName(id)} (F1)" : "Моды SOMS! (F1)";
    public override Drawable CreateIcon() => new SpriteIcon { Icon = FontAwesome.Solid.Keyboard };
    public override IEnumerable<LocalisableString> FilterTerms => base.FilterTerms.Concat(new LocalisableString[] { "mods", "моды", "hotkeys", "горячие клавиши" });
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
                Add(new ModBindingsSubsection(mode, $"{(RulesetId == null ? ModeName(mode) + " / " : "")}{CategoryName(category.Key)}",
                    category.Select(mod => new KeyBinding(InputKey.None, SomsModHotkeys.ActionId(mod.Acronym)))));
        }
    }

    private static string CategoryName(ModType type) => type switch
    {
        ModType.DifficultyReduction => "Упрощающие",
        ModType.DifficultyIncrease => "Усложняющие",
        ModType.Conversion => "Преобразующие",
        ModType.Automation => "Автоматизация",
        ModType.Fun => "Развлекательные",
        ModType.System => "Системные",
        _ => type.ToString()
    };

    private partial class ModBindingsSubsection : KeyBindingsSubsection
    {
        private readonly int mode;
        private readonly string title;
        protected override LocalisableString Header => title + " · без назначения — стандартная клавиша";
        public ModBindingsSubsection(int mode, string title, IEnumerable<KeyBinding> defaults)
        {
            this.mode = mode;
            this.title = title;
            Defaults = defaults;
        }
        protected override IEnumerable<RealmKeyBinding> GetKeyBindings(Realm realm) => realm.All<RealmKeyBinding>()
            .Where(binding => (binding.RulesetName == SomsModHotkeys.Scope && binding.Variant == mode) || (binding.RulesetName == null && binding.Variant == null));
    }
}
