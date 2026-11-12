using RimWorld;
using Verse;

namespace StrategicWork.RimWorld;

// defNames are persistent game IDs, including those used in existing Complex Jobs saves.
internal sealed record Specialty(string Strategy, string Family, string Speed, string? Outcome = null, double SkillWeight = 1);

internal static class Specialties
{
    private static readonly Dictionary<string, Specialty> Roles = new(StringComparer.Ordinal);
    static Specialties()
    {
        Add("Doctor",          "survival",  "Doctor",       "MedicalTendSpeed", "MedicalTendQuality");
        Add("FSFSurgeon",      "survival",  "Doctor",       "MedicalOperationSpeed", "MedicalSurgerySuccessChance");
        Add("FSFNurse",        "survival",  "Doctor",       "MoveSpeed", skillWeight: 0.2);
        Add("FSFTraining",     "survival",  "Handling",     "TrainAnimalChance");
        Add("FSFTaming",       "survival",  "Handling",     "TameAnimalChance");
        Add("FSFSlaughter",    "survival",  "Handling",     "MoveSpeed");
        Add("Cooking",         "survival",  "Cooking",      "CookSpeed");
        Add("FSFButcher",      "survival",  "Cooking",      "ButcheryFleshSpeed", "ButcheryFleshEfficiency");
        Add("Growing",         "survival",  "Growing",      "PlantWorkSpeed");
        Add("FSFHarvesting",   "survival",  "Growing",      "PlantWorkSpeed", "PlantHarvestYield");
        Add("FSFPruning",      "survival",  "Growing",      "PruningSpeed");
        Add("Construction",    "expansion", "Construction", "ConstructionSpeed", "ConstructSuccessChance");
        Add("FSFRepair",       "expansion", "Construction", "ConstructionSpeed");
        Add("FSFDeconstruct",  "expansion", "Construction", "ConstructionSpeed");
        Add("FSFSmoothing",    "expansion", "Construction", "SmoothingSpeed");
        Add("FSFPaint",        "expansion", "Construction", "GeneralLaborSpeed");
        Add("Mining",          "expansion", "Mining",       "MiningSpeed", "MiningYield");
        Add("FSFDrilling",     "expansion", "Mining",       "DeepDrillingSpeed");
        Add("FSFScan",         "expansion", "Mining",       "ResearchSpeed");
        Add("FSFRearming",     "defense",   "Readiness",    "MoveSpeed", "CarryingCapacity");
        Add("FSFDrugs",        "industry",  "Crafting",     "DrugSynthesisSpeed");
        Add("FSFMachining",    "industry",  "Smithing",     "GeneralLaborSpeed");
        Add("FSFFabrication",  "industry",  "Smithing",     "GeneralLaborSpeed");
        Add("FSFRefining",     "industry",  "Crafting",     "GeneralLaborSpeed");
        Add("FSFProduction",   "industry",  "Crafting",     "GeneralLaborSpeed");
        Add("FSFSmelt",        "industry",  "Crafting",     "SmeltingSpeed");
        Add("FSFStoneCut",     "industry",  "Crafting",     "GeneralLaborSpeed");
        Add("FSFCremating",    "industry",  "Hauling",      "GeneralLaborSpeed");
        foreach (var id in new[] { "Hauling", "FSFHauling", "FSFDeliver", "FSFTransport", "FSFMerge" })
            Add(id, "industry", "Hauling", "MoveSpeed", "CarryingCapacity");
        foreach (var id in new[] { "Cleaning", "FSFPollution", "FSFSnow" })
            Add(id, "industry", "Cleaning", "CleaningSpeed");
        Add("FSFConvert",      "survival",  "Warden",       "ConversionPower");
        Add("FSFPrisonCare",   "survival",  "Warden",       "MoveSpeed");
        Add("FSFHack",         "research",  "Research",     "HackingSpeed");
        Add("FSFMechanoids",   "industry",  "Smithing",     "MechRepairSpeed");
        Add("FSFTeach",        "survival",  "Childcare",    "WorkSpeedGlobal");
        Add("FSFSelfTend",     "survival",  "Doctor",       "MedicalTendSpeed", "MedicalTendQuality");
        Add("FSFRescue",       "survival",  "Doctor",       "MoveSpeed", "CarryingCapacity");
    }

    private static void Add(string id, string strategy, string family, string speed, string? outcome = null,
        double skillWeight = 1) => Roles.Add(id, new Specialty(strategy, family, speed, outcome, skillWeight));

    internal static Specialty? For(WorkTypeDef work) => Roles.GetValueOrDefault(work.defName);

    internal static double Performance(Pawn pawn, Specialty specialty)
    {
        double speed = RelativeStat(pawn, specialty.Speed);
        if (specialty.Outcome is not { } outcome) return speed;
        // Balance speed against the result of the job. Low success should make a fast surgeon less suitable.
        double result = RelativeStat(pawn, outcome);
        return Math.Sqrt(speed * result) * (specialty.Outcome == "MedicalSurgerySuccessChance" ? Math.Min(1, result) : 1);
    }

    private static double RelativeStat(Pawn pawn, string statName)
    {
        // Some DLC stats are absent when their expansion is inactive.
        var stat = DefDatabase<StatDef>.GetNamedSilentFail(statName);
        if (stat == null) return 1;
        double baseline = stat.defaultBaseValue > 0 ? stat.defaultBaseValue : 1;
        double value = pawn.GetStatValue(stat) / baseline;
        return double.IsFinite(value) ? Math.Clamp(value, 0.05, 5) : 1;
    }
}
