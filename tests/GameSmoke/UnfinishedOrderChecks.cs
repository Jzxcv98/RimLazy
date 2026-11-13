using RimWorld;
using StrategicWork.RimWorld;
using Verse;
using Verse.AI;

namespace RimLazy.GameSmoke;

internal static class UnfinishedOrderChecks
{
    // A partially completed bow followed by three fresh bows exercises the same bill type
    // as the campaign failure without requiring the local campaign save.
    internal static void Verify(Map map)
    {
        var spotDef = DefDatabase<ThingDef>.GetNamed("CraftingSpot");
        var recipe = DefDatabase<RecipeDef>.GetNamed("Make_Bow_Short");
        var work = DefDatabase<WorkGiverDef>.AllDefsListForReading
            .First(g => g.fixedBillGiverDefs?.Contains(spotDef) == true).workType;
        var workers = map.mapPawns.FreeColonistsSpawned.Where(p =>
            !p.Downed && p.workSettings is { Initialized: true } && !p.WorkTypeIsDisabled(work)).ToArray();
        var maker = workers.First();
        var other = map.mapPawns.FreeColonistsSpawned.First(p => p != maker);
        var origin = GenRadial.RadialCellsAround(maker.Position, 40, true).First(c =>
            GenAdj.OccupiedRect(c, Rot4.North, spotDef.size).ExpandedBy(1).Cells.All(cell =>
                cell.InBounds(map) && !cell.Fogged(map) && cell.Standable(map) && cell.GetThingList(map).Count == 0) &&
            maker.CanReach(c, PathEndMode.OnCell, Danger.Deadly));
        var previousPriority = maker.workSettings.GetPriority(work);
        var spot = (Building_WorkTable)ThingMaker.MakeThing(spotDef);
        var piece = (UnfinishedThing)ThingMaker.MakeThing(recipe.unfinishedThingDef, ThingDefOf.WoodLog);
        var wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
        var bill = new Bill_ProductionWithUft(recipe)
        {
            repeatMode = BillRepeatModeDefOf.RepeatCount, repeatCount = 4, ingredientSearchRadius = 3
        };
        try
        {
            maker.workSettings.SetPriority(work, 2);
            spot.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(spot, origin, map, Rot4.North);
            bill.ingredientFilter.SetDisallowAll(null, null);
            bill.ingredientFilter.SetAllow(ThingDefOf.WoodLog, true);
            spot.BillStack.AddBill(bill);
            piece.Creator = maker;
            bill.SetBoundUft(piece);
            double fullWork = recipe.WorkAmountForStuff(ThingDefOf.WoodLog);
            piece.workLeft = (float)(fullWork / 4);
            GenSpawn.Spawn(piece, spot.InteractionCell, map);
            wood.stackCount = ThingDefOf.WoodLog.stackLimit;
            GenSpawn.Spawn(wood, origin + IntVec3.East, map);
            wood.SetForbidden(false);
            piece.SetForbidden(false);

            (MapWorkOpportunities Context, OrderSite Order) Read()
            {
                var ctx = new MapWorkOpportunities(map, GenDate.TicksPerDay);
                return (ctx, ctx.Read(work).Orders!.First(o => o.Bill == bill));
            }
            var valid = Read();
            PlannerProbe.Check(valid.Order.InProgress == piece && valid.Order.WorkTicks == piece.workLeft &&
                valid.Order.TotalWorkTicks == piece.workLeft + 3 * fullWork,
                "A bound batch counts the partial bow once and each later bow at full recipe work");
            PlannerProbe.Check(valid.Context.SelectSupplies(valid.Order, maker) is { Count: 0 } &&
                valid.Context.SelectSupplies(valid.Order, other) == null,
                "Only its original crafter can resume the bound bow without collecting wood again");

            // Vanilla's BoundWorker getter clears the bill link in this state. Preview should
            // recognize it cannot resume, while leaving both vanilla objects as found.
            maker.workSettings.SetPriority(work, 0);
            var invalid = Read();
            var state = map.GetComponent<ColonyWorkState>();
            var capture = ColonyAdapter.Capture(map, state.Overrides, state.LockedCells);
            var live = ColonyAdapter.Capture(map, state.Overrides, state.LockedCells);
            PlannerProbe.Check(invalid.Order.InProgress == null && invalid.Order.TotalWorkTicks == 4 * fullWork &&
                invalid.Context.SelectSupplies(invalid.Order, null) is { Count: > 0 },
                "When the creator stops that work, new bows require full work and fresh ingredients");
            PlannerProbe.Check(bill.BoundUft == piece && piece.BoundBill == bill && capture.Columns.SequenceEqual(live.Columns),
                "Repeated work-table captures leave the invalid vanilla bill binding and demand unchanged");
        }
        finally
        {
            maker.workSettings.SetPriority(work, previousPriority);
            bill.SetBoundUft(null);
            if (piece.Spawned) piece.Destroy();
            if (wood.Spawned) wood.Destroy();
            if (spot.Spawned) spot.Destroy();
        }
    }
}
