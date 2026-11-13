using RimWorld;
using StrategicWork.RimWorld;
using Verse;
using Verse.AI;

namespace RimLazy.GameSmoke;

internal static class MaintenanceRouteChecks
{
    internal static void Verify(Map map)
    {
        var storage = DefDatabase<WorkTypeDef>.GetNamed("FSFHauling");
        var supply = DefDatabase<WorkTypeDef>.GetNamed("FSFDeliver");
        var upkeep = DefDatabase<WorkTypeDef>.GetNamed("Hauling");
        var pawn = map.mapPawns.FreeColonistsSpawned.First(p =>
            new[] { storage, supply, upkeep }.All(w => !p.WorkTypeIsDisabled(w)) &&
            p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation));
        var stockTasks = WorkerAssessment.HaulTasksFor(storage, pawn);
        var buildTasks = WorkerAssessment.HaulTasksFor(supply, pawn);
        var upkeepTasks = WorkerAssessment.HaulTasksFor(upkeep, pawn);
        PlannerProbe.Check(upkeep.workGiversByPriority.Any(g => g.giverClass == typeof(WorkGiver_Refuel)) &&
            upkeepTasks == HaulTask.None && stockTasks.HasFlag(HaulTask.StockItem) &&
            buildTasks == (HaulTask.Blueprint | HaulTask.Frame), "Split columns use only their loaded haul providers; refuelling is separate");

        var vanilla = new WorkTypeDef { defName = "Hauling", workGiversByPriority =
            [DefDatabase<WorkGiverDef>.GetNamed("HaulGeneral"), ..supply.workGiversByPriority] };
        var custom = new WorkTypeDef { defName = "UnrelatedThirdPartyName", workGiversByPriority =
            [new WorkGiverDef { giverClass = typeof(WorkGiver_ConstructDeliverResourcesToFrames) }] };
        PlannerProbe.Check(WorkerAssessment.HaulTasksFor(vanilla, pawn) == (HaulTask.StockItem | buildTasks) &&
            WorkerAssessment.HaulTasksFor(custom, pawn) == HaulTask.Frame,
            "Combined vanilla-style and third-party frame-only columns follow their actual providers");

        var log = ThingMaker.MakeThing(ThingDefOf.WoodLog);
        var medicine = ThingMaker.MakeThing(ThingDefOf.MedicineIndustrial);
        var wall = ThingDefOf.Wall;
        var cells = GenRadial.RadialCellsAround(pawn.Position, 15, true).Where(c =>
            c.InBounds(map) && !c.Fogged(map) && c.GetZone(map) == null && c.GetThingList(map).Count == 0 &&
            c.Standable(map) && pawn.CanReach(c, PathEndMode.OnCell, Danger.Some) &&
            GenConstruct.CanPlaceBlueprintAt(wall,c,Rot4.North,map,stuffDef:ThingDefOf.WoodLog).Accepted).Take(4).ToArray();
        var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile,map.zoneManager);
        Blueprint? blueprint = null;
        map.zoneManager.RegisterZone(zone);
        try
        {
            zone.AddCell(cells[0]);
            zone.settings.filter.SetDisallowAll();
            zone.settings.filter.SetAllow(medicine.def, true);
            zone.settings.Priority = StoragePriority.Critical;
            GenSpawn.Spawn(medicine, cells[1], map);
            GenSpawn.Spawn(log, cells[2], map);
            medicine.SetForbidden(false, false);
            log.SetForbidden(false, false);
            blueprint = GenConstruct.PlaceBlueprintForBuild(wall,cells[3],map,Rot4.North,Faction.OfPlayer,ThingDefOf.WoodLog);
            var opportunities = new MapWorkOpportunities(map, GenDate.TicksPerHour);
            var stock = opportunities.HaulSamples(stockTasks).ToList();
            var deliveries = opportunities.HaulSamples(buildTasks).ToList();
            PlannerProbe.Check(stock.Any(r => r.Task == HaulTask.StockItem && r.Destination == cells[0]) &&
                deliveries.Any(r => r.Task == HaulTask.Blueprint && r.Destination == cells[3]) &&
                deliveries.All(r => (r.Task & buildTasks) != 0) && !opportunities.HaulSamples(upkeepTasks).Any(),
                "Real stockpile and construction supply routes are not counted as facility maintenance");
        }
        finally
        {
            blueprint?.Destroy(DestroyMode.Cancel);
            if (!log.Destroyed) log.Destroy();
            if (!medicine.Destroyed) medicine.Destroy();
            zone.Delete();
        }
    }
}
