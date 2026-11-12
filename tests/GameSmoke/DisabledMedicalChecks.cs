using RimWorld;
using StrategicWork.Core;
using StrategicWork.RimWorld;
using Verse;

namespace RimLazy.GameSmoke;

internal static class DisabledMedicalChecks
{
    internal static void Verify(Map map)
    {
        var surgery = DefDatabase<WorkTypeDef>.GetNamed("FSFSurgeon");
        var speed = DefDatabase<StatDef>.GetNamed("MedicalOperationSpeed");
        var success = StatDefOf.MedicalSurgerySuccessChance;
        bool CanOperate(Pawn pawn) => !pawn.Downed && pawn.story != null && pawn.skills != null &&
            !pawn.WorkTypeIsDisabled(surgery) && !speed.Worker.IsDisabledFor(pawn) && !success.Worker.IsDisabledFor(pawn);
        var pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(CanOperate);
        bool generated = pawn == null;
        if (pawn == null)
        {
            pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                forceGenerateNewPawn: true, canGeneratePawnRelations: false, validatorPostGear: p => CanOperate(p)));
            GenSpawn.Spawn(pawn, map.mapPawns.FreeColonistsSpawned.First().Position, map);
        }

        var oldStory = pawn.story.Adulthood;
        var original = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => !pawn.WorkTypeIsDisabled(w))
            .ToDictionary(w => w, w => pawn.workSettings.GetPriority(w));
        bool dev = Prefs.DevMode;
        try
        {
            Prefs.DevMode = true; // This is the condition that exposed the player's two red errors.
            pawn.story.Adulthood = DefDatabase<BackstoryDef>.AllDefsListForReading
                .First(b => b.slot == BackstorySlot.Adulthood && b.workDisables == WorkTags.Caring);
            pawn.Notify_DisabledWorkTypesChanged();
            PlannerProbe.Check(pawn.WorkTypeIsDisabled(surgery) && speed.Worker.IsDisabledFor(pawn) &&
                success.Worker.IsDisabledFor(pawn), "A real Caring backstory restriction disables both surgical stats");

            var state = map.GetComponent<ColonyWorkState>();
            foreach (bool rush in new[] { false, true })
            {
                var capture = ColonyAdapter.Capture(map, state.Overrides, state.LockedCells, state.PriorityOverride, rush);
                string id = pawn.GetUniqueLoadID();
                var cell = capture.Inputs.Single(p => p.Id == id).Cells[surgery.defName];
                var plan = WorkPlanner.Plan(capture.Inputs, capture.Columns, state.Preset, capture.MaxPriority, rush);
                PlannerProbe.Check(!cell.Eligible && plan.Cells.Single(c => c.PawnId == id && c.WorkId == surgery.defName).After == 0,
                    $"Medically incapable pawn is not assigned surgery in {(rush ? "rush" : "routine")} planning");

                var stats = new StatEvaluation(pawn, rush, rush ? GenDate.TicksPerHour : GenDate.TicksPerDay);
                foreach (var stat in new[] { speed, success })
                    PlannerProbe.Check(stats.Value(stat) == 0 && stats.Current(stat) == 0 &&
                        stats.EquippedEstimate(stat, pawn.Position) == 0 && stats.Approximate,
                        $"Disabled {stat.defName} has no usable value in {(rush ? "rush" : "routine")} projections");
            }
        }
        finally
        {
            pawn.story.Adulthood = oldStory;
            pawn.Notify_DisabledWorkTypesChanged();
            foreach (var (work, priority) in original)
                if (pawn.workSettings.GetPriority(work) != priority) pawn.workSettings.SetPriority(work, priority);
            Prefs.DevMode = dev;
            if (generated) pawn.DeSpawn();
        }
        PlannerProbe.Check(CanOperate(pawn) && new StatEvaluation(pawn, true, GenDate.TicksPerHour).Current(speed) > 0,
            "Restoring medical eligibility restores a positive operation speed");
    }
}
