using RimWorld;
using StrategicWork.Core;
using StrategicWork.RimWorld;
using Verse;
using Verse.AI;

namespace RimLazy.GameSmoke;

internal static class EquipmentAdviceChecks
{
    // A single stockpiled chef hat, two eligible cooks and the live meal order from RealOrderChecks.
    // A controlled duty split makes this check independent of the randomly generated colonists' other jobs.
    internal static void Verify(Map map, Building_WorkTable stove, Bill_Production bill)
    {
        var hat = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RimLazyQA_ChefHat"), ThingDefOf.Cloth);
        var cooking = DefDatabase<WorkTypeDef>.GetNamed("Cooking");
        var spot = GenRadial.RadialCellsAround(stove.Position, 18, true).First(c =>
            c.InBounds(map) && !c.Fogged(map) && c.Standable(map) && c.GetZone(map) == null &&
            c.GetThingList(map).Count == 0 && map.mapPawns.FreeColonistsSpawned.Any(p =>
                p.CanReach(c, PathEndMode.OnCell, Danger.Some)));
        var storage = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
        var generated = new List<Pawn>();
        var removed = new List<(Pawn Owner, Apparel Item)>();
        var policies = new Dictionary<ThingFilter, bool>();
        var oldSkills = new Dictionary<SkillRecord, int>();
        map.zoneManager.RegisterZone(storage);
        try
        {
            storage.AddCell(spot);
            storage.GetStoreSettings().filter.SetAllow(hat.def, true);
            GenSpawn.Spawn(hat, spot, map);
            hat.SetForbidden(false, warnOnFail: false);
            bool CanCook(Pawn p) => !p.Downed && !p.Drafted && !p.WorkTypeIsDisabled(cooking) &&
                p.skills?.GetSkill(SkillDefOf.Cooking).TotallyDisabled == false && p.outfits != null &&
                p.CanReserveAndReach(hat, PathEndMode.Touch, Danger.Some) && EquipmentUtility.CanEquip(hat, p) &&
                hat.PawnCanWear(p) && ApparelUtility.HasPartsToWear(p, hat.def);
            var cooks = map.mapPawns.FreeColonistsSpawned.Where(CanCook).Take(2).ToList();
            // A quicktest may roll more than one pawn incapable of cooking. Supply a second test worker.
            for (int i = 0; cooks.Count < 2 && i < 12; i++)
            {
                var extra = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                generated.Add(extra);
                GenSpawn.Spawn(extra, spot, map);
                if (CanCook(extra)) cooks.Add(extra);
            }
            PlannerProbe.Check(cooks.Count == 2 && StoreUtility.IsInValidStorage(hat),
                "Two live cooks can reach the one stockpiled test chef hat");
            foreach (var cook in cooks)
            {
                var policy = cook.outfits.CurrentApparelPolicy.filter;
                policies.TryAdd(policy, policy.Allows(hat.def));
                policy.SetAllow(hat.def, true);
                foreach (var worn in cook.apparel.WornApparel
                    .Where(a => !ApparelUtility.CanWearTogether(a.def, hat.def, cook.RaceProps.body)).ToArray())
                {
                    removed.Add((cook, worn));
                    cook.apparel.Remove(worn);
                }
                var skill = cook.skills.GetSkill(SkillDefOf.Cooking);
                oldSkills[skill] = skill.GetLevel(false);
                skill.Level = 4;
            }
            var state = map.GetComponent<ColonyWorkState>();
            var snapshot = ColonyAdapter.Capture(map, state.Overrides, state.LockedCells, rush: true);
            string GearState() => string.Join("|", cooks.Select(p =>
                $"{p.thingIDNumber}:{p.equipment?.Primary?.thingIDNumber}:{string.Join(",", p.apparel.WornApparel.Select(a=>a.thingIDNumber))}"));
            var gearBefore = GearState();
            var billsBefore = stove.BillStack.Bills.ToArray();
            var beforeSettings = (bill.suspended, bill.PawnRestriction, bill.repeatCount, bill.nextTickToSearchForIngredients);
            string[] HatSuggestions(params Pawn[] recipients)
            {
                var duty = recipients.Select(p => new PlannedCell(p.GetUniqueLoadID(), "Cooking", 1, 1, 0.5)).ToArray();
                var plan = new WorkPlan(duty, [], 0, 0);
                return EquipmentAdvice.ForPlan(snapshot, plan)
                    .Where(line => line.Contains(hat.LabelCap.ToString(), StringComparison.Ordinal)).ToArray();
            }
            foreach (var cook in cooks)
                PlannerProbe.Check(HatSuggestions(cook).Length == 1, "A cook with a real meal duty receives the reachable hat recommendation");
            PlannerProbe.Check(HatSuggestions(cooks.ToArray()).Length == 1, "Two cooks share one hat recommendation without double allocating the item");
            PlannerProbe.Check(hat.Spawned && hat.Position == spot && gearBefore == GearState() &&
                billsBefore.SequenceEqual(stove.BillStack.Bills) &&
                beforeSettings == (bill.suspended, bill.PawnRestriction, bill.repeatCount, bill.nextTickToSearchForIngredients),
                "Equipment preview leaves the hat in storage, outfits untouched and the meal order unchanged");
        }
        finally
        {
            foreach (var (skill, level) in oldSkills) skill.Level = level;
            foreach (var (owner, item) in removed) owner.apparel.Wear(item, false);
            foreach (var (policy, allowed) in policies) policy.SetAllow(hat.def, allowed);
            if (!hat.Destroyed) hat.Destroy();
            storage.Delete();
            foreach (var pawn in generated)
                if (!pawn.Destroyed) pawn.Destroy();
        }
    }
}
