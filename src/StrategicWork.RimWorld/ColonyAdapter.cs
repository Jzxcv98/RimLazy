using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using StrategicWork.Core;
using Verse;

namespace StrategicWork.RimWorld;

public sealed record ColonyPawn(string Id, string Label);
public sealed record ColonyWork(string Id, string Label, string? StrategyId);
public sealed record ColonyCapture(Map Map, List<ColonyPawn> Pawns, List<ColonyWork> Works,
    List<PawnInput> Inputs, List<WorkColumn> Columns, int MaxPriority, string PrioritySource, bool ManualPriorities)
{
    internal int RequestedRange { get; init; }
    internal IReadOnlyDictionary<string, string> Mappings { get; init; } = new Dictionary<string, string>();
    internal IReadOnlySet<string> Locks { get; init; } = new HashSet<string>();
}
public sealed record WorkEdit(string PawnId, string WorkId, int Before, int After);
public sealed record WorkUndo(bool ManualBefore, bool ManualAfter, List<WorkEdit> Edits, int MapId);

public static class ColonyAdapter
{
    public const int MaxConfiguredPriority = 99;
    public static string CellKey(string pawnId, string workId) => pawnId + "/" + workId;

    private static IEnumerable<Pawn> Workers(Map map) =>
        map.mapPawns.FreeColonistsSpawned.Where(p => p.workSettings is { Initialized: true, EverWork: true });

    public static ColonyCapture Capture(Map map, IReadOnlyDictionary<string, string> mappings,
        IReadOnlySet<string> lockedCells, int priorityOverride = 0)
    {
        var workers = Workers(map).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
        var definitions = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(d => d.VisibleCurrently)
            .OrderByDescending(d => d.naturalPriority).ThenBy(d => d.defName, StringComparer.Ordinal).ToList();
        var works = new List<ColonyWork>(definitions.Count);
        var columns = new List<WorkColumn>(definitions.Count);
        var demand = new Dictionary<WorkTypeDef, WorkAvailability>();
        foreach (var def in definitions)
        {
            string? strategy = mappings.TryGetValue(def.defName, out var selected)
                ? (string.IsNullOrEmpty(selected) ? null : selected)
                : WorkProfile.SuggestStrategy(def);
            var availability = WorkProfile.ReadAvailability(map, def);
            demand.Add(def, availability);
            works.Add(new(def.defName, def.labelShort ?? def.LabelCap.ToString(), strategy));
            columns.Add(new(def.defName, strategy, def.naturalPriority, availability.Capacity));
        }

        var pawns = new List<ColonyPawn>(workers.Count);
        var inputs = new List<PawnInput>(workers.Count);
        int observed = 0;
        foreach (var worker in workers)
        {
            string id = worker.GetUniqueLoadID();
            var cells = new Dictionary<string, WorkCell>(StringComparer.Ordinal);
            foreach (var def in definitions)
            {
                int value = worker.workSettings.GetPriority(def);
                observed = Math.Max(observed, value);
                bool eligible = WorkProfile.CanDo(worker, def) && demand[def].Allows(worker);
                cells.Add(def.defName, new(value, eligible, eligible ? WorkProfile.Suitability(worker, def) : 0,
                    lockedCells.Contains(CellKey(id, def.defName))));
            }
            pawns.Add(new(id, worker.LabelShortCap.ToString()));
            inputs.Add(new(id, cells, worker.Downed ? 0 : 1));
        }
        var domain = PriorityDomain.Resolve(observed, priorityOverride);
        return new(map, pawns, works, inputs, columns, domain.Max, domain.Source, Find.PlaySettings.useWorkPriorities)
        {
            RequestedRange = priorityOverride, Mappings = mappings, Locks = lockedCells
        };
    }

    public static WorkUndo Apply(ColonyCapture capture, WorkPlan plan)
    {
        var map = capture.Map;
        RequireManual(map);
        if (!capture.ManualPriorities)
            throw Problem("RimLazy_StalePlan");
        var live = Capture(map, capture.Mappings, capture.Locks, capture.RequestedRange);
        if (live.MaxPriority != capture.MaxPriority || !live.Columns.SequenceEqual(capture.Columns) ||
            live.Inputs.Count != capture.Inputs.Count)
            throw Problem("RimLazy_StalePlan");
        // Orders can be reassigned, a worker can become incapable, or a mod can change the range while previewing.
        // Compare the inputs that affect admissibility, including cells that the plan leaves untouched.
        for (int i = 0; i < live.Inputs.Count; i++)
        {
            var was = capture.Inputs[i];
            var now = live.Inputs[i];
            if (was.Id != now.Id || was.Labor != now.Labor || was.Cells.Any(pair =>
                !now.Cells.TryGetValue(pair.Key, out var c) || c.Current != pair.Value.Current ||
                c.Eligible != pair.Value.Eligible || c.Locked != pair.Value.Locked))
                throw Problem("RimLazy_StalePlan");
        }
        var workers = Workers(map).ToDictionary(p => p.GetUniqueLoadID(), StringComparer.Ordinal);
        var defs = DefDatabase<WorkTypeDef>.AllDefsListForReading
            .Where(d => d.VisibleCurrently).ToDictionary(d => d.defName, StringComparer.Ordinal);
        var inputs = capture.Inputs.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var managed = capture.Works.Where(w => w.StrategyId != null).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        var edits = new List<WorkEdit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in plan.Cells.Where(c => c.Before != c.After))
        {
            if (!inputs.TryGetValue(c.PawnId, out var input) || !input.Cells.TryGetValue(c.WorkId, out var cell) ||
                cell.Locked || !managed.Contains(c.WorkId) || c.Before != cell.Current || c.After < 0 ||
                c.After > capture.MaxPriority || !seen.Add(CellKey(c.PawnId, c.WorkId)) ||
                !cell.Eligible)
                throw Problem("RimLazy_StalePlan");
            edits.Add(new(c.PawnId, c.WorkId, c.Before, c.After));
        }
        WriteChecked(edits, workers, defs);
        return new(true, true, edits, map.uniqueID);
    }

    public static void Undo(Map map, WorkUndo undo, IReadOnlySet<string>? lockedCells = null)
    {
        RequireManual(map);
        if (undo.MapId != map.uniqueID || !undo.ManualAfter || !undo.ManualBefore)
            throw Problem("RimLazy_UndoConflict");
        var workers = Workers(map).ToDictionary(p => p.GetUniqueLoadID(), StringComparer.Ordinal);
        var defs = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(d => d.VisibleCurrently)
            .ToDictionary(d => d.defName, StringComparer.Ordinal);
        var observed = workers.Values.SelectMany(p => defs.Values.Select(d => p.workSettings.GetPriority(d)))
            .DefaultIfEmpty(0).Max();
        int ceiling = PriorityDomain.Resolve(observed, 0).Max;
        var inverse = new List<WorkEdit>();
        foreach (var edit in undo.Edits)
        {
            if (!workers.TryGetValue(edit.PawnId, out var pawn) || !defs.TryGetValue(edit.WorkId, out var def) ||
                lockedCells?.Contains(CellKey(edit.PawnId, edit.WorkId)) == true ||
                edit.Before > ceiling || pawn.workSettings.GetPriority(def) != edit.After ||
                (edit.Before > 0 && pawn.WorkTypeIsDisabled(def)))
                throw Problem("RimLazy_UndoConflict");
            inverse.Add(new(edit.PawnId, edit.WorkId, edit.After, edit.Before));
        }
        WriteChecked(inverse, workers, defs);
    }

    private static void RequireManual(Map map)
    {
        if (!Find.Maps.Contains(map)) throw Problem("RimLazy_StalePlan");
        if (!Find.PlaySettings.useWorkPriorities) throw Problem("RimLazy_ManualRequired");
        if (PriorityDomain.UsesDetailedSchedules()) throw Problem("RimLazy_DetailedWorkTable");
    }

    private static void WriteChecked(IReadOnlyList<WorkEdit> edits, Dictionary<string, Pawn> workers,
        Dictionary<string, WorkTypeDef> defs)
    {
        int attempted = 0;
        try
        {
            foreach (var edit in edits)
            {
                var settings = workers[edit.PawnId].workSettings;
                var def = defs[edit.WorkId];
                if (settings.GetPriority(def) != edit.Before) throw Problem("RimLazy_StalePlan");
                attempted++;
                settings.SetPriority(def, edit.After);
                if (settings.GetPriority(def) != edit.After) throw Problem("RimLazy_WriteRefused");
            }
            // A compatibility patch may change another cell in the same transaction.
            if (edits.Any(e => workers[e.PawnId].workSettings.GetPriority(defs[e.WorkId]) != e.After))
                throw Problem("RimLazy_WriteRefused");
        }
        catch (Exception error)
        {
            bool restored = true;
            for (int i = attempted - 1; i >= 0; i--)
            {
                var entry = edits[i];
                var settings = workers[entry.PawnId].workSettings;
                var def = defs[entry.WorkId];
                try
                {
                    if (settings.GetPriority(def) != entry.Before) settings.SetPriority(def, entry.Before);
                    restored &= settings.GetPriority(def) == entry.Before;
                }
                catch (Exception) { restored = false; }
            }
            if (!restored) throw new InvalidOperationException("RimLazy_RollbackFailed".Translate().ToString(), error);
            throw;
        }
    }

    internal static InvalidOperationException Problem(string key) => new(key.Translate().ToString());
}
