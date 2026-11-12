using System.Text.Json;
using RimWorld;
using StrategicWork.RimWorld;
using Verse;

namespace RimLazy.GameSmoke;

internal static class WorkSplitChecks
{
    internal static readonly string[] SplitIds = ("FSFNurse FSFSurgeon FSFRearming FSFTraining FSFButcher FSFRepair FSFDeconstruct " +
        "FSFHarvesting FSFDrilling FSFDrugs FSFMachining FSFFabrication FSFRefining FSFProduction FSFSmelt FSFStoneCut " +
        "FSFTransport FSFCremating FSFDeliver FSFHauling FSFScan").Split(' ');
    internal static readonly string[] OptionalIds = ("FSFTaming FSFSlaughter FSFConvert FSFPrisonCare FSFHack FSFTeach " +
        "FSFSelfTend FSFRescue FSFPollution FSFSnow FSFMerge FSFPaint FSFMechanoids FSFPruning FSFSmoothing").Split(' ');
    internal static readonly string[] ImportedIds = ["FSFHack", "FSFRescue", "FSFMechanoids"];

    internal static void Verify(ColonyCapture snapshot, string scenario)
    {
        var defs = DefDatabase<WorkTypeDef>.AllDefsListForReading;
        var owners = SplitIds.Select(id => defs.Single(d => d.defName == id).modContentPack.PackageIdPlayerFacing.ToLowerInvariant()).Distinct().ToArray();
        PlannerProbe.Check(owners.SequenceEqual([scenario == "coexist" ? PlannerProbe.LegacyId : "rimlazy.strategicwork"]),
            "Exactly one owner supplies all 21 default split columns");
        PlannerProbe.Check(SplitIds.All(id => snapshot.Works.Any(w => w.Id == id && w.StrategyId != null)),
            "All default split columns are visible and mapped to a planner strategy");
        if (scenario is "all-splits" or "change-layout")
            PlannerProbe.Check(OptionalIds.All(id => defs.Any(w => w.defName == id)), "All 15 optional work splits are active");
        else if (scenario == "legacy-settings")
            PlannerProbe.Check(OptionalIds.All(id => defs.Any(w => w.defName == id) == ImportedIds.Contains(id)),
                "Imported exactly the three work types enabled in the existing player configuration");
        else
            PlannerProbe.Check(OptionalIds.All(id => defs.All(w => w.defName != id)), "Optional splits stay off with default settings");

        var expected = new Dictionary<string,string>
        {
            ["DoBillsMedicalHumanOperation"]="FSFSurgeon", ["DoBillsMedicalAnimalOperation"]="FSFSurgeon",
            ["DoBillsButcherFlesh"]="FSFButcher", ["Repair"]="FSFRepair",
            ["GrowerHarvest"]="FSFHarvesting", ["DoBillsStonecut"]="FSFStoneCut"
        };
        if (scenario is "all-splits" or "change-layout" or "legacy-settings")
        {
            expected["Hack"]="FSFHack"; expected["FSFDoctorRescue"]="FSFRescue";
            expected["RepairMech"]="FSFMechanoids";
        }
        else expected["DoctorRescue"]="FSFNurse";
        if (scenario is "all-splits" or "change-layout")
        {
            expected["Slaughter"]="FSFSlaughter"; expected["Tame"]="FSFTaming";
            expected["CleanClearSnow"]="FSFSnow"; expected["PaintBuilding"]="FSFPaint";
        }
        var allGivers = DefDatabase<WorkGiverDef>.AllDefsListForReading;
        var catalog = allGivers.OrderBy(g => g.defName, StringComparer.Ordinal)
            .ToDictionary(g => g.defName, g => g.workType?.defName, StringComparer.Ordinal);
        File.WriteAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, "WorkCatalog.json"),
            JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented=true }));
        foreach (var pair in expected)
        {
            var giver = DefDatabase<WorkGiverDef>.GetNamed(pair.Key);
            if (giver.workType?.defName != pair.Value || !giver.workType.workGiversByPriority.Contains(giver))
                throw new InvalidOperationException($"{pair.Key} must be in the live {pair.Value} work-giver list; found {giver.workType?.defName}");
        }
        PlannerProbe.Check(true, "Representative work-givers are registered on the correct medical, production and construction columns");
        VerifySkillSensitivity(snapshot);
    }

    private static void VerifySkillSensitivity(ColonyCapture snapshot)
    {
        var choices = (from pawn in snapshot.Map.mapPawns.FreeColonistsSpawned
                       from work in new[] { "FSFRepair", "FSFHarvesting", "FSFDrilling", "FSFTraining" }
                       let def = DefDatabase<WorkTypeDef>.GetNamed(work)
                       let cell = snapshot.Inputs.Single(p => p.Id == pawn.GetUniqueLoadID()).Cells[work]
                       where cell.Eligible && def.relevantSkills is { Count: 1 } && !pawn.skills.GetSkill(def.relevantSkills[0]).TotallyDisabled
                       select (pawn, work, skill: pawn.skills.GetSkill(def.relevantSkills[0]))).First();
        var state = snapshot.Map.GetComponent<ColonyWorkState>();
        double Score() => ColonyAdapter.Capture(snapshot.Map, state.Overrides, state.LockedCells).Inputs
            .Single(p => p.Id == choices.pawn.GetUniqueLoadID()).Cells[choices.work].Suitability;
        int original = choices.skill.Level;
        try
        {
            choices.skill.Level = 1;
            double novice = Score();
            choices.skill.Level = 18;
            PlannerProbe.Check(Score() > novice, $"The actual {choices.work} score increases with its relevant skill");
        }
        finally { choices.skill.Level = original; }
    }
}
