using RimWorld;
using StrategicWork.Core;
using StrategicWork.RimWorld;
using Verse;
using Verse.AI;

namespace RimLazy.GameSmoke;

// Only used on the disposable quicktest map. Use real buildings, item stacks, bills and pawn stats.
internal static class RealOrderChecks
{
    internal static void Verify(Map map)
    {
        const string work = "Cooking";
        var state = map.GetComponent<ColonyWorkState>();
        ColonyCapture Capture() => ColonyAdapter.Capture(map, state.Overrides, state.LockedCells, rush: true);
        WorkColumn Column(ColonyCapture snapshot) => snapshot.Columns.Single(w => w.Id == work);

        var workType = DefDatabase<WorkTypeDef>.GetNamed(work);
        var cook = map.mapPawns.FreeColonistsSpawned.First(p => !p.Downed && p.skills != null &&
            !p.WorkTypeIsDisabled(workType) && !p.skills.GetSkill(SkillDefOf.Cooking).TotallyDisabled);
        var other = map.mapPawns.FreeColonistsSpawned.First(p => p != cook);
        var skill = cook.skills.GetSkill(SkillDefOf.Cooking);
        int originalLevel = skill.GetLevel(false);
        var stoveDef = DefDatabase<ThingDef>.GetNamed("FueledStove");
        var origin = GenRadial.RadialCellsAround(cook.Position, 35, true).First(c =>
            GenAdj.OccupiedRect(c, Rot4.North, stoveDef.size).ExpandedBy(1).Cells.All(x =>
                x.InBounds(map) && !x.Fogged(map) && x.Standable(map) && x.GetThingList(map).Count == 0) &&
            cook.CanReach(c, PathEndMode.OnCell,Danger.Deadly));
        var foodDef = new[] { "RawRice", "RawCorn", "RawPotatoes", "RawFungus" }
            .Select(s => DefDatabase<ThingDef>.GetNamedSilentFail(s))
            .First(d => d != null && map.listerThings.ThingsOfDef(d).All(t=>t.Position.DistanceToSquared(origin)>20*20));
        var priorBills = workType.workGiversByPriority.SelectMany(g => g.fixedBillGiverDefs ?? [])
            .Distinct().SelectMany(d => map.listerBuildings.AllBuildingsColonistOfDef(d))
            .OfType<IBillGiver>().SelectMany(b => b.BillStack.Bills).Where(b=>!b.suspended).ToArray();
        var station = (Building_WorkTable)ThingMaker.MakeThing(stoveDef);
        var rice = ThingMaker.MakeThing(foodDef);
        try
        {
            foreach (var existing in priorBills) existing.suspended=true;
            station.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(station,origin,map,Rot4.North);
            var fuel = station.GetComp<CompRefuelable>();
            fuel.Refuel(((CompProperties_Refuelable)fuel.props).fuelCapacity);
            var recipe = DefDatabase<RecipeDef>.GetNamed("CookMealSimple");
            var bill = new Bill_Production(recipe) { repeatMode=BillRepeatModeDefOf.RepeatCount,
                repeatCount=2, ingredientSearchRadius=1 };
            bill.ingredientFilter.SetDisallowAll(null,null);
            bill.ingredientFilter.SetAllow(foodDef,true);
            bill.SetStoreMode(BillStoreModeDefOf.DropOnFloor);
            station.BillStack.AddBill(bill);
            var far = IngredientCell(map, cook, station.InteractionCell, 9, 10);
            var near = IngredientCell(map, cook, station.InteractionCell, 2, 3);
            rice.stackCount=foodDef.stackLimit;
            GenSpawn.Spawn(rice, far, map);
            rice.SetForbidden(false);

            PlannerProbe.Check(Column(Capture()).Capacity == 0, "Real cooking bill excludes ingredients outside its search radius");
            bill.ingredientSearchRadius=20;
            var supplied=Capture();
            PlannerProbe.Check(Column(supplied).Capacity==1 && Column(supplied).OutputDemand is > 0,
                "A fueled stove with two real meal orders and matching ingredients supplies one finite work site");
            var key=ColonyAdapter.CellKey(cook.GetUniqueLoadID(), work);
            var distant=supplied.CellDetails[key];
            PlannerProbe.Check(distant.PoisonChance.HasValue && distant.EstimatedTravelShare>0, "Real meal recipe measures cook risk and hauling time");
            bill.repeatCount=4;
            double twoOrders=Column(supplied).OutputDemand!.Value;
            PlannerProbe.Check(Math.Abs(Column(Capture()).OutputDemand!.Value-2*twoOrders)<1e-5,
                "Four meal orders expose twice the finite demand of two at the same station");
            bill.repeatCount=2;
            rice.DeSpawn();
            GenSpawn.Spawn(rice,near,map);
            var nearby=Capture().CellDetails[key];
            var originalPassion = skill.passion;
            try
            {
                skill.passion = Passion.None;
                var unpassionate = Capture().CellDetails[key];
                skill.passion = Passion.Major;
                var passionate = Capture().CellDetails[key];
                PlannerProbe.Check(Math.Abs(passionate.EstimatedPerformance - unpassionate.EstimatedPerformance) < 1e-7 &&
                    passionate.TrainingGain > unpassionate.TrainingGain && unpassionate.TrainingGain > 0,
                    "Changing only the cook's passion changes learning on the actual bill and leaves current output unchanged");
            }
            finally { skill.passion = originalPassion; }
            PlannerProbe.Check(nearby.EstimatedTravelShare<distant.EstimatedTravelShare &&
                nearby.EstimatedPerformance>distant.EstimatedPerformance,
                "A nearby ingredient stack shortens real meal travel and increases predicted throughput");

            EquipmentAdviceChecks.Verify(map, station, bill);
            bill.SetPawnRestriction(other);
            bool CookEligible(ColonyCapture s) => s.Inputs.Single(p=>p.Id==cook.GetUniqueLoadID()).Cells[work].Eligible;
            PlannerProbe.Check(!CookEligible(Capture()),"A meal order assigned to someone else excludes this cook");
            bill.SetPawnRestriction(cook);
            var allowed=Capture();
            PlannerProbe.Check(CookEligible(allowed), "Assigning that same order to the cook restores admission");
            skill.Level=4;
            var junior=Capture().CellDetails[key];
            CheckPoisonAgainstGame(cook,station,junior);
            skill.Level=9;
            var senior=Capture().CellDetails[key];
            CheckPoisonAgainstGame(cook,station,senior);
            PlannerProbe.Check(senior.EstimatedPerformance>junior.EstimatedPerformance && senior.PoisonChance<=junior.PoisonChance,
                "Raising the real cook's skill improves a fixed meal order and does not increase contamination risk");

            fuel.ConsumeFuel(fuel.Fuel);
            PlannerProbe.Check(Column(Capture()).Capacity==0,"Losing stove fuel immediately removes the finite cooking opportunity");
        }
        finally
        {
            skill.Level=originalLevel;
            if (rice.Spawned) rice.Destroy();
            if (station.Spawned) station.Destroy();
            foreach(var existing in priorBills) existing.suspended=false;
        }
    }

    private static IntVec3 IngredientCell(Map map,Pawn cook,IntVec3 from,float min,float max) =>
        GenRadial.RadialCellsAround(from,min,max).First(c=>c.InBounds(map) && c.Standable(map) && !c.Fogged(map) &&
            c.GetThingList(map).Count==0 && cook.CanReach(c, PathEndMode.OnCell, Danger.Deadly));

    private static void CheckPoisonAgainstGame(Pawn pawn,Building station,WorkAssessment estimate)
    {
        var room=station.InteractionCell.GetRoom(station.Map);
        double environment=room?.GetStat(RoomStatDefOf.FoodPoisonChance) ?? RoomStatDefOf.FoodPoisonChance.roomlessScore;
        double personal=Math.Clamp(pawn.GetStatValue(StatDefOf.FoodPoisonChance),0,1);
        double expected=environment+(1-environment)*personal;
        PlannerProbe.Check(estimate.PoisonChance.HasValue && Math.Abs(estimate.PoisonChance.Value-expected)<1e-6,
            $"Meal contamination agrees with active room and cook stats at effective cooking skill {pawn.skills.GetSkill(SkillDefOf.Cooking).Level}");
    }
}
