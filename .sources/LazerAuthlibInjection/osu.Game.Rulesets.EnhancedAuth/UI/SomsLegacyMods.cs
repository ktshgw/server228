#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Input.States;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Input.Bindings;
using osu.Game.Overlays.Mods;
using osu.Game.Overlays.Mods.Input;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>Stable's three mod rows, sharing lazer's selection, permissions and score multiplier.</summary>
public sealed partial class SomsLegacyMods : SomsLegacyComponent, IKeyBindingHandler<GlobalAction>
{
    private static readonly Color4 reducingColour = new(44, 204, 42, 255);
    private static readonly Color4 increasingColour = new(255, 75, 0, 255);
    private static readonly IReadOnlyDictionary<int, KeyCombination> noBindings = new Dictionary<int, KeyCombination>();
    private readonly ModSelectOverlay owner;
    private Bindable<Dictionary<ModType, IReadOnlyList<ModState>>>? available;
    private Bindable<Visibility>? visibility;
    private bool advanced;
    private TruncatingSpriteText? selectionText;
    private TruncatingSpriteText? multiplierText;
    private ModState[] visibleMods = Array.Empty<ModState>();
    private ModColumn[] nativeColumns = Array.Empty<ModColumn>();
    private readonly List<Container> animatedRows = new();
    private ScoreMultiplierCalculator? multiplierCalculator;
    private object? multiplierRuleset;
    private object? multiplierBeatmap;
    private double nextMultiplierUpdate;

    public SomsLegacyMods(ModSelectOverlay owner)
    {
        this.owner = owner;
        Name = "soms-legacy-mod-selection";
    }

    protected override void LoadComplete()
    {
        available = owner.AvailableMods.GetBoundCopy();
        available.BindValueChanged(_ => RequestRefresh());
        visibility = owner.State.GetBoundCopy();
        visibility.BindValueChanged(change =>
        {
            if (change.NewValue == Visibility.Hidden)
            {
                bool wasAdvanced = advanced;
                advanced = false;
                if (wasAdvanced) RequestRefresh(0, visibleOnly: true);
            }
            else if (!advanced) animateEntrance();
        });
        base.LoadComplete();
    }

    protected override void Rebuild(ISkinSource skin)
    {
        selectionText = null;
        multiplierText = null;
        animatedRows.Clear();
        // Incompatible mods remain as disabled tiles. Selecting one mod must not
        // reconstruct every skinned button merely because another becomes invalid.
        visibleMods = owner.AllAvailableMods.ToArray();
        var native = SomsLegacyInterfacePatch.Member<Drawable>(owner, "TopLevelContent");
        if (native == null) return;
        nativeColumns = descendants(native).OfType<ModColumn>().ToArray();
        Alpha = 1;
        if (advanced)
        {
            AddInternal(new SomsLegacyOverlayButton(skin, "", "← Классический выбор модов", () =>
            {
                owner.SearchTextBox.Current.Value = string.Empty;
                advanced = false;
                RequestRefresh();
            }, new Color4(120, 199, 236, 255))
            {
                Name = "soms-legacy-return-to-mod-grid",
                Position = new Vector2(20, 18),
                Size = new Vector2(350, 44),
            });
            return;
        }

        HideNative(new[] { native });
        AddInternal(new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.Black, Alpha = 0.84f });
        var scaling = new DrawSizePreservingFillContainer { TargetDrawSize = new Vector2(1280, 720) };
        var canvas = new Container
        {
            Name = "soms-legacy-mod-canvas",
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Size = new Vector2(1280, 720),
        };
        scaling.Add(canvas);
        AddInternal(scaling);
        canvas.Add(label("Моды влияют на процесс игры. Некоторые из них изменяют количество получаемых", 8, 4, 1264, 30));
        canvas.Add(label("очков, а некоторые придуманы просто так, для развлечения.", 8, 39, 1264, 30));
        canvas.Add(multiplierText = label("Score Multiplier: 1.00x", 0, 102, 720, 38));
        multiplierText.Anchor = Anchor.TopCentre;
        multiplierText.Origin = Anchor.TopCentre;

        addRow(canvas, skin, "Упрощение игры", reducingColour, 178,
            new[] { "EZ" }, new[] { "NF" }, new[] { "HT", "DC" });
        addRow(canvas, skin, "Усложнение игры", increasingColour, 268,
            new[] { "HR" }, new[] { "SD", "PF" }, new[] { "DT", "NC" }, new[] { "HD" }, new[] { "FL" }, new[] { "FI" });
        addRow(canvas, skin, "Особые", Color4.White, 358,
            new[] { "RX" }, new[] { "AP" }, new[] { "SO" }, new[] { "AT", "AU", "CN", "CM" }, new[] { "V2" }, new[] { "MR" }, new[] { "RD" });

        canvas.Add(selectionText = label("", 290, 437, 960, 17, new Color4(190, 190, 190, 255)));
        canvas.Add(new ActionBar("1. Сбросить все моды", clearSelection, new Color4(238, 46, 0, 255))
        {
            Name = "soms-legacy-clear-mods",
            Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre,
            Position = new Vector2(0, 482), Size = new Vector2(680, 52),
        });
        canvas.Add(new ActionBar("2. Отмена", owner.Hide, new Color4(98, 98, 98, 255))
        {
            Name = "soms-legacy-accept-mods",
            Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre,
            Position = new Vector2(0, 558), Size = new Vector2(680, 52),
        });
        canvas.Add(new ActionBar("Дополнительные моды и настройки", showAdvanced, new Color4(56, 52, 70, 255), 22)
        {
            Name = "soms-legacy-advanced-mod-settings",
            Anchor = Anchor.TopCentre, Origin = Anchor.TopCentre,
            Position = new Vector2(0, 638), Size = new Vector2(520, 38),
        });
        nextMultiplierUpdate = 0;
        if (owner.State.Value == Visibility.Visible) animateEntrance();
    }

    private void addRow(Container canvas, ISkin skin, string title, Color4 colour, float y, params string[][] families)
    {
        var row = new Container
        {
            Name = "soms-legacy-mod-row-" + animatedRows.Count,
            Position = new Vector2(0, y), Size = new Vector2(1280, 72),
        };
        row.Add(label(title, 20, 13, 280, 30, colour));
        int index = 0;
        foreach (string[] family in families)
        {
            var states = family.Select(acronym => visibleMods.FirstOrDefault(mod => mod.Mod.Acronym == acronym))
                .OfType<ModState>().ToArray();
            if (states.Length == 0) continue;
            row.Add(new ModTile(skin, states, cycle, colour, () => !owner.SelectedMods.Disabled)
            {
                Name = "soms-legacy-mod-" + states[0].Mod.Acronym,
                Position = new Vector2(316 + index * 99, 0), Size = new Vector2(68, 66),
            });
            index++;
        }
        if (index == 0)
            row.Add(label("Нет доступных модов", 316, 22, 700, 20, new Color4(160, 160, 160, 255)));
        animatedRows.Add(row);
        canvas.Add(row);
    }

    private void animateEntrance()
    {
        for (int i = 0; i < animatedRows.Count; i++)
        {
            var row = animatedRows[i];
            row.FadeOut();
            row.X = 18;
            row.Delay(i * 35).FadeIn(220, Easing.OutQuint).MoveToX(0, 280, Easing.OutQuint);
        }
    }

    private void cycle(ModState[] family)
    {
        if (owner.SelectedMods.Disabled || family.Any(mod => mod.Active.Value && mod.Active.Disabled)) return;
        var selectable = family.Where(mod => mod.ValidForSelection.Value && !mod.Active.Disabled).ToArray();
        if (selectable.Length == 0) return;
        int current = Array.FindIndex(selectable, mod => mod.Active.Value);
        if (current >= 0) selectable[current].Active.Value = false;
        if (++current >= selectable.Length) return;
        var state = selectable[current];
        state.PendingConfiguration = state.Mod.RequiresConfiguration;
        state.Active.Value = true;
        if (state.Mod.RequiresConfiguration) showAdvanced();
    }

    private void clearSelection()
    {
        if (owner.SelectedMods.Disabled) return;
        // Native DeselectAll queues staggered panel updates. The hidden columns do not tick here.
        foreach (var mod in owner.AllAvailableMods.Where(mod => mod.Active.Value && !mod.Active.Disabled).ToArray())
            mod.Active.Value = false;
    }

    private void showAdvanced()
    {
        advanced = true;
        if (SomsLegacyInterfacePatch.Member<ModCustomisationPanel>(owner, "customisationPanel") is { } panel && panel.Enabled.Value)
            panel.ExpandedState.Value = ModCustomisationPanel.ModCustomisationPanelState.Expanded;
        RequestRefresh();
    }

    protected override bool OnKeyDown(KeyDownEvent e)
    {
        if (!LegacyEnabled || advanced || owner.State.Value != Visibility.Visible || e.Repeat) return false;
        var bindings = currentBindings();
        if (handleCustomKey(e.CurrentState, bindings)) return true;
        if (!owner.SelectedMods.Disabled)
        {
            var selectable = owner.AllAvailableMods.Where(mod => !mod.Active.Disabled).ToArray();
            // Reuse the hidden columns' configured handlers, including users' custom overrides.
            foreach (var column in nativeColumns)
            {
                if (SomsLegacyInterfacePatch.Member<IModHotkeyHandler>(column, "hotkeyHandler") is not { } handler) continue;
                IEnumerable<ModState> columnMods = column.AvailableMods;
                if (handler is SequentialModHotkeyHandler)
                {
                    // Disabled entries still occupy their original sequential key position.
                    // Removing one first would assign its neighbour to a different key.
                    var keys = SomsLegacyInterfacePatch.Member<Key[]>(handler, "toggleKeys");
                    int index = keys == null ? -1 : Array.IndexOf(keys, e.Key);
                    var target = index < 0 ? null : columnMods.Where(mod => mod.Visible).ElementAtOrDefault(index);
                    if (!e.ControlPressed && !e.AltPressed && !e.SuperPressed && target?.Active.Disabled == true) return true;
                }
                else columnMods = columnMods.Where(mod => !mod.Active.Disabled);
                if (SomsModKeyPressPatch.Handle(e, selectable, columnMods, handler, bindings))
                    return true;
            }
        }
        if (e.ControlPressed || e.AltPressed || e.SuperPressed || e.ShiftPressed) return false;
        switch (e.Key)
        {
            case Key.Number1:
            case Key.Keypad1:
                clearSelection();
                return true;
            case Key.Number2:
            case Key.Keypad2:
                owner.Hide();
                return true;
            case Key.Tab:
                showAdvanced();
                return true;
        }
        return false;
    }

    public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
    {
        if (!LegacyEnabled || advanced || owner.State.Value != Visibility.Visible || e.Repeat) return false;
        if (e.Action != GlobalAction.DeselectAllMods && e.Action != GlobalAction.Back && e.Action != GlobalAction.ToggleModSelection) return false;
        // Global bindings can consume a key before OnKeyDown reaches the rows. Apply an overlapping
        // user mod binding here, so assigning 1, Escape or F1 never clears/closes the overlay instead.
        if (handleCustomKey(e.CurrentState, currentBindings())) return true;
        if (e.Action != GlobalAction.DeselectAllMods) return false;
        clearSelection();
        return true;
    }

    public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e) { }

    private IReadOnlyDictionary<int, KeyCombination> currentBindings() =>
        SomsModHotkeys.Observers.TryGetValue(owner, out var observer)
            ? observer.ForRuleset(owner.Ruleset.Value?.OnlineID ?? -1) : noBindings;

    private bool handleCustomKey(InputState input, IReadOnlyDictionary<int, KeyCombination> bindings)
    {
        var pressed = KeyCombination.FromInputState(input);
        foreach (var binding in bindings)
        {
            if (!binding.Value.IsPressed(pressed, input, KeyCombinationMatchingMode.Exact)) continue;
            var state = owner.AllAvailableMods.FirstOrDefault(mod => SomsModHotkeys.ActionId(mod.Mod.Acronym) == binding.Key);
            // A forbidden custom binding still consumes the key: for example a locked mod on 2
            // must not turn that same press into the classic close-overlay shortcut.
            if (!owner.SelectedMods.Disabled && state is { Visible: true } && !state.Active.Disabled)
            {
                state.PendingConfiguration = !state.Active.Value && state.Mod.RequiresConfiguration;
                state.Active.Toggle();
                if (state.Active.Value && state.Mod.RequiresConfiguration) showAdvanced();
            }
            return true;
        }
        return false;
    }

    protected override void Update()
    {
        base.Update();
        if (selectionText == null || !LegacyEnabled) return;
        var selected = owner.ActiveMods.Value;
        selectionText.Text = selected.Count == 0 ? "" : "Выбрано: " + string.Join(" · ", selected.Select(mod => mod.Acronym));
        if (Time.Current < nextMultiplierUpdate) return;
        nextMultiplierUpdate = Time.Current + 100;
        if (owner.Ruleset.Value == null || owner.Beatmap.Value == null || multiplierText == null) return;
        if (!ReferenceEquals(multiplierRuleset, owner.Ruleset.Value) || !ReferenceEquals(multiplierBeatmap, owner.Beatmap.Value))
        {
            multiplierRuleset = owner.Ruleset.Value;
            multiplierBeatmap = owner.Beatmap.Value;
            multiplierCalculator = owner.Ruleset.Value.CreateInstance().CreateScoreMultiplierCalculator(
                new ScoreMultiplierContext(owner.Beatmap.Value.BeatmapInfo.Difficulty));
        }
        double multiplier = multiplierCalculator!.CalculateFor(selected);
        multiplierText.Text = "Score Multiplier: " + multiplier.ToString("0.00", CultureInfo.InvariantCulture) + "x";
        multiplierText.Colour = multiplier < 1 ? increasingColour : multiplier > 1 ? reducingColour : Color4.White;
    }

    protected override void RestoreLayout()
    {
        selectionText = null;
        multiplierText = null;
        nativeColumns = Array.Empty<ModColumn>();
        animatedRows.Clear();
        multiplierCalculator = null;
        multiplierRuleset = null;
        multiplierBeatmap = null;
    }

    protected override void Dispose(bool isDisposing)
    {
        available?.UnbindAll();
        visibility?.UnbindAll();
        base.Dispose(isDisposing);
    }

    private static IEnumerable<Drawable> descendants(Drawable drawable)
    {
        yield return drawable;
        if (drawable is not CompositeDrawable composite) yield break;
        foreach (var child in SomsLegacyInterfacePatch.Children(composite))
            foreach (var descendant in descendants(child)) yield return descendant;
    }

    private static TruncatingSpriteText label(string text, float x, float y, float width, float size, Color4? colour = null) => new()
    {
        Position = new Vector2(x, y), MaxWidth = width, Text = text,
        Font = SomsLegacyFont.Font(size), Colour = colour ?? Color4.White, Shadow = true,
    };

    private sealed partial class ActionBar : OsuClickableContainer
    {
        private readonly Box hover;

        public ActionBar(string text, Action action, Color4 colour, float fontSize = 40)
        {
            Action = action;
            Masking = true;
            CornerRadius = 3;
            Children = new Drawable[]
            {
                new Box { RelativeSizeAxes = Axes.Both, Colour = colour },
                hover = new Box { RelativeSizeAxes = Axes.Both, Colour = Color4.White, Alpha = 0 },
                new Box { RelativeSizeAxes = Axes.X, Height = 1, Colour = Color4.White, Alpha = 0.12f },
                new TruncatingSpriteText
                {
                    Anchor = Anchor.Centre, Origin = Anchor.Centre, Text = text,
                    Font = SomsLegacyFont.Font(fontSize), Shadow = true,
                },
            };
        }

        protected override bool OnHover(HoverEvent e)
        {
            hover.FadeTo(0.16f, 100);
            this.ScaleTo(1.015f, 180, Easing.OutQuint);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            hover.FadeOut(180);
            this.ScaleTo(1, 220, Easing.OutQuint);
            base.OnHoverLost(e);
        }
    }

    private sealed partial class ModTile : OsuClickableContainer
    {
        private readonly ModState[] family;
        private readonly Func<bool> canSelect;
        private readonly Box selected;
        private readonly Container icon;
        private readonly Drawable[] art;
        private bool? previousActive;
        private bool previousHovered;
        private int previousVariant = -1;

        public ModTile(ISkin skin, ModState[] family, Action<ModState[]> action, Color4 colour, Func<bool> canSelect)
        {
            this.family = family;
            this.canSelect = canSelect;
            Action = () => action(family);
            Children = new Drawable[]
            {
                selected = new Box { RelativeSizeAxes = Axes.Both, Colour = colour, Alpha = 0 },
                icon = new Container { Anchor = Anchor.Centre, Origin = Anchor.Centre, RelativeSizeAxes = Axes.Both },
            };
            art = family.Select(state => SomsLegacyOverlayButton.Art(skin, "selection-mod-" + assetSuffix(state.Mod.Acronym))
                ?? new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        new Box { RelativeSizeAxes = Axes.Both, Colour = colour, Alpha = 0.5f },
                        new OsuSpriteText
                        {
                            Anchor = Anchor.Centre, Origin = Anchor.Centre, Text = state.Mod.Acronym,
                            Font = SomsLegacyFont.Font(26, bold: true), Shadow = true,
                        },
                    },
                }).ToArray();
            foreach (var image in art) { image.Alpha = 0; icon.Add(image); }
        }

        protected override void Update()
        {
            base.Update();
            int variant = Array.FindIndex(family, state => state.Active.Value);
            bool active = variant >= 0;
            if (variant < 0) variant = 0;
            if (variant != previousVariant)
            {
                for (int i = 0; i < art.Length; i++) art[i].Alpha = i == variant ? 1 : 0;
                previousVariant = variant;
                TooltipText = family[variant].Mod.Name + " (" + family[variant].Mod.Acronym + ")"
                    + (family.Length > 1 ? " — повторное нажатие: " + string.Join(" → ", family.Select(mod => mod.Mod.Acronym)) + " → выкл." : "");
            }
            if (previousActive != active || previousHovered != IsHovered)
            {
                previousActive = active;
                previousHovered = IsHovered;
                selected.FadeTo(active ? 0.18f : 0, 140);
                icon.ScaleTo(active ? 1.12f : IsHovered ? 1.06f : 1, 240, Easing.OutBack);
                icon.RotateTo(active ? 10 : IsHovered ? -4 : 0, 220, Easing.OutQuint);
            }
            Alpha = canSelect() && family.Any(state => state.ValidForSelection.Value && !state.Active.Disabled) ? 1 : 0.35f;
        }

        private static string assetSuffix(string acronym) => acronym switch
        {
            "EZ" => "easy", "NF" => "nofail", "HT" => "halftime", "DC" => "daycore",
            "HR" => "hardrock", "SD" => "suddendeath", "PF" => "perfect", "DT" => "doubletime",
            "NC" => "nightcore", "HD" => "hidden", "FI" => "fadein", "FL" => "flashlight",
            "RX" => "relax", "AP" => "relax2", "AT" or "AU" => "autoplay", "CN" or "CM" => "cinema",
            "SO" => "spunout", "MR" => "mirror", "RD" => "random", "TP" => "target", "V2" => "scorev2",
            _ => acronym.ToLowerInvariant(),
        };
    }
}
