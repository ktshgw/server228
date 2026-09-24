using System.Reflection;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input;
using osu.Framework.Input.States;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.Settings.Sections;
using osu.Game.Overlays.Settings.Sections.UserInterface;
using osu.Game.Overlays.Settings.Sections.Audio;
using osu.Game.Overlays.Volume;
using osu.Game.Rulesets.EnhancedAuth.Configuration;
using osu.Game.Rulesets.EnhancedAuth.Patches;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Play;
using osu.Game.Skinning;
using osuTK;
using osuTK.Input;

internal sealed partial class IntegrationGame
{
    private SkinSection activitySkin = null!;
    private SongSelectSettings activitySong = null!;
    private GeneralSettings activityGeneral = null!;
    private AudioDevicesSettings activityAudio = null!;
    private Task<double[]>? previewTask;
    private double[]? basePP;
    private int previewVisibilityStep;
    private double previewWait;
    private Drawable smoothTrailProbe = null!;
    private void InitActivityChecks()
    {
        string path = Path.Combine(profile, "migration-check.json");
        File.WriteAllText(path, "{\"ShowSliderEndMiss\":false}");
        var prefs = new SomsClientPreferences(path);
        require(prefs.SliderMissDisplay.Value == SomsSliderMissDisplay.None && prefs.DrawFollowPoints.Value, "Old disabled misses migrate to None; followpoints default on");
        prefs.SliderMissDisplay.Value = SomsSliderMissDisplay.Judgements;
        prefs = new SomsClientPreferences(path);
        require(prefs.SliderMissDisplay.Value == SomsSliderMissDisplay.Judgements && prefs.ShowSliderEndMiss.Value, "Slider miss style persists and preserves old visibility binding");
        SomsClientPreferences.Instance.ShowMapPP.Value = false;
        var trailType = Assembly.Load("osu.Game.Rulesets.Osu").GetType("osu.Game.Rulesets.Osu.Skinning.Legacy.LegacyCursorTrail")!;
        ActivitySkinProbe.Texture = Dependencies.Get<osu.Framework.Graphics.Rendering.IRenderer>().CreateTexture(64, 64);
        smoothTrailProbe = (Drawable)Activator.CreateInstance(trailType, new object[] { DispatchProxy.Create<ISkin, ActivitySkinProbe>() })!;
        Add(new osu.Framework.Graphics.Containers.Container { Size = new Vector2(600, 400), Alpha = 0, AlwaysPresent = true, Child = smoothTrailProbe });
        SomsClientPreferences.Instance.EnhancedVolume.Value = true;
        Add(new ScaleSettingsHost { Alpha = 0, AlwaysPresent = true, Children = new Drawable[]
            { activitySkin = new SkinSection(), activitySong = new SongSelectSettings(), activityGeneral = new GeneralSettings(), activityAudio = new AudioDevicesSettings() } });
    }

    private void CheckActivitySettings()
    {
        require(activitySkin.IsLoaded && activitySong.IsLoaded && activityGeneral.IsLoaded, "New settings must load with real dependencies");
        var sliders = activitySkin.Children.OfType<SettingsItemV2>().Where(item => item.Name == "soms-skin-sliderendmiss").ToArray();
        require(sliders.Length == 1 && sliders[0].Control is FormDropdown<SomsSliderMissDisplay>, "Exactly one slider-miss dropdown");
        var post = typeof(SomsSkinElementSettingsPatch).GetMethod("Postfix", BindingFlags.Static | BindingFlags.NonPublic)!;
        for (int i = 0; i < 20; i++) post.Invoke(null, new object[] { activitySkin });
        require(activitySkin.Children.Count(item => item.Name == "soms-skin-followpoints") == 1, "Repeated skin settings never duplicate followpoints");
        require(activitySong.Children.Count(item => item.Name == "soms-gameplay-map-pp") == 1, "Song select contains pp toggle");
        require(!activityGeneral.Children.Any(item => item.Name == "soms-gameplay-volume"), "Volume toggle removed from Interface");
        var audioFlow = member<FillFlowContainer>(activityAudio, "FlowContent")!;
        var audioItems = audioFlow.Children.OrderBy(audioFlow.GetLayoutPosition).ToArray();
        require(audioItems[1].Name == "soms-gameplay-volume", "Enhanced volume is immediately after the output device");
        require(audioItems[2] is SettingsItemV2 legacyAudio && legacyAudio.Control.GetType().Name == "LegacyAudioCheckbox", "Enhanced volume must be above legacy audio");

        var volume = member<VolumeOverlay>(this, "volume")!;
        var master = member<VolumeMeter>(volume, "volumeMeterMaster")!;
        master.Bindable.Value = .5;
        volume.FocusMasterVolume();
        var input = GetContainingInputManager();
        var state = new InputState(); state.Keyboard.Keys.SetPressed(Key.LAlt, true); state.Mouse.Scroll = Vector2.UnitY;
        var handled = typeof(InputManager).GetMethod("handleScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(input, new object[] { state, Vector2.Zero, false });
        Console.WriteLine($"VOLUME handled={handled} master={master.Bindable.Value} enabled={SomsClientPreferences.Enabled} input={input.GetType().Name} action={Enum.GetName(typeof(osu.Game.Input.Bindings.GlobalAction), (int)osu.Game.Input.Bindings.GlobalAction.IncreaseVolume)}");
        require(handled is true && master.Bindable.Value > .5 && master.Bindable.Value <= .6, "Alt wheel adjusts volume once before screen handlers");

        var trailType = Assembly.Load("osu.Game.Rulesets.Osu").GetType("osu.Game.Rulesets.Osu.Skinning.Legacy.LegacyCursorTrail")!;
        var trail = Activator.CreateInstance(trailType, new object?[] { null })!;
        trailType.GetProperty("DisjointTrail")!.SetValue(trail, true);
        SomsClientPreferences.Instance.ForceSmoothCursorTrail.Value = true;
        require(trailType.GetProperty("DisjointTrail")!.GetValue(trail) is false, "Force smooth overrides absent cursormiddle");
        SomsClientPreferences.Instance.ForceSmoothCursorTrail.Value = false;
        require(trailType.GetProperty("DisjointTrail")!.GetValue(trail) is true, "Turning smooth off restores skin behavior");
        ((IDisposable)trail).Dispose();
        CheckSmoothTrailSampling();
        CheckSliderStyles();
        Console.WriteLine("PASS migrated preferences, native settings, smooth trail and global Alt-wheel");
    }

    private void CheckSmoothTrailSampling()
    {
        require(smoothTrailProbe.IsLoaded, "Native skinned trail is dependency-loaded");
        var type = smoothTrailProbe.GetType();
        type.GetProperty("DisjointTrail")!.SetValue(smoothTrailProbe, true);
        SomsClientPreferences.Instance.ForceSmoothCursorTrail.Value = true;
        var sample = type.BaseType!.GetMethod("AddTrail", BindingFlags.Instance | BindingFlags.NonPublic)!;
        sample.Invoke(smoothTrailProbe, new object[] { smoothTrailProbe.ToScreenSpace(new Vector2(10, 20)) });
        sample.Invoke(smoothTrailProbe, new object[] { smoothTrailProbe.ToScreenSpace(new Vector2(410, 20)) });
        var parts = member<Array>(smoothTrailProbe, "parts")!;
        var positions = parts.Cast<object>().Where(part => (long)part.GetType().GetField("InvalidationID")!.GetValue(part)! >= 0)
            .Select(part => (Vector2)part.GetType().GetField("Position")!.GetValue(part)!).ToArray();
        require(positions.Length >= 30 && positions.Max(p => p.X) >= 400, "Dense trail reaches the cursor with no exclusion gap");
        require(positions.Zip(positions.Skip(1)).All(pair => Vector2.Distance(pair.First, pair.Second) <= 10.01), "Quarter-width McOsu interpolation has no holes between samples");
        require(smoothTrailProbe.Blending == BlendingParameters.Additive, "Enabling smooth updates blending on an existing skin");
        SomsClientPreferences.Instance.ForceSmoothCursorTrail.Value = false;
        type.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(smoothTrailProbe, null);
        require(smoothTrailProbe.Blending == BlendingParameters.Inherit && type.GetProperty("DisjointTrail")!.GetValue(smoothTrailProbe) is true, "Turning off smooth restores native skin rendering");
        Console.WriteLine($"PASS native trail buffer: {positions.Length} samples, last x={positions.Max(p => p.X)}, no cursor gap, native restoration");
    }

    private bool CheckActivityPreview()
    {
        var panel = descendants(selection!).OfType<SomsMapPerformance>().Single();
        var titleWedge = (osu.Game.Screens.Select.BeatmapTitleWedge)member<Drawable>(panel, "Parent")!;
        if (previewVisibilityStep < 4)
        {
            if (previewVisibilityStep == 0)
            {
                foreach (string name in new[] { "titleLabel", "artistLabel" })
                    member<MarqueeContainer>(titleWedge, name)!.CreateContent = () => new osu.Game.Graphics.Sprites.OsuSpriteText
                    { Text = string.Concat(Enumerable.Repeat("SOMS! Very long title (Nightcore & Cut Ver.) — ", 15)), Font = osu.Game.Graphics.OsuFont.GetFont(size: 28) };
                SomsClientPreferences.Instance.LegacyInterface.Value = false;
                previewWait = Time.Current + 500;
                previewVisibilityStep = 1;
                return false;
            }
            if (Time.Current < previewWait) return false;
            if (previewVisibilityStep == 1 || previewVisibilityStep == 3)
            {
                require(panel.Alpha == 0, "Disabled pp panel must be hidden");
                CheckTitleClip(panel, titleWedge, false);
                SomsClientPreferences.Instance.ShowMapPP.Value = true;
                previewVisibilityStep++;
                previewWait = Time.Current + 500;
                return false;
            }
            if (panel.Alpha == 0 || descendants(panel).OfType<osu.Game.Graphics.Sprites.OsuSpriteText>().Any(t => t.Text.ToString() is "—" or "…")) return false;
            require(panel.IsPresent && panel.DrawWidth > 200 && panel.DrawHeight > 50, "Enabled pp panel has actual visible geometry");
            var wedge = (osu.Game.Screens.Select.BeatmapTitleWedge)member<Drawable>(panel, "Parent")!;
            CheckAccuracyPills(panel, wedge);
            CheckTitleClip(panel, wedge, true);
            require(panel.Depth < descendants(wedge).First(d => d.GetType().Name == "WedgeBackground").Depth, "PP must draw in front of the native wedge background");
            var rect = panel.ScreenSpaceDrawQuad.AABB;
            var outer = wedge.ScreenSpaceDrawQuad.AABB;
            require(outer.Contains(new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f)), "PP must be inside the actual song header, not clipped outside");
            for (Drawable? parent = panel; parent != null; parent = member<Drawable>(parent, "Parent"))
                require(parent.Alpha > 0, "Every PP ancestor must be visible in native song select");
            SomsClientPreferences.Instance.ShowMapPP.Value = false;
            previewVisibilityStep = 3;
            previewWait = Time.Current + 500;
            return false;
        }
        if (panel.Alpha == 0 || descendants(panel).OfType<osu.Game.Graphics.Sprites.OsuSpriteText>().Any(t => t.Text.ToString() is "—" or "…")) return false;
        SomsClientPreferences.Instance.LegacyInterface.Value = true;
        if (previewTask == null)
        {
            var working = Beatmap.Value;
            var ruleset = Ruleset.Value;
            previewTask = Task.Run(() => SomsMapPerformance.Calculate(working, ruleset, Array.Empty<Mod>(), CancellationToken.None));
            return false;
        }
        if (!previewTask.IsCompleted) return false;
        if (basePP == null)
        {
            basePP = previewTask.GetAwaiter().GetResult();
            require(basePP.All(v => double.IsFinite(v) && v > 0), "Native calculator must return four pp estimates");
            var working = Beatmap.Value;
            var ruleset = Ruleset.Value;
            var dt = (ModDoubleTime)ruleset.CreateInstance().CreateMod<ModDoubleTime>();
            dt.SpeedChange.Value = 1.2;
            previewTask = Task.Run(() => SomsMapPerformance.Calculate(working, ruleset, new Mod[] { dt }, CancellationToken.None));
            return false;
        }
        var modPP = previewTask.GetAwaiter().GetResult();
        require(Math.Abs(modPP[3] - basePP[3]) > .001, "Custom DT speed must affect pp estimate");
        require(descendants(selection!).Count(d => d.Name == "soms-map-pp") == 1, "One pp panel on actual song-select wedge");
        Console.WriteLine($"PASS native pp NM={basePP[3]:F3} DT1.2={modPP[3]:F3}");
        return true;
    }

    private void CheckSliderStyles()
    {
        var source = DispatchProxy.Create<ISkinSource, ActivitySkinProbe>();
        var lookup = new SkinComponentLookup<osu.Game.Rulesets.Scoring.HitResult>(osu.Game.Rulesets.Scoring.HitResult.IgnoreMiss);
        using var drawable = new SkinnableDrawable(lookup);
        var changed = typeof(SkinnableDrawable).GetMethod("SkinChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        SomsClientPreferences.Instance.SliderMissDisplay.Value = SomsSliderMissDisplay.Judgements;
        changed.Invoke(drawable, new object[] { source });
        require(drawable.Drawable.Name == "Ok", "sliderendmiss must resolve the skin's hit100 component");
        require(ReferenceEquals(member<ISkinComponentLookup>(drawable, "ComponentLookup"), lookup), "Temporary style lookup must restore the original judgement identity");
        using var tick = new SkinnableDrawable(new SkinComponentLookup<osu.Game.Rulesets.Scoring.HitResult>(osu.Game.Rulesets.Scoring.HitResult.LargeTickMiss));
        changed.Invoke(tick, new object[] { source });
        require(tick.Drawable.Name == "Meh", "slidertickmiss must resolve the skin's hit50 component");
        SomsClientPreferences.Instance.SliderMissDisplay.Value = SomsSliderMissDisplay.None;
        changed.Invoke(drawable, new object[] { source });
        require(drawable.Alpha == 0, "None must hide slider misses");
        SomsClientPreferences.Instance.SliderMissDisplay.Value = SomsSliderMissDisplay.Default;
        changed.Invoke(drawable, new object[] { source });
        require(drawable.Drawable.Name == "IgnoreMiss" && drawable.Alpha > 0, "Default restores native miss lookup and visibility");
        Console.WriteLine("PASS sliderendmiss -> hit100, slidertickmiss -> hit50, None and native restoration");
    }

    private void CheckActivitySeek(Player player)
    {
        var overlay = descendants(player).OfType<SomsSeekOverlay>().Single();
        CheckSeekCursorHidden(player);
        var clock = member<GameplayClockContainer>(player, "GameplayClockContainer")!;
        clock.Stop();
        require(!SomsGameplaySeek.IsPractice(player), "Opening the player does not make the score practice");
        require(OnPressed(new osu.Framework.Input.Events.KeyBindingPressEvent<osu.Game.Input.Bindings.GlobalAction>(
            new InputState(), (osu.Game.Input.Bindings.GlobalAction)(int)SomsSkinAction.GameplaySeek, false)), "Real global hotkey dispatch must find the player without reaching skin-slot actions");
        overlay.SeekToFraction(.7);
        typeof(SomsSeekOverlay).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(overlay, null);
        require(overlay.ProvidingUserCursor && overlay.Cursor is osu.Game.Graphics.Cursor.MenuCursorContainer, "Scrubbing offers the native menu cursor while paused");
        require(overlay.Cursor.State.Value == Visibility.Visible && overlay.Cursor.Depth < member<Drawable>(overlay, "timeline")!.Depth, "Menu cursor is visible above the timeline");
        require(member<Drawable>(overlay, "cursorLayer")!.Alpha == 1, "Held scrub cursor layer is visible");
        double forward = clock.CurrentTime;
        overlay.SeekToFraction(.1);
        require(clock.CurrentTime < forward && SomsGameplaySeek.IsPractice(player), "Forward/backward seek changes time and invalidates score submission");
        SomsGameplaySeek.SetHeld(false);
        typeof(SomsSeekOverlay).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(overlay, null);
        require(!overlay.ProvidingUserCursor && overlay.Cursor.State.Value == Visibility.Hidden, "Release restores normal gameplay cursor ownership");
        CheckSeekCursorHidden(player);
        clock.Start();
        var submissionType = typeof(Player).Assembly.GetType("osu.Game.Screens.Play.SubmittingPlayer")!;
        if (submissionType.IsInstanceOfType(player))
            require(((Task)submissionType.GetMethod("submitScore", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(player, new object[] { player.GameplayState.Score })!).IsCompletedSuccessfully,
                "Practice submission must stop before the API request");
        // Exercise the real patched submission method even when this run uses ReplayPlayer.
        // Any attempt to access API/token state on this uninitialised object would fail.
        var solo = (Player)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SoloPlayer));
        var soloOverlay = new SomsSeekOverlay(solo);
        typeof(SomsSeekOverlay).GetProperty("Used")!.SetValue(soloOverlay, true);
        var registry = typeof(SomsGameplaySeek).GetField("Players", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        registry.GetType().GetMethod("Add")!.Invoke(registry, new object[] { solo, soloOverlay });
        require(((Task)submissionType.GetMethod("submitScore", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(solo, new object[] { player.GameplayState.Score })!).IsCompletedSuccessfully, "No API access for practice solo score");
        registry.GetType().GetMethod("Remove")!.Invoke(registry, new object[] { solo });
        soloOverlay.Dispose();
        Console.WriteLine("PASS real gameplay scrubbing forward/backward and submission guard");
    }

    private void CheckSeekCursorHidden(Player player)
    {
        var overlay = descendants(player).OfType<SomsSeekOverlay>().Single();
        require(!overlay.ProvidingUserCursor, "An idle seek overlay cannot take cursor ownership");
        require(overlay.Cursor.DrawColourInfo.Colour.TopLeft.Linear.A == 0, "Seek cursor must actually render with zero alpha on entry, release and retry, not only have Hidden state");
        Console.WriteLine("PASS no extra menu cursor: native draw alpha is zero");
    }

    private void CheckTitleClip(SomsMapPerformance panel, osu.Game.Screens.Select.BeatmapTitleWedge wedge, bool clipped)
    {
        var update = typeof(SomsMapPerformance).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!;
        float width = wedge.Width;
        foreach (float fraction in clipped ? new[] { 1f, .75f, .55f } : new[] { 1f })
        {
            wedge.Width = width * fraction;
            update.Invoke(panel, null);
            foreach (string name in new[] { "titleLabel", "artistLabel" })
            {
                var label = member<MarqueeContainer>(wedge, name)!;
                var link = member<Drawable>(label, "Parent")!;
                var row = member<Container>(link, "Parent")!;
                require(label.Masking == clipped, "Title and artist restore their native masking when pp is disabled");
                if (clipped)
                {
                    float edge = Math.Min(panel.ToSpaceOfOtherDrawable(Vector2.Zero, label).X,
                        panel.ToSpaceOfOtherDrawable(new Vector2(0, panel.DrawHeight), label).X);
                    require(label.DrawWidth >= 0 && (label.DrawWidth == 0 || label.DrawWidth <= edge - 11), "Marquee clip ends to the left of pp at resized header widths");
                    require(member<Drawable>(label, "flow")!.DrawWidth > label.DrawWidth, "Fixture really overflows; the marquee's copies must be clipped");
                }
                else require(row.Padding.Right == 0 && label.DrawWidth == row.DrawWidth, "Disabling pp restores the whole title width");
            }
        }
        wedge.Width = width;
        update.Invoke(panel, null);
        Console.WriteLine($"PASS title/artist clipping={clipped}: real long marquees, counter edge, resized widths and native restoration");
    }

    private void CheckAccuracyPills(SomsMapPerformance panel, osu.Game.Screens.Select.BeatmapTitleWedge wedge)
    {
        var star = descendants(wedge).OfType<osu.Game.Beatmaps.Drawables.StarRatingDisplay>().Single();
        var update = typeof(SomsMapPerformance).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var backgrounds = member<osu.Framework.Graphics.Shapes.Box[]>(panel, "accuracyBackgrounds")!;
        var labels = member<osu.Game.Graphics.Sprites.OsuSpriteText[]>(panel, "accuracyLabels")!;
        require(descendants(panel).OfType<CircularContainer>().Count(d => d.Name.StartsWith("soms-pp-accuracy-")) == 4, "All four percentages have capsule backgrounds");
        // Exercise the actual native spectrum, including dark/high-star pills.
        var displayed = (Bindable<double>)star.DisplayedStars;
        double original = displayed.Value;
        foreach (double stars in new[] { 2.0, 5.57, 8.0 })
        {
            displayed.Value = stars;
            update.Invoke(panel, null);
            require(backgrounds.All(box => box.Colour == star.DisplayedDifficultyColour), "Percentage backgrounds match native difficulty colour");
            require(labels.All(label => label.Colour == star.DisplayedDifficultyTextColour), "Percentage text matches native contrast colour");
        }
        displayed.Value = original;
        update.Invoke(panel, null);
        Console.WriteLine("PASS four accuracy pills follow native difficulty background and contrast colours");
    }
}

public class ActivitySkinProbe : DispatchProxy
{
    public static osu.Framework.Graphics.Textures.Texture? Texture;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "GetTexture") return Texture;
        if (method?.Name == "GetDrawableComponent")
            return new osu.Framework.Graphics.Shapes.Box { Name = ((SkinComponentLookup<osu.Game.Rulesets.Scoring.HitResult>)args![0]!).Component.ToString() };
        return null;
    }
}
