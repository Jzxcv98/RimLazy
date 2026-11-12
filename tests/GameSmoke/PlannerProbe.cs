using System.Text.Json;
using RimWorld;
using StrategicWork.Core;
using StrategicWork.RimWorld;
using Verse;

namespace RimLazy.GameSmoke;

// The runner installs this assembly only into its disposable game copy.
public sealed class PlannerProbe(Map map) : MapComponent(map)
{
    internal const string LegacyId = "frozensnowfox.complexjobs";
    private static readonly string? Scenario = Environment.GetEnvironmentVariable("RIMLAZY_GAME_SMOKE");
    private static readonly List<string> Checks = [];
    private static Dictionary<string, int>? original;
    private static Map? oldMap, bootstrapMap;
    private static bool done;
    private static int frames, edits, workers, columns, maxPriority, legacyEdits;
    private static string? lockKey;

    internal static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
        Checks.Add(label);
    }

    private static Dictionary<string, int> Table(ColonyCapture capture) =>
        capture.Inputs.SelectMany(p => p.Cells.Select(cell =>
            (Key: ColonyAdapter.CellKey(p.Id, cell.Key), cell.Value.Current))).ToDictionary(x => x.Key, x => x.Current);

    private ColonyCapture Capture(ColonyWorkState state) =>
        ColonyAdapter.Capture(map, state.Overrides, state.LockedCells);

    public override void MapComponentUpdate()
    {
        if (done || Scenario is not ("native" or "coexist" or "migration" or "all-splits" or "legacy-settings" or "change-layout") ||
            Current.ProgramState != ProgramState.Playing || Find.CurrentMap != map ||
            map.mapPawns.FreeColonistsSpawned.Count == 0 || frames++ < 12) return;
        if (!Environment.GetCommandLineArgs().Contains("-quicktest", StringComparer.OrdinalIgnoreCase) ||
            !Path.GetFileName(GenFilePaths.SaveDataFolderPath).StartsWith("smoke-", StringComparison.Ordinal)) return;
        try
        {
            var state = map.GetComponent<ColonyWorkState>();
            if (oldMap != null)
            {
                if (oldMap != map) VerifyReload(state);
                return;
            }
            if (Scenario is "migration" or "change-layout")
            {
                if (bootstrapMap == null)
                {
                    bootstrapMap = map;
                    LongEventHandler.ExecuteWhenFinished(() => GameDataSaveLoader.LoadGame("RimLazy-migration-input"));
                    return;
                }
                if (bootstrapMap == map) return;
                if (Scenario == "change-layout") VerifyChangedLayout(state);
                VerifyMigration(state);
            }
            ExerciseWorkPlanner(state);
        }
        catch (Exception ex) { Finish(ex.ToString()); }
    }

    private void VerifyChangedLayout(ColonyWorkState state)
    {
        var expected = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(
            File.ReadAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, "named-priorities.json")))!;
        var snapshot = Capture(state);
        Check(WorkSplitChecks.OptionalIds.All(id => snapshot.Works.Any(w => w.Id == id)), "Reopened native default-layout save with every optional split enabled");
        foreach (var pawn in snapshot.Inputs)
        {
            var saved = expected[pawn.Id];
            Check(pawn.Cells.All(c => saved.GetValueOrDefault(c.Key) == c.Value.Current),
                $"Every work priority for {pawn.Id} matches its saved work-type ID, including disabled new jobs");
        }
    }

    private void VerifyMigration(ColonyWorkState state)
    {
        // The archived v0.1 save recorded both priorities and stable pawn/work IDs in its undo journal.
        // Check the game's actual decoded grid before the planner or the UI can modify it.
        var legacy = state.LastUndo ?? throw new InvalidOperationException("Expected v0.1 undo journal in legacy QA save");
        var live = Table(Capture(state));
        Check(legacy.MapId == map.uniqueID && legacy.Edits.Count > 0, "Loaded source map and its original undo journal");
        Check(legacy.Edits.Any(e => e.WorkId.StartsWith("FSF", StringComparison.Ordinal) && e.After == 6),
            "Source save contains sixth-tier work on a subdivided job");
        foreach (var edit in legacy.Edits)
        {
            string key = ColonyAdapter.CellKey(edit.PawnId, edit.WorkId);
            if (!live.TryGetValue(key, out int current) || current != edit.After)
                throw new InvalidOperationException($"Legacy cell {key} should be {edit.After}, found {current}");
        }
        legacyEdits = legacy.Edits.Count;
        Check(state.LockedCells.All(live.ContainsKey) && state.LockedCells.Count > 0 && state.Overrides.ContainsKey("Hauling"),
            "Old locks and strategy mapping still resolve");
        ColonyAdapter.Undo(map, legacy, state.LockedCells);
        live = Table(Capture(state));
        Check(legacy.Edits.All(e => live[ColonyAdapter.CellKey(e.PawnId, e.WorkId)] == e.Before),
            "The source transaction can be undone with both external mods absent");
    }

    private void ExerciseWorkPlanner(ColonyWorkState state)
    {
        Check(!ModsConfig.IsActive("lauriichan.prioritymaster") && ModsConfig.IsActive(LegacyId) == (Scenario == "coexist"),
            "Loaded expected external mod list");
        Find.TickManager.Pause();
        Find.PlaySettings.useWorkPriorities = true;
        var snapshot = Capture(state);
        workers = snapshot.Pawns.Count; columns = snapshot.Works.Count; maxPriority = snapshot.MaxPriority;
        Check(workers > 0 && maxPriority == 6, "RimLazy provides six priority levels");
        if (Scenario == "legacy-settings") WorkTableChecks.VerifyPrioritySettingsImport();
        WorkSplitChecks.Verify(snapshot, Scenario!);
        WorkTableChecks.Verify(map);

        var tab = DefDatabase<MainButtonDef>.GetNamed("RimLazy_Planner");
        Find.MainTabsRoot.SetCurrentTab(tab, false);
        Check(Find.MainTabsRoot.OpenTab == tab && tab.TabWindow is ColonyWorkWindow, "Opened the Planner tab after the Work tab");
        lockKey = ColonyAdapter.CellKey(snapshot.Pawns[0].Id, "Hauling");
        state.LockedCells.Add(lockKey);
        state.Overrides["Hauling"] = "industry";
        snapshot = Capture(state);
        original = Table(snapshot);
        var plan = WorkPlanner.Plan(snapshot.Inputs, snapshot.Columns, state.Preset, snapshot.MaxPriority);
        var next = Table(Capture(state));
        Check(original.All(k => next[k.Key] == k.Value), "Preview leaves every cell untouched");
        Check(WorkSplitChecks.SplitIds.All(id => plan.Cells.Any(c => c.WorkId == id)), "Planner emits individual priorities for all 21 core split columns");
        if (Scenario == "legacy-settings")
            Check(WorkSplitChecks.ImportedIds.All(id => plan.Cells.Any(c => c.WorkId == id)),
                "Planner evaluates hacking, rescue and mechanoid work from the imported settings");
        var undo = ColonyAdapter.Apply(snapshot, plan);
        edits = undo.Edits.Count;
        Check(edits > 0, "Applied real changes through the game work-settings API");
        state.LastUndo = undo;
        var actual = Table(Capture(state));
        Check(actual[lockKey] == original[lockKey] && plan.Cells.All(c => actual[ColonyAdapter.CellKey(c.PawnId, c.WorkId)] == c.After),
            "Read-back equals plan and lock is preserved");
        Check(plan.Cells.Any(c => WorkSplitChecks.SplitIds.Contains(c.WorkId) && c.After == 6),
            "A subdivided work column uses the native sixth tier");
        bool refused = false;
        try { ColonyAdapter.Apply(snapshot, plan); } catch (InvalidOperationException) { refused = true; }
        Check(refused, "Stale preview rejected");
        GameDataSaveLoader.SaveGame("RimLazy-quicktest");
        Check(File.Exists(GenFilePaths.FilePathForSavedGame("RimLazy-quicktest")), "Saved the test colony");
        oldMap = map;
        LongEventHandler.ExecuteWhenFinished(() => GameDataSaveLoader.LoadGame("RimLazy-quicktest"));
    }

    private void VerifyReload(ColonyWorkState state)
    {
        var restored = state.LastUndo;
        Check(restored is { Edits.Count: > 0 } && restored.Edits.Count == edits, "Undo journal survives reload");
        Check(state.LockedCells.Contains(lockKey!) && state.Overrides.ContainsKey("Hauling"), "Lock and mapping survive reload");
        var snapshot = Capture(state);
        Check(snapshot.MaxPriority == 6 && snapshot.Inputs.Any(p => p.Cells.Any(c =>
                  WorkSplitChecks.SplitIds.Contains(c.Key) && c.Value.Current == 6)), "Sixth-tier split work survives game reload");
        ColonyAdapter.Undo(map, restored!, state.LockedCells);
        var reverted = Table(Capture(state));
        Check(original!.Count == reverted.Count && original.All(k => reverted[k.Key] == k.Value),
            "Undo restores every original grid cell");
        Finish(null);
    }

    private static void Finish(string? error)
    {
        done = true;
        var output = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimLazySmoke.json");
        File.WriteAllText(output + ".tmp", JsonSerializer.Serialize(new { ok = error == null, scenario = Scenario,
            workers, columns, maxPriority, edits, verifiedLegacyEdits = legacyEdits, checks = Checks, error }));
        File.Move(output + ".tmp", output, true);
    }
}
