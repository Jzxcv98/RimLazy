using System.Text.Json;
using RimWorld;
using StrategicWork.Core;
using StrategicWork.RimWorld;
using Verse;

namespace RimLazy.GameSmoke;

// Only installed in the test copy of the game. Exercise a real map, including Scribe and the game setter.
public sealed class PlannerProbe(Map map) : MapComponent(map)
{
    private static readonly string? Scenario = Environment.GetEnvironmentVariable("RIMLAZY_GAME_SMOKE");
    private static readonly List<string> Checks = [];
    private static Dictionary<string, int>? original;
    private static Map? originalMap;
    private static bool done;
    private static int edits;
    private static int frames, workers, columns, maxPriority;
    private static int advertised = 6;
    private static string? lockKey;
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
        Checks.Add(label);
    }
    private static Dictionary<string,int> Table(ColonyCapture c) =>
        c.Inputs.SelectMany(p => p.Cells.Select(w => (Key: ColonyAdapter.CellKey(p.Id,w.Key), w.Value.Current)))
            .ToDictionary(x => x.Key,x => x.Current);

    public override void MapComponentUpdate()
    {
        if (done || Scenario is not ("base" or "complex") || Current.ProgramState != ProgramState.Playing ||
            Find.CurrentMap != map || map.mapPawns.FreeColonistsSpawned.Count == 0 || frames++ < 12) return;
        // Require both an explicit quicktest and a test-owned profile before ever writing to a map.
        if (!Environment.GetCommandLineArgs().Contains("-quicktest", StringComparer.OrdinalIgnoreCase) ||
            !Path.GetFileName(GenFilePaths.SaveDataFolderPath).StartsWith("smoke-", StringComparison.Ordinal)) return;
        try
        {
            var state = map.GetComponent<ColonyWorkState>();
            if (originalMap != null)
            {
                if (ReferenceEquals(map,originalMap)) return;
                var restored = state.LastUndo;
                Check(restored is { Edits.Count: > 0 } && restored.Edits.Count == edits, "Undo survives actual game reload");
                Check(state.LockedCells.Contains(lockKey!) && state.Overrides.ContainsKey("Hauling"), "Map lock and mapping survived reload");
                ColonyAdapter.Undo(map,restored!,state.LockedCells);
                var reverted = Table(ColonyAdapter.Capture(map,state.Overrides,state.LockedCells));
                Check(original!.Count == reverted.Count && original.All(k => reverted[k.Key] == k.Value), "Undo restores every original grid cell");
                Finish(null);
                return;
            }
            var tab=DefDatabase<MainButtonDef>.GetNamed("RimLazy_Planner");
            Check(tab.TabWindow is ColonyWorkWindow, "Planner tab resolves to live window");
            Find.MainTabsRoot.SetCurrentTab(tab, false);
            Check(Find.MainTabsRoot.OpenTab == tab, "Game opened the Planner tab");
            Find.TickManager.Pause();
            Find.PlaySettings.useWorkPriorities = true;
            // This developer-only companion advertises a six-tier table. Complex Jobs itself adds columns, not tiers.
            if (Scenario=="complex")
            {
                // Also cover an extension without a range API: a real cell already uses its sixth tier.
                var available=ColonyAdapter.Capture(map,state.Overrides,state.LockedCells);
                var worker=available.Inputs.SelectMany(p=>p.Cells.Where(c=>c.Value.Eligible && c.Value.Current>0).Select(c=>(p.Id,c.Key,c.Value.Current))).First();
                var settings=map.mapPawns.FreeColonistsSpawned.Single(p=>p.GetUniqueLoadID()==worker.Id).workSettings;
                var column=DefDatabase<WorkTypeDef>.GetNamed(worker.Key);
                settings.SetPriority(column,6);
                Check(ColonyAdapter.Capture(map,state.Overrides,state.LockedCells).MaxPriority==6,"Existing sixth-tier cell detected without a provider");
                settings.SetPriority(column,worker.Current);
                PriorityCompatibility.RegisterRange("rimlazy.qaprobe", () => advertised);
            }
            var snapshot = ColonyAdapter.Capture(map,state.Overrides,state.LockedCells);
            workers=snapshot.Pawns.Count; columns=snapshot.Works.Count; maxPriority=snapshot.MaxPriority;
            Check(workers>0 && columns>5, "Live workers and columns detected");
            if (Scenario == "complex")
            {
                Check(snapshot.Works.Any(w=>w.Id=="FSFNurse") && snapshot.Works.Any(w=>w.Id=="FSFSurgeon"), "Actual Complex Jobs nurse and surgeon columns detected");
                Check(maxPriority==6,"Runtime six-tier provider detected");
            }
            lockKey=ColonyAdapter.CellKey(snapshot.Pawns[0].Id,"Hauling");
            state.LockedCells.Add(lockKey);
            state.Overrides["Hauling"]="industry";
            snapshot=ColonyAdapter.Capture(map,state.Overrides,state.LockedCells);
            original=Table(snapshot);
            var plan=WorkPlanner.Plan(snapshot.Inputs,snapshot.Columns,state.Preset,snapshot.MaxPriority);
            var next=Table(ColonyAdapter.Capture(map,state.Overrides,state.LockedCells));
            Check(original.All(k=>next[k.Key]==k.Value), "Preview leaves game grid untouched");
            if (Scenario=="complex")
            {
                advertised=4;
                bool refused=false;
                try { ColonyAdapter.Apply(snapshot,plan); } catch(InvalidOperationException) { refused=true; }
                var unchanged=Table(ColonyAdapter.Capture(map,state.Overrides,state.LockedCells));
                Check(refused && original.All(k=>unchanged[k.Key]==k.Value),"Lowered live range invalidates preview without writes");
                advertised=6;
            }
            var undo=ColonyAdapter.Apply(snapshot,plan);
            edits=undo.Edits.Count;
            Check(edits>0, "Applied real changes via game API");
            state.LastUndo=undo;
            var actual=Table(ColonyAdapter.Capture(map,state.Overrides,state.LockedCells));
            Check(actual[lockKey]==original[lockKey] && plan.Cells.All(c => actual[ColonyAdapter.CellKey(c.PawnId,c.WorkId)]==c.After), "Read-back equals plan; lock preserved");
            if (Scenario=="complex") Check(undo.Edits.Any(e=>e.After==6), "Generated and wrote sixth-tier work in live game");
            var staleRejected=false;
            try { ColonyAdapter.Apply(snapshot,plan); } catch (InvalidOperationException) { staleRejected=true; }
            Check(staleRejected, "Stale preview rejected");
            GameDataSaveLoader.SaveGame("RimLazy-quicktest");
            Check(File.Exists(GenFilePaths.FilePathForSavedGame("RimLazy-quicktest")), "Game wrote disposable save");
            originalMap=map;
            LongEventHandler.ExecuteWhenFinished(()=>GameDataSaveLoader.LoadGame("RimLazy-quicktest"));
        }
        catch(Exception ex) { Finish(ex.ToString()); }
    }
    private static void Finish(string? error)
    {
        done=true;
        var output=Path.Combine(GenFilePaths.SaveDataFolderPath,"RimLazySmoke.json");
        File.WriteAllText(output+".tmp",JsonSerializer.Serialize(new { ok=error==null, scenario=Scenario, workers, columns, maxPriority, edits, checks=Checks, error }));
        File.Move(output+".tmp",output,true);
    }
}
