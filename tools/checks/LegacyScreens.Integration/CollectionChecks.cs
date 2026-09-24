using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input;
using osu.Framework.Input.StateChanges;
using osu.Game.Beatmaps;
using osu.Game.Collections;
using osu.Game.Database;
using osu.Game.Graphics.UserInterface;
using osuTK.Input;

internal sealed partial class IntegrationGame
{
    private int collectionStep;
    private double nextCollectionCheck;
    private Guid collectionId, contextMap;
    private string contextHash = "";

    private bool CheckLegacyCollections(CompositeDrawable layer)
    {
        if (collectionStep >= 17) return true;
        if (Clock.CurrentTime < nextCollectionCheck) return false;
        nextCollectionCheck = Clock.CurrentTime + 400;
        var realm = member<RealmAccess>(this, "realm")!;
        BeatmapCollection? saved() => realm.Realm.Find<BeatmapCollection>(collectionId);
        var menu = member<Container>(layer, "options");
        Console.WriteLine("COLLECTION CHECK " + collectionStep);
        switch (collectionStep++)
        {
            case 0:
                var rows = member<Container>(layer, "rows")!;
                var target = descendants(rows).First(d => d.Name == "soms-selection-difficulty-row"
                    && d.Y > 20 && d.Y + d.DrawHeight < rows.DrawHeight && member<BeatmapInfo>(d, "map")!.ID != Beatmap.Value.BeatmapInfo.ID);
                var map = member<BeatmapInfo>(target, "map")!;
                contextMap = map.ID; contextHash = map.MD5Hash;
                input(new MousePositionAbsoluteInput { Position = target.ScreenSpaceDrawQuad.Centre });
                input(new MouseButtonInput(MouseButton.Right, true));
                require(member<Container>(layer, "options") == null, "Right press must wait for click/drag distinction");
                input(new MouseButtonInput(MouseButton.Right, false));
                break;
            case 1:
                require(menu?.Name == "soms-selection-options-menu", "Right click must show F3 actions");
                require(Beatmap.Value.BeatmapInfo.ID == contextMap, "Right click must target the clicked difficulty");
                key(Key.Number1);
                break;
            case 2:
                require(menu?.Name == "soms-selection-collections-menu" && menu.IsLoaded, "First F3 action must open legacy collection manager");
                var inputBox = descendants(menu!).OfType<OsuTextBox>().Single();
                inputBox.Text = "Collection 123"; GetContainingFocusManager().ChangeFocus(inputBox); key(Key.Enter);
                break;
            case 3:
                var created = realm.Realm.All<BeatmapCollection>().Single(c => c.Name == "Collection 123");
                collectionId = created.ID;
                require(created.BeatmapMD5Hashes.Count == 0, "Creating a collection must not add maps implicitly");
                require(click(menu!, "soms-collection-add-map-" + collectionId), "Single-difficulty add button unavailable");
                break;
            case 4:
                require(saved()!.BeatmapMD5Hashes.SequenceEqual(new[] { contextHash }), "+ must add only the clicked difficulty");
                click(menu!, "soms-collection-add-map-" + collectionId);
                require(saved()!.BeatmapMD5Hashes.Count == 1, "Repeated + must not duplicate hashes");
                click(menu!, "soms-collection-add-set-" + collectionId);
                break;
            case 5:
                var all = realm.Realm.Find<BeatmapInfo>(contextMap)!.BeatmapSet!.Beatmaps.Select(b => b.MD5Hash).ToHashSet();
                require(all.Count == 18 && all.SetEquals(saved()!.BeatmapMD5Hashes), "+Set must resolve the complete installed set");
                click(menu!, "soms-collection-remove-map-" + collectionId);
                break;
            case 6:
                require(saved()!.BeatmapMD5Hashes.Count == 17 && !saved()!.BeatmapMD5Hashes.Contains(contextHash), "Minus removes only the selected difficulty");
                click(menu!, "soms-collection-remove-set-" + collectionId);
                break;
            case 7:
                require(saved()!.BeatmapMD5Hashes.Count == 0, "Minus Set removes all set difficulties");
                click(menu!, "soms-collection-add-map-" + collectionId);
                break;
            case 8:
                if (member<object>(menu!, "renaming") == null)
                {
                    require(click(menu!, "soms-collection-rename-" + collectionId), "Rename button must load after membership refresh");
                    collectionStep--; return false;
                }
                var renameBox = descendants(menu!).OfType<OsuTextBox>().Single();
                require(renameBox.HasFocus, "Rename focuses its text field");
                renameBox.Text = "Renamed 456"; key(Key.Enter);
                break;
            case 9:
                require(saved()!.Name == "Renamed 456" && saved()!.BeatmapMD5Hashes.Contains(contextHash), "Rename preserves membership");
                key(Key.Escape);
                break;
            case 10:
                require(menu == null, "Escape closes collections");
                require(click(layer, "soms-selection-tab-Collections"), "Collections grouping tab missing");
                break;
            case 11:
                var folder = descendants(member<Container>(layer, "rows")!).FirstOrDefault(d => d.Name == "soms-selection-collection-row");
                if (folder == null) { collectionStep--; return false; }
                require(folder.TriggerClick(), "Native collection group cannot open");
                break;
            case 12:
                key(Key.F3); key(Key.Number1);
                break;
            case 13:
                require(menu?.Name == "soms-selection-collections-menu", "Collections must reopen through F3");
                require(click(menu!, "soms-collection-select-" + collectionId), "Saved collection not visible after reopening");
                click(menu!, "soms-collection-delete");
                require(saved() != null, "Deletion requires confirmation");
                break;
            case 14:
                click(menu!, "soms-collection-delete");
                require(saved() == null, "Confirmed deletion must persist");
                require(realm.Realm.Find<BeatmapInfo>(contextMap) != null, "Collection deletion must retain beatmap files");
                key(Key.Escape); invoke(layer, "setGroup", "None");
                break;
            case 15:
                if (member<Container>(layer, "options") != null) throw new Exception("Collection overlay leaked after close");
                break;
            case 16:
                Console.WriteLine("PASS real RMB context actions; collection create/rename/persistence; +, +Set, minus, minus Set; live native grouping; confirmed deletion; no map removal");
                break;
        }
        return false;
    }
}
