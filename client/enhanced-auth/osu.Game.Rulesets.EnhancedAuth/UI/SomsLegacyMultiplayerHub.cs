#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
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
            add("menu-multi", "Lounge", "Open rooms and team play", new Color4(91, 82, 159, 255), () => lounge.TriggerClick());

        if (somsai != null)
            add("", "SOMSAI", "1v1 / 2v2 / tournament customs", new Color4(94, 63, 186, 255), () => somsai.TriggerClick(), FontAwesome.Solid.Crown);

        add("menu-back", "Back", "Select a game mode", new Color4(90, 95, 111, 255), back);

        void add(string asset, string title, string description, Color4 colour, Action? action, IconUsage? icon = null)
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
                if (icon.HasValue)
                {
                    button.Add(new SpriteIcon
                    {
                        Position = new Vector2(34, 20),
                        Size = new Vector2(30),
                        Icon = icon.Value,
                        Shadow = true,
                    });
                }
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
