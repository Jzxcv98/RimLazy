using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace StrategicWork.RimWorld;

internal sealed record BillCandidate(Bill Bill, WorkGiverDef Giver);
internal sealed record WorkAvailability(double? Capacity, List<BillCandidate>? Orders = null)
{
    internal bool Allows(Pawn pawn) => Orders is not { Count: > 0 } || Orders.Any(candidate =>
        WorkProfile.CapableOfGiver(pawn, candidate.Giver) && candidate.Bill.PawnAllowedToStartAnew(pawn) &&
        candidate.Bill.recipe.PawnSatisfiesSkillRequirements(pawn));
}

internal static class WorkProfile
{
    // Player overrides always win. Preserve emergency/self-care and unidentified mod columns by default.
    internal static string? SuggestStrategy(WorkTypeDef work)
    {
        switch (work.defName)
        {
            case "Firefighter": case "Patient": case "PatientBedRest": case "BasicWorker": return null;
            case "Warden": case "Childcare": case "Fishing": return "survival";
            case "DarkStudy": return "research";
            case "Art": case "Hauling": case "Cleaning": return "industry";
        }
        var tags = work.workTags;
        if ((tags & WorkTags.Hunting) != 0) return "defense";
        if ((tags & (WorkTags.Caring | WorkTags.Cooking | WorkTags.PlantWork | WorkTags.Animals)) != 0) return "survival";
        if ((tags & (WorkTags.Mining | WorkTags.Constructing)) != 0) return "expansion";
        if ((tags & (WorkTags.Crafting | WorkTags.Artistic | WorkTags.Hauling | WorkTags.Cleaning)) != 0) return "industry";
        if ((tags & WorkTags.Intellectual) != 0) return "research";
        if ((tags & (WorkTags.Violent | WorkTags.Shooting)) != 0) return "defense";
        if (work.workGiversByPriority is not { Count: > 0 }) return null;
        return work.workGiversByPriority.All(g => g.giverClass == typeof(WorkGiver_DoBill)) ? "industry" : null;
    }

    internal static bool CanDo(Pawn pawn, WorkTypeDef work) => !pawn.WorkTypeIsDisabled(work) &&
        work.workGiversByPriority is { Count: > 0 } givers && givers.Any(g => CapableOfGiver(pawn, g));

    internal static bool CapableOfGiver(Pawn pawn, WorkGiverDef giver) =>
        (pawn.CombinedDisabledWorkTags & giver.workTags) == 0 &&
        (giver.requiredCapacities == null || giver.requiredCapacities.All(c => pawn.health.capacities.CapableOf(c)));

    internal static double Suitability(Pawn pawn, WorkTypeDef work)
    {
        var records = work.relevantSkills?.Where(s => s != null && pawn.skills != null)
            .Select(s => pawn.skills.GetSkill(s)).Where(s => s != null && !s.TotallyDisabled).ToList();
        double aptitude = records is { Count: > 0 } ? records.Average(s => s.Level / 20d) : 0.5;
        // A small tie-break for long-term learning. An experienced worker usually wins over a novice with passion.
        double training = records is { Count: > 0 }
            ? records.Average(s => s.passion == Passion.Major ? 0.06 : s.passion == Passion.Minor ? 0.03 : 0)
            : 0;
        var stat = work.defName switch
        {
            "Research" => StatDefOf.ResearchSpeed, "Construction" => StatDefOf.ConstructionSpeed,
            "Mining" => StatDefOf.MiningSpeed, "Growing" or "PlantCutting" => StatDefOf.PlantWorkSpeed,
            "Doctor" => StatDefOf.MedicalTendSpeed, "Hauling" or "Cleaning" => StatDefOf.MoveSpeed,
            _ => StatDefOf.WorkSpeedGlobal
        };
        // Convert tiles/second into a dimensionless ratio for comparison with percentage-based work stats.
        double speed = pawn.GetStatValue(stat) / (stat == StatDefOf.MoveSpeed ? 4.6d : 1d);
        if (!double.IsFinite(speed)) speed = 1;
        return (0.4 + aptitude + training) * Math.Clamp(speed, 0.05, 5);
    }

    internal static WorkAvailability ReadAvailability(Map map, WorkTypeDef work)
    {
        var givers = work.workGiversByPriority;
        if (givers is not { Count: > 0 }) return new(null);
        if (givers.All(g => g.giverClass == typeof(WorkGiver_DoBill) && g.fixedBillGiverDefs is { Count: > 0 }))
            return ReadBillStations(map, givers);
        if (givers.Any(g => g.giverClass == typeof(WorkGiver_Researcher)))
            return ReadResearchStations(map, givers);
        return new(null);
    }

    private static WorkAvailability ReadBillStations(Map map, List<WorkGiverDef> givers)
    {
        var stations = new HashSet<Building>();
        var orders = new List<BillCandidate>();
        foreach (var giver in givers)
        foreach (var buildingDef in giver.fixedBillGiverDefs)
        foreach (var building in map.listerBuildings.AllBuildingsColonistOfDef(buildingDef))
        {
            if (building is not IBillGiver station || !station.CurrentlyUsableForBills()) continue;
            foreach (var bill in station.BillStack.Bills)
            {
                if (bill.suspended || !bill.ShouldDoNow() || bill.recipe == null) continue;
                stations.Add(building);
                orders.Add(new(bill, giver));
            }
        }
        return new(stations.Count, orders);
    }

    // The base Research column also owns hacking, scanners and study. Include their potential work sites.
    // If another mod installs a new kind of provider in this column, its station limit is unknown.
    private static WorkAvailability ReadResearchStations(Map map, List<WorkGiverDef> givers)
    {
        var targets = new HashSet<Thing>();
        foreach (var giver in givers)
        {
            if (giver.giverClass == typeof(WorkGiver_Researcher))
            {
                var project = Find.ResearchManager.GetProject();
                if (project != null)
                    foreach (var bench in map.listerBuildings.AllBuildingsColonistOfClass<Building_ResearchBench>())
                        if (project.CanBeResearchedAt(bench, false)) targets.Add(bench);
            }
            else if (giver.giverClass == typeof(WorkGiver_OperateScanner) && giver.scannerDef != null)
                targets.UnionWith(map.listerBuildings.AllBuildingsColonistOfDef(giver.scannerDef));
            else if (giver.giverClass == typeof(WorkGiver_Hack))
                targets.UnionWith(map.listerThings.ThingsInGroup(ThingRequestGroup.Hackable));
            else if (giver.giverClass == typeof(WorkGiver_StudyInteract))
            {
                var study = Find.StudyManager;
                if (study != null) targets.UnionWith(study.GetStudiableThingsAndPlatforms(map));
            }
            else return new(null);
        }
        return new(targets.Count);
    }
}
