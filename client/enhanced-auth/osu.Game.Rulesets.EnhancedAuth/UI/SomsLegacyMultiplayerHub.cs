#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Screens.Menu;
using osu.Game.Skinning;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EnhancedAuth.UI;

/// <summary>The classic multiplayer submenu, using the real native menu actions including login checks.</summary>
public sealed partial class SomsLegacyMultiplayerHub : FillFlowContainer
{
    public SomsLegacyMultiplayerHub(ISkin skin, ButtonSystem nativeButtons, Action back)
    {
        Name = "soms-legacy-multiplayer-hub";
        Width = 500;
        AutoSizeAxes = Axes.Y;
        Direction = FillDirection.Vertical;
        Spacing = new Vector2(0, 9);

        var native = SomsLegacyInterfacePatch.Member<List<MainMenuButton>>(nativeButtons, "buttonsMulti") ?? new List<MainMenuButton>();
        var lounge = native.FirstOrDefault(button => button.Name != "somsai-menu-button");
        var somsai = native.FirstOrDefault(button => button.Name == "somsai-menu-button");

        if (lounge != null)
            add("menu-multi", "Лобби", "Открытые комнаты и командная игра", new Color4(91, 82, 159, 255), () => lounge.TriggerClick());

        // Ranked is intentionally unavailable on SOMS!, in either interface mode.
        add("", "Ranked", "Иди в обычный лазер", new Color4(74, 77, 91, 255), null);

        if (somsai != null)
            add("menu-somsai", "SOMSAI", "1v1 · 2v2 · турнирные кастомы", new Color4(57, 146, 160, 255), () => somsai.TriggerClick());

        add("menu-back", "Назад", "Выбор режима игры", new Color4(90, 95, 111, 255), back);

        void add(string asset, string title, string description, Color4 colour, Action? action)
        {
            var button = new OsuClickableContainer
            {
                Name = "soms-legacy-hub-" + title.ToLowerInvariant(),
                Size = new Vector2(500, 70),
                Action = action,
                Enabled = { Value = action != null },
                TooltipText = description,
            };
            string stableAsset = asset switch
            {
                "menu-multi" => "menu-button-multiplayer",
                "menu-back" => "menu-button-back",
                _ => asset,
            };
            var art = SomsLegacyOverlayButton.Art(skin, stableAsset) ?? SomsLegacyOverlayButton.Art(skin, asset);
            if (art != null) button.Add(art);
            else
            {
                button.Add(new Box { RelativeSizeAxes = Axes.Both, Colour = colour });
                button.Add(new TruncatingSpriteText
                {
                    Position = new Vector2(98, 8),
                    Width = 380,
                    Text = title,
                    Font = SomsLegacyFont.Font(29, bold: true),
                    Shadow = true,
                });
                button.Add(new TruncatingSpriteText
                {
                    Position = new Vector2(100, 43),
                    Width = 380,
                    Text = description,
                    Font = SomsLegacyFont.Font(16),
                    Shadow = true,
                });
            }
            Add(button);
        }
    }
}
