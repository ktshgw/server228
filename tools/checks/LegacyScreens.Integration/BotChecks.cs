using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Screens;
using osu.Game.Rulesets.EnhancedAuth.UI;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens.Play;
using osu.Game.Screens.Select;

internal sealed partial class IntegrationGame
{
    public bool BotOnly { get; init; }
    private int botStep;
    private SomsAiBotScreen? botScreen;

    private bool CheckBotPractice()
    {
        switch (botStep)
        {
            case 0:
                if (selection != null && ScreenStack.CurrentScreen != selection)
                {
                    selection!.MakeCurrent();
                    frames = 0;
                    return false;
                }
                CheckBotSimulation();
                ScreenStack.CurrentScreen.Push(botScreen = new SomsAiBotScreen());
                botStep++;
                frames = 0;
                return false;
            case 1:
                if (ScreenStack.CurrentScreen != botScreen || botScreen?.IsLoaded != true) return false;
                invoke(botScreen, "chooseMap");
                botStep++;
                frames = 0;
                return false;
            case 2:
                if (ScreenStack.CurrentScreen is not SoloSongSelect picker || !picker.IsLoaded) return false;
                picker.Mods.Value = new[] { picker.Ruleset.Value.CreateInstance().CreateAllMods().First(m => m.Acronym == "DT") };
                invoke(picker, "OnStart");
                botStep++;
                frames = 0;
                return false;
            case 3:
                if (ScreenStack.CurrentScreen != botScreen) return false;
                require(botScreen!.Beatmap.Value.BeatmapInfo.Metadata.Title.Contains("Native Legacy Integration"), "Practice picker must transfer the chosen map");
                require(botScreen.Mods.Value.Any(m => m.Acronym == "DT"), "Practice picker must preserve selected mods");
                invoke(botScreen!, "start");
                botStep++;
                frames = 0;
                return false;
            case 4:
                if (ScreenStack.CurrentScreen is not SomsAiBotPlayer player || !player.IsLoaded) return false;
                require(player.LoadedBeatmapSuccessfully, "Practice gameplay must load the chosen beatmap");
                require(player.GameplayState.Mods.Any(m => m.Acronym == "DT"), "Practice gameplay must use selected mods");
                var panel = descendants(player).FirstOrDefault(d => d.Name == "somsai-bot-live-score");
                require(panel != null, "Live bot panel missing; simulation=" + (member<SomsAiBotSimulation>(player, "bot") != null));
                if (!panel!.IsLoaded) return false;
                require((Player)player is not SubmittingPlayer, "Practice cannot use a submitting player");
                Console.WriteLine("PASS Bot lobby -> native map/mod picker -> real local practice Player + live score");
                botStep++;
                frames = 0;
                return false;
            case 5:
                if (ScreenStack.CurrentScreen != botScreen) return false;
                var results = member<FillFlowContainer>(botScreen!, "resultPanel")!;
                require(results.Children.Count == 4, "Completed duel must show winner, both scores and match tally");
                require((int)member<object>(botScreen!, "losses")! == 1, "No-input player must lose to a nonzero bot score");
                Console.WriteLine("PASS Bot duel completed and returned to ocean lobby with a result; no ranking submission");
                return true;
        }
        return false;
    }

    private void CheckBotSimulation()
    {
        foreach (int mode in new[] { 0, 1, 2, 3 })
        {
            var ruleset = RulesetStore.GetRuleset(mode)!.CreateInstance();
            var playable = Beatmap.Value.GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());
            foreach (var level in Enum.GetValues<SomsAiBotLevel>())
            {
                var sim = new SomsAiBotSimulation(ruleset, playable, Array.Empty<Mod>(), level, 7, 873);
                sim.Advance(-100000);
                require(sim.AppliedCount == 0 && sim.Processor.TotalScore.Value == 0, "Bot must wait for gameplay");
                sim.Advance(double.PositiveInfinity);
                long final = sim.Processor.TotalScore.Value;
                double accuracy = sim.Processor.Accuracy.Value;
                require(sim.AppliedCount == sim.ObjectCount, "Bot must judge all nested objects");
                require(final >= 0 && final <= sim.Processor.MaximumTotalScore && accuracy >= 0 && accuracy <= 1, "Bot must use valid native scores");
                if (level == SomsAiBotLevel.Mrekk)
                    require(sim.Processor.HighestCombo.Value == sim.Processor.MaximumCombo, "mrekk must FC every ruleset, including slider ticks");
                sim.Advance(-100000);
                require(sim.Processor.TotalScore.Value == 0, "Rewind must undo bot score");
                sim.Advance(double.PositiveInfinity);
                require(sim.Processor.TotalScore.Value == final && Math.Abs(sim.Processor.Accuracy.Value - accuracy) < 1e-9, "Seek must replay deterministic judgements");
                sim.Processor.Dispose();
            }
        }
        var osu = RulesetStore.GetRuleset(0)!.CreateInstance();
        var map = Beatmap.Value.GetPlayableBeatmap(osu.RulesetInfo, Array.Empty<Mod>());
        long easy = 0, hard = 0, easyMap = 0;
        for (int seed = 0; seed < 100; seed++)
        {
            long score(SomsAiBotLevel level, double stars)
            {
                var sim = new SomsAiBotSimulation(osu, map, Array.Empty<Mod>(), level, stars, seed);
                sim.Advance(double.PositiveInfinity);
                long value = sim.Processor.TotalScore.Value;
                sim.Processor.Dispose();
                return value;
            }
            easy += score(SomsAiBotLevel.Easy, 8);
            hard += score(SomsAiBotLevel.Impossible, 8);
            easyMap += score(SomsAiBotLevel.Easy, 2);
        }
        require(hard > easy && easyMap > easy, "Bot strength and map difficulty must affect score distributions");
        require(new SomsAiBotResult(1, .99, 1, 1000000, .98, 100, true).Winner > 0, "mrekk duel must compare accuracy, not score");
        Console.WriteLine("PASS Six bot levels in all four rulesets: valid native scores, mrekk FC, deterministic rewind, map/skill scaling");
    }
}
