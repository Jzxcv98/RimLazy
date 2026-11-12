using RimWorld;
using StrategicWork.Core;
using Verse;
using Verse.AI;

namespace StrategicWork.RimWorld;

// Suggestions use spare gear or movable apparel on another planned worker. Never schedule a job,
// change an outfit or assume a suggested transfer has taken place.
internal static class EquipmentAdvice
{
    private sealed record Duty(string Label, double Labor, StatDef Speed, StatDef? Outcome, bool Cooking);
    private sealed record Worker(Pawn Pawn, StatEvaluation Stats, List<Duty> Duties);
    private sealed record Option(Worker Receiver, Worker? Donor, Thing Item, Duty Duty, double Gain);

    internal static IReadOnlyList<string> ForPlan(ColonyCapture capture, WorkPlan plan)
    {
        var map = capture.Map;
        var people = map.mapPawns.FreeColonistsSpawned.ToDictionary(p => p.GetUniqueLoadID());
        var workers = new List<Worker>();
        foreach (var group in plan.Cells.GroupBy(c => c.PawnId))
        {
            if (!people.TryGetValue(group.Key, out var pawn)) continue;
            var duties = new List<Duty>();
            bool worksUnknownJob = false;
            foreach (var cell in group.Where(c => c.EstimatedLabor > 0))
            {
                var work = DefDatabase<WorkTypeDef>.GetNamedSilentFail(cell.WorkId);
                if (work == null) { worksUnknownJob = true; continue; }
                var role = Specialties.For(work);
                string speedId = role?.Speed ?? (work.defName == "Research" ? "ResearchSpeed" : "");
                if (speedId.Length == 0 || DefDatabase<StatDef>.GetNamedSilentFail(speedId) is not { } speed)
                { worksUnknownJob = true; continue; }
                var outcome = role?.Outcome == null ? null : DefDatabase<StatDef>.GetNamedSilentFail(role.Outcome);
                duties.Add(new(work.LabelCap.ToString(), cell.EstimatedLabor, speed, outcome, work.defName == "Cooking"));
            }
            if (!worksUnknownJob && !pawn.Downed && !pawn.Drafted && !pawn.InMentalState)
                workers.Add(new(pawn, new StatEvaluation(pawn, true, capture.Rush ? GenDate.TicksPerHour : GenDate.TicksPerDay), duties));
        }
        if (workers.Count == 0) return [];
        var gear = new List<(Thing Item, Worker? Donor)>();
        foreach (var category in new[] { ThingRequestGroup.Apparel, ThingRequestGroup.Weapon })
            foreach (var item in map.listerThings.ThingsInGroup(category))
                if (HasWorkStats(item) && StoreUtility.IsInValidStorage(item) && !item.IsBurning())
                    gear.Add((item, null));
        foreach (var donor in workers)
            foreach (var apparel in donor.Pawn.apparel.WornApparel)
                if (HasWorkStats(apparel) && StatEvaluation.CanTransfer(donor.Pawn, apparel)) gear.Add((apparel, donor));

        var options = new List<Option>();
        foreach (var (item, donor) in gear.OrderBy(g => g.Item.thingIDNumber))
        {
            double loss = donor == null ? 0 : -donor.Duties.Sum(d => Difference(donor, d, item, remove: true));
            foreach (var receiver in workers)
            {
                if (receiver == donor || !receiver.Duties.Any(d => Affects(d, item)) || !CanUse(receiver.Pawn, item, donor?.Pawn)) continue;
                double benefit = 0, strongest = 0;
                Duty? match = null;
                foreach (var duty in receiver.Duties)
                {
                    double gain = Difference(receiver, duty, item, remove: false);
                    benefit += gain;
                    if (gain > strongest) { strongest = gain; match = duty; }
                }
                if (match != null && benefit - loss > 0.0001) options.Add(new(receiver, donor, item, match, benefit - loss));
            }
        }
        var used = new HashSet<int>();
        var peopleInTransfer = new HashSet<Pawn>();
        var result = new List<string>();
        foreach (var o in options.OrderByDescending(o => o.Gain).ThenBy(o => o.Item.thingIDNumber).ThenBy(o => o.Receiver.Pawn.thingIDNumber))
        {
            if (used.Contains(o.Item.thingIDNumber) || peopleInTransfer.Contains(o.Receiver.Pawn) ||
                (o.Donor != null && peopleInTransfer.Contains(o.Donor.Pawn))) continue;
            used.Add(o.Item.thingIDNumber);
            peopleInTransfer.Add(o.Receiver.Pawn);
            if (o.Donor != null) peopleInTransfer.Add(o.Donor.Pawn);
            string source = o.Donor?.Pawn.LabelShortCap.ToString() ?? "RimLazy_GearFromStorage".Translate().ToString();
            result.Add("RimLazy_GearSuggestion".Translate(o.Receiver.Pawn.LabelShortCap, o.Item.LabelCap, source, o.Duty.Label).ToString());
        }
        return result;
    }

    private static bool HasWorkStats(Thing item) => item.def.equippedStatOffsets is { Count: > 0 } &&
        !StatEvaluation.HasIndirectWearEffects(item);

    private static bool CanUse(Pawn receiver, Thing item, Pawn? donor)
    {
        if (receiver.Downed || receiver.Drafted || !EquipmentUtility.CanEquip(item, receiver)) return false;
        bool fits = item is Apparel apparel
            ? receiver.apparel != null && receiver.outfits?.CurrentApparelPolicy.filter.Allows(apparel) == true &&
              apparel.PawnCanWear(receiver) && ApparelUtility.HasPartsToWear(receiver, apparel.def) &&
              receiver.apparel.WornApparel.All(worn => ApparelUtility.CanWearTogether(worn.def, apparel.def, receiver.RaceProps.body))
            : receiver.equipment != null && receiver.equipment.Primary == null;
        if (!fits) return false;
        return donor == null ? !item.IsForbidden(receiver) && receiver.CanReserveAndReach(item, PathEndMode.Touch, Danger.Some)
            : !donor.Position.IsForbidden(receiver) && receiver.CanReach(donor, PathEndMode.Touch, Danger.Some);
    }

    private static bool Affects(Duty duty, Thing item) =>
        item.def.equippedStatOffsets.Any(m => m.stat == duty.Speed || m.stat == duty.Outcome ||
            (duty.Cooking && m.stat == StatDefOf.FoodPoisonChance) || m.stat == StatDefOf.WorkSpeedGlobal);

    private static double Difference(Worker worker, Duty duty, Thing item, bool remove)
    {
        var poison = duty.Cooking ? DefDatabase<StatDef>.GetNamedSilentFail("FoodPoisonChance") : null;
        if (!Affects(duty,item)) return 0;
        double Utility(bool changed)
        {
            float Read(StatDef stat) => !changed ? worker.Stats.EquippedEstimate(stat) :
                remove ? worker.Stats.WithoutTool(stat, item) : worker.Stats.WithAdditionalTool(stat, item);
            double speed = Math.Max(0, Read(duty.Speed));
            if (duty.Outcome != null) speed *= Math.Max(0, Read(duty.Outcome));
            return poison == null ? speed : speed * (1 - Math.Clamp(Read(poison), 0f, 1f));
        }
        double before = Utility(false);
        return before <= 0 ? 0 : duty.Labor * (Utility(true) / before - 1);
    }
}
