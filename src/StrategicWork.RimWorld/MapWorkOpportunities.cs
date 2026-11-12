using RimWorld;
using Verse;
using Verse.AI;

namespace StrategicWork.RimWorld;

internal sealed record IngredientStock(IngredientCount Ingredient, Thing[] Stock)
{
    internal Thing[][] ByDef { get; } = Stock.GroupBy(t=>t.def).Select(g=>g.ToArray()).ToArray();
}
internal sealed record Pickup(Thing Thing, int Amount);
internal sealed record OrderSite(Bill Bill, WorkGiverDef Giver, Building Station, List<IngredientStock> Supplies,
    double WorkTicks, double? Iterations, bool CanModel);
internal sealed record WorkSite(Thing Target, WorkGiverDef Giver, StatDef? Speed, SkillDef? Skill, double XpFactor);
internal sealed record OpportunitySet(double? Capacity, List<OrderSite>? Orders = null, List<WorkSite>? Sites = null,
    bool HasUnmodelledWork = false, double? Demand = null, string? SharedSiteIds = null);
internal sealed record HaulRoute(Thing Item, IntVec3 Destination, int Amount);
internal readonly record struct RouteCost(double WeatherAdjustedCells, double GroundTicks)
{
    internal double Ticks(double speed) => Math.Max(0,WeatherAdjustedCells*60/Math.Max(0.15,speed)+GroundTicks);
}

// A short-lived read-only snapshot. Each bench and ingredient pool is enumerated once per capture.
internal sealed class MapWorkOpportunities(Map map, int horizonTicks)
{
    private readonly Dictionary<(IntVec3,IngredientCount,Bill), Thing[]> stockCache = new();
    private List<HaulRoute>? haulRoutes;
    private List<HaulRoute>? deliveries;
    private readonly Dictionary<(Pawn,Thing),bool> reachable = new();
    private readonly Dictionary<(IntVec3,IntVec3,Area?,PathingContext,RaceProperties),RouteCost> routeCache = new();
    private int sampledPaths;

    internal OpportunitySet Read(WorkTypeDef work)
    {
        var givers = work.workGiversByPriority;
        if (givers is not { Count: > 0 }) return new(null, HasUnmodelledWork:true);
        if (givers.All(g => g.giverClass == typeof(WorkGiver_DoBill) && g.fixedBillGiverDefs is { Count: > 0 }))
            return BillStations(givers);
        if (work.defName == "FSFSurgeon" && givers.All(g => g.defName is
            "DoBillsMedicalHumanOperation" or "DoBillsMedicalAnimalOperation" or "TakeToBedToOperate"))
            return new(map.mapPawns.SpawnedPawnsWhoShouldHaveSurgeryDoneNow.Count);
        if (givers.Any(g => g.giverClass == typeof(WorkGiver_Researcher)) ||
            givers.All(g => g.giverClass == typeof(WorkGiver_OperateScanner) || g.giverClass == typeof(WorkGiver_DeepDrill)))
            return SkilledSites(givers);
        return new(null);
    }

    private OpportunitySet SkilledSites(List<WorkGiverDef> givers)
    {
        var sites = new List<WorkSite>();
        bool unmodelled = false;
        foreach (var giver in givers)
        {
            var type = giver.giverClass;
            if (type == typeof(WorkGiver_Researcher))
            {
                var project = Find.ResearchManager.GetProject();
                if (project != null)
                    foreach (var bench in map.listerBuildings.AllBuildingsColonistOfClass<Building_ResearchBench>())
                        if (project.CanBeResearchedAt(bench, false))
                            sites.Add(new(bench, giver, StatDefOf.ResearchSpeed, SkillDefOf.Intellectual, 1));
            }
            else if (type == typeof(WorkGiver_OperateScanner) && giver.scannerDef != null)
            {
                foreach (var scanner in map.listerBuildings.AllBuildingsColonistOfDef(giver.scannerDef))
                    if (!scanner.IsBurning() && scanner.TryGetComp<CompScanner>()?.CanUseNow == true)
                        sites.Add(new(scanner, giver, StatDefOf.ResearchSpeed, SkillDefOf.Intellectual, 0.35));
            }
            else if (type == typeof(WorkGiver_DeepDrill))
            {
                foreach (var drill in map.listerBuildings.AllBuildingsColonistOfDef(ThingDefOf.DeepDrill))
                    if (drill.TryGetComp<CompDeepDrill>()?.CanDrillNow() == true)
                        sites.Add(new(drill, giver, StatDefOf.DeepDrillingSpeed, SkillDefOf.Mining, 0.65));
            }
            else if (type == typeof(WorkGiver_CreateXenogerm) && ModsConfig.BiotechActive)
            {
                foreach (var machine in map.listerBuildings.AllBuildingsColonistOfClass<Building_GeneAssembler>())
                    if (machine.CanBeWorkedOnNow.Accepted || machine.ArchitesRequiredNow > 0)
                        sites.Add(new(machine, giver, null, null, 0));
            }
            else if (type == typeof(WorkGiver_Hack))
            {
                foreach (var target in map.listerThings.ThingsInGroup(ThingRequestGroup.Hackable))
                    if (target.TryGetComp<CompHackable>() is { IsHacked:false, Autohack:true })
                        sites.Add(new(target, giver, StatDefOf.HackingSpeed, SkillDefOf.Intellectual, 1));
            }
            else if (type == typeof(WorkGiver_StudyInteract))
            {
                if (Find.StudyManager is {} manager)
                    foreach (var target in manager.GetStudiableThingsAndPlatforms(map))
                        if (target.TryGetComp<CompStudiable>() is {} study && study.CurrentlyStudiable())
                            sites.Add(new(target, giver, null, null, 0));
            }
            else unmodelled = true;
        }
        var targets = sites.Select(s => s.Target).Distinct().ToList();
        return new(unmodelled ? null : targets.Count, Sites:sites,
            HasUnmodelledWork:unmodelled, SharedSiteIds: unmodelled ? null : SiteIds(targets));
    }

    private OpportunitySet BillStations(List<WorkGiverDef> givers)
    {
        var orders = new List<OrderSite>();
        var seen = new HashSet<(Bill,WorkGiverDef)>();
        foreach (var giver in givers)
        foreach (var def in giver.fixedBillGiverDefs)
        foreach (var station in map.listerBuildings.AllBuildingsColonistOfDef(def))
        {
            if (station is not IBillGiver table || !table.CurrentlyUsableForBills() || station.IsBurning()) continue;
            foreach (var bill in table.BillStack.Bills)
            {
                if (!seen.Add((bill,giver)) || bill.suspended || bill.deleted || bill.recipe == null ||
                    (bill.recipe.requiredGiverWorkType is {} required && required != giver.workType)) continue;
                // Rely on the stock counter, but do not call Bill_Production.ShouldDoNow: it mutates bill.paused.
                double? iterations = RemainingIterations(bill);
                if (iterations <= 0) continue;
                var bound = (bill as Bill_ProductionWithUft)?.BoundUft;
                var supplies = new List<IngredientStock>();
                if (bound == null)
                    foreach (var ingredient in bill.recipe.ingredients)
                        supplies.Add(new(ingredient, FindSupplies(ingredient, bill, station)));
                double work = bound?.workLeft ?? bill.recipe.workAmount;
                if (work <= 0 && bill.recipe.products is { Count: > 0 } products)
                {
                    var stuff = supplies.SelectMany(i => i.Stock).FirstOrDefault(t => t.def.IsStuff)?.def;
                    work = bill.recipe.WorkAmountForStuff(stuff);
                }
                var order = new OrderSite(bill, giver, station, supplies, Math.Max(1, work), iterations,
                    bill is Bill_Production && work > 0 && bill.recipe.workerClass.Assembly == typeof(RecipeDef).Assembly);
                // The map may have plenty of ingredients in general, but none inside this bill's radius/filter.
                if (bound != null || SelectSupplies(order, null) != null) orders.Add(order);
            }
        }
        var distinct = orders.DistinctBy(o => o.Bill).ToArray();
        double? finite = distinct.All(o => o.Iterations.HasValue && o.CanModel)
            ? distinct.Sum(o => o.Iterations!.Value * o.WorkTicks) / horizonTicks : null;
        var stations = orders.Select(o=>o.Station).Distinct().ToArray();
        return new(stations.Length, orders, Demand:finite, SharedSiteIds:SiteIds(stations));
    }

    private static string? SiteIds(IEnumerable<Thing> sites)
    {
        var ids = sites.Select(s=>s.GetUniqueLoadID()).Order(StringComparer.Ordinal).ToArray();
        return ids.Length==0 ? null : string.Join('|',ids);
    }

    private static double? RemainingIterations(Bill bill)
    {
        if (bill is not Bill_Production p) return null;
        if (p.repeatMode == BillRepeatModeDefOf.RepeatCount) return Math.Max(0, p.repeatCount);
        if (p.repeatMode != BillRepeatModeDefOf.TargetCount) return null;
        int count = p.recipe.WorkerCounter.CountProducts(p);
        if (count >= p.targetCount || (p.pauseWhenSatisfied && p.paused && count > p.unpauseWhenYouHave)) return 0;
        int yield = p.recipe.products.FirstOrDefault()?.count ?? 1;
        return Math.Ceiling((p.targetCount - count) / (double)Math.Max(1, yield));
    }

    private Thing[] FindSupplies(IngredientCount ingredient, Bill bill, Building station)
    {
        var key = (station.Position, ingredient, bill);
        if (stockCache.TryGetValue(key,out var result)) return result;
        // Enumerating defs of this ingredient avoids scanning every tree, pawn and building on the map.
        double radiusSq = bill.ingredientSearchRadius * bill.ingredientSearchRadius;
        result = ingredient.filter.AllowedThingDefs.SelectMany(def => map.listerThings.ThingsOfDef(def))
            .Where(t => t.Spawned && !t.Position.Fogged(map) && !t.IsForbidden(Faction.OfPlayer) &&
                t.Position.DistanceToSquared(station.Position) <= radiusSq && ingredient.filter.Allows(t) && bill.IsFixedOrAllowedIngredient(t))
            .OrderBy(t => t.Position.DistanceToSquared(station.Position)).ThenBy(t => t.thingIDNumber).ToArray();
        stockCache.Add(key,result);
        return result;
    }

    internal List<Pickup>? SelectSupplies(OrderSite order, Pawn? worker)
    {
        if (order.Bill is Bill_ProductionWithUft { BoundUft: {} uft } bound)
            return worker == null || (worker == bound.BoundWorker && CanAccess(worker,uft)) ? [] : null;
        var used = new Dictionary<Thing,int>();
        foreach (var pool in order.Supplies.OrderBy(p => p.Stock.Length))
        {
            double wanted = order.Bill.recipe.Worker.GetIngredientCount(pool.Ingredient, order.Bill);
            if (wanted <= 0) continue;
            IEnumerable<IEnumerable<Thing>> sets = order.Bill.recipe.allowMixingIngredients
                ? [pool.Stock] : pool.ByDef;
            bool filled = false;
            foreach (var set in sets)
            {
                var picks = new List<Pickup>();
                double left = wanted;
                foreach (var item in set)
                {
                    if (worker != null && !CanAccess(worker,item)) continue;
                    double unit = order.Bill.recipe.IngredientValueGetter.ValuePerUnitOf(item.def);
                    if (unit <= 0) continue;
                    int take = Math.Min(item.stackCount - used.GetValueOrDefault(item), (int)Math.Ceiling(left / unit - 1e-6));
                    if (take <= 0) continue;
                    picks.Add(new(item,take)); left -= take * unit;
                    if (left <= 1e-5) break;
                }
                if (left > 1e-5) continue;
                foreach (var pick in picks) used[pick.Thing] = used.GetValueOrDefault(pick.Thing) + pick.Amount;
                filled = true;
                break;
            }
            if (!filled) return null;
        }
        return used.Select(k => new Pickup(k.Key,k.Value)).ToList();
    }

    internal bool CanAccess(Pawn pawn, Thing target)
    {
        if (reachable.TryGetValue((pawn,target),out var value)) return value;
        value = target.Spawned && !target.IsForbidden(pawn) &&
            pawn.CanReach(target, target.def.hasInteractionCell ? PathEndMode.InteractionCell : PathEndMode.Touch, Danger.Deadly);
        reachable[(pawn,target)] = value;
        return value;
    }

    internal bool AllowsWorker(Pawn pawn, OrderSite order) => WorkProfile.CapableOfGiver(pawn,order.Giver) &&
        CanAccess(pawn,order.Station) && order.Bill.PawnAllowedToStartAnew(pawn) &&
        order.Bill.recipe.PawnSatisfiesSkillRequirements(pawn);

    internal IReadOnlyList<HaulRoute> Hauling()
    {
        if (haulRoutes != null) return haulRoutes;
        haulRoutes = [];
        foreach (var item in map.listerHaulables.ThingsPotentiallyNeedingHauling().OrderBy(t=>t.thingIDNumber))
        {
            if (!item.Spawned || item.IsForbidden(Faction.OfPlayer) || item.Position.Fogged(map)) continue;
            var destination = StorageCell(item.def, item.Position, StoreUtility.CurrentStoragePriorityOf(item));
            if (destination is not {} cell) continue;
            haulRoutes.Add(new(item,cell,item.stackCount));
            if (haulRoutes.Count == 12) break; // fixed sample, independent of the number of pawns
        }
        return haulRoutes;
    }

    internal IReadOnlyList<HaulRoute> Deliveries()
    {
        if (deliveries != null) return deliveries;
        deliveries = [];
        var construction = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)
            .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
            .Where(t=>t.Faction == Faction.OfPlayer).OrderBy(t=>t.thingIDNumber);
        foreach (var site in construction)
        {
            if (site is not IConstructible blueprint || site.IsForbidden(Faction.OfPlayer)) continue;
            foreach (var material in blueprint.TotalMaterialCost())
            {
                int needed=blueprint.ThingCountNeeded(material.thingDef);
                if (needed<=0) continue;
                var source=map.listerThings.ThingsOfDef(material.thingDef).Where(t=>t.Spawned &&
                    !t.Position.Fogged(map) && !t.IsForbidden(Faction.OfPlayer)).MinBy(t=>t.Position.DistanceToSquared(site.Position));
                if (source!=null) deliveries.Add(new(source,site.Position,Math.Min(source.stackCount,needed)));
                if (deliveries.Count==12) return deliveries;
            }
        }
        return deliveries;
    }

    internal IntVec3? StorageCell(ThingDef product, IntVec3 from, StoragePriority minimum = StoragePriority.Unstored)
    {
        var group = map.haulDestinationManager.AllGroupsListInPriorityOrder
            .Where(g=>g.Settings.Priority>minimum && g.Settings.AllowedToAccept(product) && g.CellsList.Count>0)
            .OrderByDescending(g=>g.Settings.Priority).ThenBy(g=>g.CellsList[0].DistanceToSquared(from)).FirstOrDefault();
        return group?.CellsList.MinBy(c=>c.DistanceToSquared(from));
    }

    internal RouteCost Route(Pawn pawn,IntVec3 from,IntVec3 to)
    {
        if (from==to) return default;
        var pathing=map.pathing.For(pawn);
        var key=(from,to,PathUtility.GetAllowedArea(pawn),pathing,pawn.RaceProps);
        if (routeCache.TryGetValue(key,out var known)) return known;
        RouteCost result;
        // Bound synchronous game path searches; the sampled routes are shared by workers with compatible traversal.
        if (sampledPaths<40 && from.InBounds(map) && to.InBounds(map) &&
            pathing.pathGrid.Walkable(from) && pathing.pathGrid.Walkable(to))
        {
            sampledPaths++;
            using var path=map.pathFinder.FindPathNow(from,to,pawn);
            if (path.Found && path.NodesReversed.Count>1)
            {
                double distance=0,ground=0;
                var nodes=path.NodesReversed;
                for (int i=nodes.Count-2;i>=0;i--)
                {
                    var prev=nodes[i+1]; var next=nodes[i];
                    distance+=GridDistance(prev,next)*WeatherCost(prev);
                    ground+=Math.Max(0,pathing.pathGrid.Cost(next));
                }
                result=new(distance,ground);
                routeCache[key]=result;
                return result;
            }
        }
        // A deliberately labelled estimate when this capture has exhausted its route sample.
        result=new(GridDistance(from,to)*(WeatherCost(from)+WeatherCost(to))/2,0);
        routeCache[key]=result;
        return result;
    }

    private double WeatherCost(IntVec3 c) => !c.InBounds(map) || c.Roofed(map) ? 1 :
        1/Math.Max(0.05,map.weatherManager.CurMoveSpeedMultiplier);

    private static double GridDistance(IntVec3 a,IntVec3 b)
    {
        int x=Math.Abs(a.x-b.x), z=Math.Abs(a.z-b.z);
        return Math.Max(x,z)+0.41421356*Math.Min(x,z);
    }
}
