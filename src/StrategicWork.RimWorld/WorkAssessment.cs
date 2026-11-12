using RimWorld;
using Verse;
using Verse.AI;

namespace StrategicWork.RimWorld;

// Rates are standard work per available tick. They are comparable within one work type.
public sealed record WorkAssessment(double EstimatedPerformance, double EquippedPerformance, double TrainingGain,
    double? PoisonChance, double EstimatedTravelShare, bool Approximate)
{
    public bool EquipmentMatters => Math.Abs(EstimatedPerformance - EquippedPerformance) >
        0.005 * Math.Max(0.01, EstimatedPerformance);
}

internal readonly record struct Evaluation(WorkAssessment Detail, bool Eligible, bool Comparable);
internal sealed class WorkerAssessment(Pawn pawn, StatEvaluation stats, MapWorkOpportunities map, bool rush)
{
    private const double ReferenceMoveSpeed = 4.6;
    private const double TicksPerSecond = 60;
    private readonly record struct Rate(double Value, double Travel, double? Poison);

    internal Evaluation Evaluate(WorkTypeDef work, OpportunitySet opportunities)
    {
        stats.BeginAssessment();
        var fallback = Fallback(work);
        if (!WorkProfile.CanDo(pawn, work)) return new(fallback, false, false);
        if (opportunities.Orders is { Count: > 0 } orders)
        {
            var candidates = new List<WorkAssessment>();
            bool known = true;
            foreach (var group in orders.GroupBy(o => o.Station))
            {
                // The game scans bills in their configured order. Only compare the first feasible one per table.
                foreach (var order in group)
                {
                    if (!map.AllowsWorker(pawn,order) || map.SelectSupplies(order,pawn) is not {} picks) continue;
                    candidates.Add(Production(order,picks));
                    known &= order.CanModel;
                    break;
                }
            }
            return Combine(candidates, fallback, known && !stats.Approximate);
        }
        if (opportunities.Sites is {Count: > 0} sites)
        {
            var results = sites.Where(s => WorkProfile.CapableOfGiver(pawn,s.Giver) &&
                map.CanAccess(pawn,s.Target) &&
                (s.Target.TryGetComp<CompHackable>() is not {} hack || hack.CanHackNow(pawn).Accepted)).Select(Site).ToList();
            if (opportunities.HasUnmodelledWork) results.Add(fallback);
            return Combine(results, fallback, !opportunities.HasUnmodelledWork && sites.All(s=>s.Speed != null) && !stats.Approximate);
        }
        if (work.defName == "FSFSurgeon" && opportunities.Capacity is > 0)
        {
            var results = new List<WorkAssessment>();
            foreach (var patient in pawn.Map.mapPawns.SpawnedPawnsWhoShouldHaveSurgeryDoneNow)
            {
                if (patient==pawn || !map.CanAccess(pawn,patient)) continue;
                var bill = patient.BillStack.Bills.OfType<Bill_Medical>().FirstOrDefault(b =>
                    !b.deleted && !b.suspended && b.recipe != null && b.PawnAllowedToStartAnew(pawn) &&
                    b.recipe.PawnSatisfiesSkillRequirements(pawn));
                if (bill != null) results.Add(Surgery(patient,bill));
            }
            return Combine(results, fallback, !stats.Approximate);
        }
        if (work.defName is "FSFHauling" or "Hauling" or "FSFDeliver")
        {
            var available = work.defName=="FSFDeliver" ? map.Deliveries() : map.Hauling();
            var routes = available.Where(r => map.CanAccess(pawn,r.Item) &&
                !r.Destination.IsForbidden(pawn) && pawn.CanReach(r.Destination,PathEndMode.Touch,Danger.Deadly)).ToList();
            if (routes.Count > 0)
            {
                var samples = routes.Select(Haul).ToList();
                return Combine(samples,fallback,!stats.Approximate);
            }
        }
        return new(fallback,true,(Specialties.For(work)!=null || work.defName=="Research") && !stats.Approximate);
    }

    private static Evaluation Combine(List<WorkAssessment> rows, WorkAssessment fallback, bool comparable)
    {
        if (rows.Count == 0) return new(fallback, false, false);
        var poisoned = rows.Where(x=>x.PoisonChance.HasValue).ToList();
        return new(new(rows.Average(x=>x.EstimatedPerformance), rows.Average(x=>x.EquippedPerformance),
            rows.Average(x=>x.TrainingGain), poisoned.Count==0 ? null : poisoned.Average(x=>x.PoisonChance!.Value),
            rows.Average(x=>x.EstimatedTravelShare), rows.Any(x=>x.Approximate)),true,comparable);
    }

    private WorkAssessment Production(OrderSite order, List<Pickup> picks)
    {
        var recipe = order.Bill.recipe;
        var station = order.Station;
        var cell = station.InteractionCell;
        bool poisonable = recipe.products.Any(p => p.thingDef.HasComp(typeof(CompFoodPoisonable)));
        double roomChance = poisonable ? cell.GetRoom(station.Map)?.GetStat(RoomStatDefOf.FoodPoisonChance) ??
            RoomStatDefOf.FoodPoisonChance.roomlessScore : 0;
        double benchRate = recipe.workTableSpeedStat != null && station is Building_WorkTable
            ? station.GetStatValue(recipe.workTableSpeedStat) : 1;
        double efficiency = recipe.workTableEfficiencyStat != null && station is Building_WorkTable
            ? station.GetStatValue(recipe.workTableEfficiencyStat) : 1;
        bool qualityProduct = recipe.products.Any(p=>p.thingDef.HasComp(typeof(CompQuality)));
        double craftQuality = qualityProduct && recipe.workSkill != null ? CraftQualityFactor(recipe.workSkill,order.Bill as Bill_Production) : 1;
        Rate Calculate(bool equipped)
        {
            double Stat(StatDef def) => equipped ? stats.EquippedEstimate(def,cell) : stats.Value(def,cell);
            double operation = order.WorkTicks / Math.Max(0.001, (recipe.workSpeedStat == null ? 1 : Stat(recipe.workSpeedStat)) * benchRate);
            // A normal site estimate amortizes first arrival over future work. A rush uses the current pawn location.
            double speed = Stat(StatDefOf.MoveSpeed);
            double travel = rush ? map.Route(pawn,pawn.Position,cell).Ticks(speed) : 0;
            foreach (var pick in picks)
            {
                int capacity = Carry(pick.Thing.def, Stat(StatDefOf.CarryingCapacity));
                travel += 2 * map.Route(pawn,pick.Thing.Position,cell).Ticks(speed) * Math.Ceiling((double)pick.Amount / capacity);
            }
            if (order.Bill is Bill_Production production && production.GetStoreMode()!=BillStoreModeDefOf.DropOnFloor &&
                recipe.products.FirstOrDefault() is {} item)
            {
                var destination = production.GetStoreMode()==BillStoreModeDefOf.SpecificStockpile
                    ? Nearest(production.GetSlotGroup()?.CellsList, cell) : map.StorageCell(item.thingDef, cell);
                if (destination.HasValue) travel += 2 * map.Route(pawn,cell,destination.Value).Ticks(speed) *
                    Math.Ceiling((double)item.count / Carry(item.thingDef, Stat(StatDefOf.CarryingCapacity)));
            }
            double? poison = poisonable ? roomChance + (1-roomChance)*Math.Clamp(Stat(StatDefOf.FoodPoisonChance),0,1) : null;
            double usefulYield = (recipe.efficiencyStat == null ? 1 : Stat(recipe.efficiencyStat)) *
                efficiency * craftQuality * (1 - poison.GetValueOrDefault());
            return new(order.WorkTicks * Math.Max(0,usefulYield)/(travel+operation), travel/(travel+operation),poison);
        }
        var result = Calculate(false);
        var equippedRate = Calculate(true);
        return new(result.Value,equippedRate.Value,Learn(recipe.workSkill,recipe.workSkillLearnFactor,1-result.Travel),
            result.Poison,result.Travel,stats.Approximate || picks.Count>0 || qualityProduct || !order.CanModel);
    }

    private WorkAssessment Site(WorkSite s)
    {
        IntVec3 cell = s.Target.def.hasInteractionCell ? s.Target.InteractionCell : s.Target.Position;
        // Measure one representative work session at the device. Travel to a fixed bench is paid once.
        const double work = 1500;
        var factor = s.Target is Building_ResearchBench bench ? bench.GetStatValue(StatDefOf.ResearchSpeedFactor) : 1;
        Rate Calculate(bool equipped)
        {
            double Stat(StatDef def) => equipped ? stats.EquippedEstimate(def,cell):stats.Value(def,cell);
            var op = work / Math.Max(0.001,(s.Speed==null?1:Stat(s.Speed))*factor);
            var travel = map.Route(pawn,pawn.Position,cell).Ticks(Stat(StatDefOf.MoveSpeed));
            return new(work/(op+travel),travel/(op+travel),null);
        }
        var a=Calculate(false); var b=Calculate(true);
        return new(a.Value,b.Value,Learn(s.Skill,s.XpFactor,1-a.Travel),null,a.Travel,true);
    }

    private WorkAssessment Haul(HaulRoute route)
    {
        var from = route.Item.Position;
        var outbound = map.Route(pawn,from,route.Destination);
        var first = map.Route(pawn,pawn.Position,from);
        Rate Calculate(bool equipped)
        {
            double Stat(StatDef def) => equipped ? stats.EquippedEstimate(def) : stats.Value(def);
            var cap = Carry(route.Item.def,Stat(StatDefOf.CarryingCapacity));
            double trips=Math.Ceiling((double)route.Amount/cap);
            double move = Stat(StatDefOf.MoveSpeed);
            double travel=first.Ticks(move) + outbound.Ticks(move)*(2*trips-1);
            double handling=30*trips; // short pickup/drop allowance, estimated
            double standardTrips=Math.Ceiling((double)route.Amount/Carry(route.Item.def,75));
            double reference=outbound.Ticks(ReferenceMoveSpeed)*(2*standardTrips-1) + 30*standardTrips;
            return new(reference/(travel+handling), travel/(travel+handling),null);
        }
        var a=Calculate(false);var b=Calculate(true);
        return new(a.Value,b.Value,0,null,a.Travel,true);
    }

    private WorkAssessment Surgery(Pawn patient, Bill_Medical bill)
    {
        var cell = patient.Position;
        double amount = bill.recipe.workAmount>0 ? bill.recipe.workAmount : 1500;
        double bedFactor = patient.CurrentBed()?.GetStatValue(StatDefOf.SurgerySuccessChanceFactor) ?? 1;
        Rate Calculate(bool equipped)
        {
            double Stat(StatDef def) => equipped ? stats.EquippedEstimate(def,cell) : stats.Value(def,cell);
            double op = amount / Math.Max(0.001, Stat(bill.recipe.workSpeedStat ?? DefDatabase<StatDef>.GetNamed("MedicalOperationSpeed")));
            // Conditional, regular surgical performance. Medicine and custom surgery components remain estimates.
            double success = Math.Clamp(Stat(StatDefOf.MedicalSurgerySuccessChance) * bedFactor * bill.recipe.surgerySuccessChanceFactor,0,1);
            double travel=map.Route(pawn,pawn.Position,cell).Ticks(Stat(StatDefOf.MoveSpeed));
            return new(amount*success/(op+travel),travel/(op+travel),null);
        }
        var a=Calculate(false);var b=Calculate(true);
        return new(a.Value,b.Value,Learn(bill.recipe.workSkill ?? SkillDefOf.Medicine,
            bill.recipe.workSkillLearnFactor,1-a.Travel),null,a.Travel,true);
    }

    private WorkAssessment Fallback(WorkTypeDef work)
    {
        var role = Specialties.For(work);
        var speed = role == null ? null : DefDatabase<StatDef>.GetNamedSilentFail(role.Speed);
        var outcome = role?.Outcome is {} name ? DefDatabase<StatDef>.GetNamedSilentFail(name) : null;
        // A task without a known XP source receives zero learning credit; work tags alone prove no training.
        var learningSkill = Specialties.LearningSkill(work.defName);
        double travelFraction = work.defName switch {
            "Hauling" or "FSFHauling" or "FSFTransport" or "FSFDeliver" or "FSFMerge" or
                "FSFNurse" or "FSFRescue" or "FSFRearming" or "FSFPrisonCare" => 0.7,
            "Research" or "FSFDrilling" or "Cooking" => 0.08,
            _ => 0.25
        };
        Rate Calculate(bool equipped)
        {
            double Stat(StatDef def) => equipped ? stats.EquippedEstimate(def) : stats.Value(def);
            double move = Math.Max(0.01,Stat(StatDefOf.MoveSpeed)/ReferenceMoveSpeed);
            var rateStat = speed ?? (work.defName=="Research" ? StatDefOf.ResearchSpeed : StatDefOf.WorkSpeedGlobal);
            double action = rateStat==StatDefOf.MoveSpeed ? 1 : Math.Max(0.001,Stat(rateStat));
            // Frozen work units, with separate movement/operation phases. Carry capacity requires an actual item.
            double travel = travelFraction / move, time = travel + (1-travelFraction)/action;
            double yield = outcome == null || outcome == StatDefOf.CarryingCapacity ? 1 : Math.Max(0,Stat(outcome));
            bool cooking = work.defName=="Cooking";
            double? poison = cooking ? Math.Clamp(Stat(StatDefOf.FoodPoisonChance),0,1) : null;
            return new(yield * (1-poison.GetValueOrDefault()) / time,travel/time,poison);
        }
        var a=Calculate(false);var b=Calculate(true);
        return new(a.Value,b.Value,Learn(learningSkill,Specialties.LearningFactor(work.defName),1-a.Travel),
            a.Poison,a.Travel,true);
    }

    private double Learn(SkillDef? skill,double xpFactor,double operationShare)
    {
        if (skill==null || xpFactor<=0 || pawn.skills?.GetSkill(skill) is not {} record ||
            record.TotallyDisabled || record.Level>=20) return 0;
        // LearnRateFactor includes passion, global learning, animals learning and the day's XP saturation.
        // Express the opportunity as progress toward another skill level per unit of operation time.
        float learningRate;
        try { learningRate = record.LearnRateFactor(); }
        catch (NotImplementedException) { return 0; } // Some mods add passion values without exposing their XP rule.
        return float.IsFinite(learningRate) ? Math.Max(0,learningRate) * xpFactor * operationShare *
            1000 / Math.Max(1, record.XpRequiredForLevelUp) : 0;
    }

    private double CraftQualityFactor(SkillDef skill, Bill_Production? bill)
    {
        int level = pawn.skills?.GetSkill(skill).Level ?? 0;
        int persistentBonus = ModsConfig.IdeologyActive && pawn.Ideo != null
            ? pawn.Ideo.GetRole(pawn)?.def.roleEffects?.OfType<RoleEffect_ProductionQualityOffset>().Sum(r=>r.offset) ?? 0 : 0;
        // Integrate the game's split Gaussian. Its upper tail is redrawn half the time for a masterwork.
        // Work quality from a persistent production specialist remains; creativity is a one-use effect.
        ReadOnlySpan<double> centers = [0.7,1.1,1.5,1.8,2,2.2,2.4,2.6,2.8,2.95,3.1,3.25,3.4,3.5,3.6,3.7,3.8,3.9,4,4.1,4.2];
        double center = centers[Math.Clamp(level,0,20)];
        double Tail(int boundary,double upperWidth)
        {
            if (boundary==0) return 1;
            if (boundary==6) return 0;
            double z=(boundary-center)/(boundary<center ? 0.6 : upperWidth);
            double t=1/(1+0.2316419*Math.Abs(z));
            double tail=Math.Exp(-0.5*z*z)/Math.Sqrt(2*Math.PI) *
                t*(0.319381530+t*(-0.356563782+t*(1.781477937+t*(-1.821255978+1.330274429*t))));
            return z>=0 ? tail : 1-tail;
        }
        double reroll=0.5*Tail(5,0.8), value=0;
        for (int grade=0;grade<=5;grade++)
        {
            double prob=Tail(grade,0.8)-Tail(grade+1,0.8) + reroll*(Tail(grade,0.95)-Tail(grade+1,0.95));
            if (grade==5) prob-=reroll;
            int adjusted=Math.Clamp(grade+persistentBonus,0,6);
            if (bill?.repeatMode==BillRepeatModeDefOf.TargetCount)
                value+=prob * (bill.qualityRange.Includes((QualityCategory)adjusted)?1:0);
            else value+=prob * (1+adjusted)/3.0;
        }
        return Math.Max(0,value);
    }

    private static int Carry(ThingDef item,double capacity) =>
        Math.Clamp((int)Math.Round(capacity/Math.Max(0.001,item.VolumePerUnit)),1,Math.Max(1,item.stackLimit));
    private static IntVec3? Nearest(List<IntVec3>? cells,IntVec3 origin) =>
        cells is {Count:>0} ? cells.MinBy(c=>c.DistanceToSquared(origin)) : null;
}
