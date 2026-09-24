using System.Collections;
using System.Reflection;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Game.Beatmaps;
using osu.Game.Screens.Select;
using osu.Framework.Input.StateChanges;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterface;
using osu.Game.Overlays.Mods;
using osu.Game.Overlays;
using osu.Game.Overlays.Volume;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Screens.Menu;
using osuTK;
using osuTK.Input;

internal sealed partial class IntegrationGame
{
    private int multiplayerCheck, navigationCheck;
    private double nextMultiplayerCheck, nextNavigationCheck;
    private Guid navigationMap, navigationSet, beforeArrow;
    private bool coversChecked;
    private double coverWaitStarted;
    private float oldBrowse;
    private double oldVolume;
    private int oldHistory;
    private Vector2 dragPosition;
    private double unmodifiedStars;
    private Drawable? summaryBeforeMods, modTileBeforeBurst;

    // Apply inputs to the real root manager: includes focus, platform/global bindings,
    // positional queues, drag thresholds and click suppression after dragging.
    private void input(IInput change)
    {
        var manager = GetContainingInputManager();
        change.Apply(manager.CurrentState, manager);
    }

    private void key(Key key, bool shift = false)
    {
        if (shift) input(new KeyboardKeyInput(Key.ShiftLeft, true));
        input(new KeyboardKeyInput(key, true));
        input(new KeyboardKeyInput(key, false));
        if (shift) input(new KeyboardKeyInput(Key.ShiftLeft, false));
    }

    private bool CheckNativeMultiplayer(MainMenu menu, Drawable layer)
    {
        if (multiplayerCheck >= 3) return true;
        if (multiplayerCheck == 0 && (layer.Alpha <= 0 || !descendants((CompositeDrawable)layer).Any(d => d.Name == "legacy-menu-logo" && d.IsLoaded))) return false;
        if (Clock.CurrentTime < nextMultiplayerCheck) return false;
        nextMultiplayerCheck = Clock.CurrentTime + 600;
        var buttons = member<ButtonSystem>(menu, "Buttons")!;
        switch (multiplayerCheck++)
        {
            case 0:
                // The accelerated headless clock advances without OS activity during
                // imports; do not let the fixture's artificial idle state close Multi.
                buttons.ReturnToTopOnIdle = false;
                invoke(layer, "openMultiplayer");
                break;
            case 1:
                require(layer.Alpha == 0 && buttons.IsPresent && !SomsLegacyInputPatch.IsBlocked(buttons), $"Multiplayer must use live native menu and input: legacy={layer.Alpha}, buttons={buttons.Alpha}, present={buttons.IsPresent}, blocked={SomsLegacyInputPatch.IsBlocked(buttons)}, state={buttons.State}");
                require(buttons.State == ButtonSystemState.Multi, "Native multiplayer submenu not opened");
                require(descendants(buttons).Any(d => d.Name == "somsai-menu-button" && d.IsPresent), "Native SOMSAI entry missing");
                require(!descendants(menu).Any(d => d.Name == "soms-legacy-multiplayer-hub"), "Legacy multiplayer hub still attached");
                key(Key.Escape);
                break;
            case 2:
                require(layer.Alpha > 0 && member<string>(layer, "page") == "play", "Native multiplayer Back must restore legacy Play menu");
                require(click(menu, "legacy-menu-logo"), "Cannot close restored legacy menu");
                Console.WriteLine("PASS native multiplayer submenu/SOMSAI and Back to legacy menu");
                break;
        }
        return false;
    }

    private bool CheckLegacyNavigation(CompositeDrawable layer)
    {
        if (navigationCheck >= 33) return true;
        if (!CheckMapCovers(layer)) return false;
        if (Clock.CurrentTime < nextNavigationCheck) return false;
        nextNavigationCheck = Clock.CurrentTime + 600;
        Console.WriteLine("INPUT CHECK " + navigationCheck);
        var rows = member<Container>(layer, "rows")!;
        var carousel = member<object>(layer, "carousel")!;
        var search = descendants(layer).OfType<FocusedTextBox>().Single();
        var mods = member<ModSelectOverlay>(selection!, "modSelectOverlay")!;
        float browse() => (float)layer.GetType().GetField("browseCentre", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(layer)!;
        double volume() => member<VolumeMeter>(member<VolumeOverlay>(this, "volume")!, "volumeMeterMaster")!.Bindable.Value;
        int history() => member<IList>(carousel, "randomHistory")!.Count;
        void sameMap() => require(Beatmap.Value.BeatmapInfo.ID == navigationMap, "Browsing changed the selected beatmap");
        void lastDifficulty() => require(member<IEnumerable<GroupedBeatmap>>(layer, "filtered")!
            .Where(map => map.Beatmap.BeatmapSet?.ID == Beatmap.Value.BeatmapSetInfo.ID).Last().Beatmap.ID == Beatmap.Value.BeatmapInfo.ID,
            "Set navigation did not select the last visible difficulty");
        void wheel(float delta, bool precise = false) => input(new MouseScrollRelativeInput { Delta = new Vector2(0, delta), IsPrecise = precise });
        void point(Vector2 position) => input(new MousePositionAbsoluteInput { Position = position });

        switch (navigationCheck++)
        {
            case 0:
                CloseAllOverlays(); search.Text = ""; search.TakeFocus(); navigationMap = Beatmap.Value.BeatmapInfo.ID;
                navigationSet = Beatmap.Value.BeatmapSetInfo.ID;
                break;
            case 1:
                require(search.HasFocus, "Legacy search must hold focus during keyboard tests");
                key(Key.Right);
                break;
            case 2:
                require(Beatmap.Value.BeatmapInfo.ID != navigationMap, "Right arrow with search focus did not select next map");
                require(Beatmap.Value.BeatmapSetInfo.ID != navigationSet, "Right arrow stopped at another difficulty in the same set");
                require(Beatmap.Value.BeatmapInfo.Metadata.Title == "Native Legacy Integration second set", "Right arrow did not select the adjacent set in list order");
                lastDifficulty();
                key(Key.Left);
                break;
            case 3:
                require(Beatmap.Value.BeatmapSetInfo.ID == navigationSet, "Left arrow did not skip back to the previous whole set");
                lastDifficulty();
                navigationMap = Beatmap.Value.BeatmapInfo.ID;
                key(Key.Up);
                break;
            case 4:
                require(Beatmap.Value.BeatmapInfo.ID != navigationMap, "Global Up did not select previous difficulty");
                require(Beatmap.Value.BeatmapSetInfo.ID == navigationSet, "Up must retain difficulty-by-difficulty navigation");
                key(Key.Down);
                break;
            case 5:
                sameMap(); key(Key.F1);
                break;
            case 6:
                require(mods.State.Value == Visibility.Visible, "F1 did not open mods via global binding");
                key(Key.F1);
                break;
            case 7:
                require(mods.State.Value == Visibility.Hidden, "F1 did not close mods");
                key(Key.F3);
                break;
            case 8:
                require(member<Container>(layer, "options") != null, "F3 did not open map actions");
                key(Key.Right); key(Key.Down); key(Key.F2);
                break;
            case 9:
                sameMap(); key(Key.F3);
                break;
            case 10:
                require(member<Container>(layer, "options") == null, "F3 did not close map actions");
                oldHistory = history(); key(Key.F2);
                break;
            case 11:
                require(history() == oldHistory + 1, "F2 did not create a native random history entry");
                require(Beatmap.Value.BeatmapInfo.ID != navigationMap, "Random selection did not change map");
                key(Key.F2, shift: true);
                break;
            case 12:
                sameMap(); require(history() == oldHistory, "Shift+F2 did not rewind native random history");
                oldBrowse = browse(); oldVolume = volume();
                point(rows.ToScreenSpace(new Vector2(rows.DrawWidth / 2, 190))); wheel(3);
                break;
            case 13:
                sameMap(); require(browse() < oldBrowse, "Wheel over map rows did not browse independently");
                require(Math.Abs(volume() - oldVolume) < .00001, "Map wheel changed volume");
                oldBrowse = browse();
                point(layer.ToScreenSpace(new Vector2(layer.DrawWidth * .15f, layer.DrawHeight * .6f))); wheel(1);
                break;
            case 14:
                sameMap(); require(browse() < oldBrowse, "Wheel outside map rows did not browse");
                require(Math.Abs(volume() - oldVolume) < .00001, "Background wheel changed volume");
                oldBrowse = browse(); wheel(.25f, precise: true);
                break;
            case 15:
                sameMap(); require(Math.Abs(browse() - oldBrowse + .25f) < .01, "Precise wheel lost its fractional delta");
                oldBrowse = browse(); dragPosition = rows.ToScreenSpace(new Vector2(rows.DrawWidth / 2, 240));
                point(dragPosition); input(new MouseButtonInput(MouseButton.Left, true));
                break;
            case 16:
                point(dragPosition - new Vector2(0, 90));
                break;
            case 17:
                point(dragPosition - new Vector2(0, 170));
                break;
            case 18:
                input(new MouseButtonInput(MouseButton.Left, false));
                sameMap(); require(browse() > oldBrowse, "Dragging rows did not move the viewport");
                break;
            case 19:
                sameMap(); require(ScreenStack.CurrentScreen == selection, "Drag release started gameplay");
                var row = descendants(rows).First(d => d.Name == "soms-selection-difficulty-row" && d.DrawPosition.Y > 50 && d.DrawPosition.Y < 250);
                beforeArrow = Beatmap.Value.BeatmapInfo.ID;
                point(row.ToScreenSpace(new Vector2(100, 25)));
                input(new MouseButtonInput(MouseButton.Left, true)); input(new MouseButtonInput(MouseButton.Left, false));
                break;
            case 20:
                require(Beatmap.Value.BeatmapInfo.ID != beforeArrow, "Click after independent browsing did not select map");
                navigationMap = Beatmap.Value.BeatmapInfo.ID;
                oldBrowse = browse(); oldVolume = volume(); key(Key.F3);
                break;
            case 21:
                wheel(-2); key(Key.Escape);
                break;
            case 22:
                sameMap(); require(Math.Abs(browse() - oldBrowse) < .001, "Map-action overlay leaked wheel input");
                require(Math.Abs(volume() - oldVolume) < .00001, "Map-action wheel changed volume");
                point(layer.ToScreenSpace(new Vector2(layer.DrawWidth * .4f, layer.DrawHeight * .6f)));
                input(new KeyboardKeyInput(Key.AltLeft, true)); wheel(-1); input(new KeyboardKeyInput(Key.AltLeft, false));
                break;
            case 23:
                sameMap(); require(Math.Abs(browse() - oldBrowse) < .001, "Alt+wheel scrolled the map list");
                require(volume() < oldVolume, "Alt+wheel no longer adjusts volume");
                Console.WriteLine("PASS real keyboard routing, arrows, F1/F3, random rewind, wheel/precise wheel, drag, click and Alt+wheel");
                CloseAllOverlays();
                point(rows.ToScreenSpace(new Vector2(rows.DrawWidth / 2, rows.DrawHeight * .1f)));
                input(new MouseButtonInput(MouseButton.Right, true));
                oldBrowse = browse();
                break;
            case 24:
                point(rows.ToScreenSpace(new Vector2(rows.DrawWidth / 2, rows.DrawHeight * .9f)));
                break;
            case 25:
                input(new MouseButtonInput(MouseButton.Right, false));
                sameMap();
                float expectedBrowse = .9f * (member<IList>(layer, "displayed")!.Count - 1);
                require(Math.Abs(browse() - expectedBrowse) < .05f, $"Right drag did not scrub to pointer position: {browse()} != {expectedBrowse}");
                require(member<Container>(layer, "options") == null, "Right drag must suppress the map context click");
                require(ScreenStack.CurrentScreen == selection, "Right drag selected or started a map");
                var baseDifficulty = member<osu.Framework.Bindables.IBindable<StarDifficulty>>(layer, "summaryDifficulty");
                if (baseDifficulty?.Value.Stars is not > 0) { navigationCheck--; return false; }
                unmodifiedStars = baseDifficulty.Value.Stars;
                summaryBeforeMods = member<Drawable>(layer, "difficultyText");
                key(Key.F1);
                break;
            case 26:
                modTileBeforeBurst = descendants(mods).First(d => d.Name == "soms-legacy-mod-DT");
                var doubleTime = mods.AllAvailableMods.First(m => m.Mod.Acronym == "DT");
                for (int i = 0; i < 20; i++) doubleTime.Active.Value = !doubleTime.Active.Value;
                doubleTime.Active.Value = true;
                break;
            case 27:
                double adjusted = member<osu.Framework.Bindables.IBindable<StarDifficulty>>(layer, "summaryDifficulty")!.Value.Stars;
                if (Math.Abs(adjusted - unmodifiedStars) < .0001) { navigationCheck--; return false; }
                require(ReferenceEquals(summaryBeforeMods, member<Drawable>(layer, "difficultyText")), "Mod changes rebuilt the selected-map header");
                require(ReferenceEquals(modTileBeforeBurst, descendants(mods).First(d => d.Name == "soms-legacy-mod-DT")), "Mod burst recreated the mod tiles");
                require(member<SpriteText>(layer, "timingText")!.Text.ToString().Contains("180"), "DT did not update the 120 BPM display");
                key(Key.F1);
                // Recenter via normal selection to bring difficulty rows into view.
                key(Key.Up);
                break;
            case 28:
                var difficultyRows = descendants(rows).Where(d => d.Name == "soms-selection-difficulty-row").ToArray();
                if (difficultyRows.Length == 0 || difficultyRows.Any(d => member<osu.Framework.Bindables.IBindable<StarDifficulty>>(d, "difficulty")?.Value.Stars is not > 0)) { navigationCheck--; return false; }
                foreach (var difficultyRow in difficultyRows)
                {
                    double rating = member<osu.Framework.Bindables.IBindable<StarDifficulty>>(difficultyRow, "difficulty")!.Value.Stars;
                    require(Math.Abs(rating - unmodifiedStars) > .0001, "A visible row retained the unmodified star rating");
                    require(((osu.Game.Graphics.Containers.OsuClickableContainer)difficultyRow).TooltipText.ToString().Contains(rating.ToString("0.##")), "Row tooltip does not contain the adjusted difficulty");
                }
                mods.AllAvailableMods.First(m => m.Mod.Acronym == "DT").Active.Value = false;
                invoke(layer, "setGroup", "Artist");
                break;
            case 29:
                if (!descendants(rows).Any(d => d.Name == "soms-selection-collection-row")) { navigationCheck--; return false; }
                navigationMap = Beatmap.Value.BeatmapInfo.ID;
                invoke(layer, "setGroup", "Collections");
                break;
            case 30:
                if ((bool)member<object>(carousel, "IsFiltering")!) { navigationCheck--; return false; }
                sameMap();
                invoke(layer, "setGroup", "None");
                break;
            case 31:
                if (!descendants(rows).Any(d => d.Name == "soms-selection-difficulty-row")) { navigationCheck--; return false; }
                double restored = member<osu.Framework.Bindables.IBindable<StarDifficulty>>(layer, "summaryDifficulty")!.Value.Stars;
                if (Math.Abs(restored - unmodifiedStars) > .0001) { navigationCheck--; return false; }
                sameMap();
                break;
            case 32:
                Console.WriteLine("PASS RMB quick scrubbing without selection, native DT stars/BPM and restoration, stable mod/header drawables, Artist/Collections/None grouping");
                break;
        }
        return false;
    }

    private bool CheckMapCovers(CompositeDrawable layer)
    {
        if (coversChecked) return true;
        if (coverWaitStarted == 0) coverWaitStarted = Clock.CurrentTime;
        var maps = member<IEnumerable<GroupedBeatmap>>(layer, "filtered")!;
        require(maps.Any(m => m.Beatmap.BeatmapSet?.Files.Count == 0 && m.Beatmap.Metadata.BackgroundFile == "cover.png"), "Cover test must use native detached carousel snapshots with omitted Files");
        var rows = member<Container>(layer, "rows")!;
        var visible = descendants(rows).OfType<CompositeDrawable>()
            .Where(d => d.Name is "soms-selection-set-row" or "soms-selection-difficulty-row")
            .Where(d => d.Y >= 0 && d.Y + d.DrawHeight <= rows.DrawHeight).ToArray();
        bool ready = visible.Length > 0 && visible.All(row => descendants(row).OfType<Sprite>()
            .Any(sprite => sprite.Name == "soms-selection-cover-image" && sprite.Texture is { Width: 64, Height: 48 }));
        if (!ready)
        {
            if (Clock.CurrentTime - coverWaitStarted >= 20000)
                foreach (var row in visible)
                {
                    Console.WriteLine($"COVER {row.Name} y={row.Y} present={row.IsPresent}");
                    foreach (var d in descendants(row).Where(d => d is DelayedLoadWrapper || d.Name == "soms-selection-cover-image"))
                    {
                        var texture = (d as Sprite)?.Texture;
                        Console.WriteLine($"  {d.GetType().Name}: loaded={d.IsLoaded}, present={d.IsPresent}, size={d.DrawSize}, texture={texture?.Width}x{texture?.Height}");
                    }
                }
            require(Clock.CurrentTime - coverWaitStarted < 20000, "Visible map rows did not load the imported PNG thumbnails from detached metadata");
            return false;
        }
        coversChecked = true;
        Console.WriteLine("PASS real imported PNG thumbnails on visible legacy rows with empty snapshot Files");
        return true;
    }
}
