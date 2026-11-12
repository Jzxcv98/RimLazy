using RimWorld;
using Verse;

namespace StrategicWork.RimWorld;

// defNames are persistent game IDs, including those used in existing Complex Jobs saves.
internal sealed record Specialty(string Strategy, string Family, string Speed, string? Outcome = null);

internal static class Specialties
{
    private static readonly Dictionary<string, Specialty> Roles = new(StringComparer.Ordinal);
    static Specialties()
    {
        Add("Doctor",          "survival",  "Doctor",       "MedicalTendSpeed", "MedicalTendQuality");
        Add("FSFSurgeon",      "survival",  "Doctor",       "MedicalOperationSpeed", "MedicalSurgerySuccessChance");
        Add("FSFNurse",        "survival",  "Doctor",       "MoveSpeed");
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

    private static void Add(string id, string strategy, string family, string speed, string? outcome = null) => Roles.Add(id, new Specialty(strategy, family, speed, outcome));

    internal static Specialty? For(WorkTypeDef work) => Roles.GetValueOrDefault(work.defName);

    internal static SkillDef? LearningSkill(string id)
    {
        return id switch
        {
            "Research" or "FSFHack" or "FSFScan" => SkillDefOf.Intellectual,
            "Construction" or "FSFRepair" or "FSFDeconstruct" or "FSFSmoothing" => SkillDefOf.Construction,
            "Growing" or "FSFHarvesting" or "PlantCutting" or "FSFPruning" => SkillDefOf.Plants,
            "FSFDrilling" or "Mining" => SkillDefOf.Mining,
            "FSFMechanoids" => SkillDefOf.Crafting,
            "FSFTraining" or "FSFTaming" or "Handling" or "Fishing" => SkillDefOf.Animals,
            "Doctor" or "FSFSelfTend" or "FSFSurgeon" => SkillDefOf.Medicine,
            _ => null
        };
    }

    internal static double LearningFactor(string id) => id switch
    {
        "Construction" or "FSFDeconstruct" => 2.5,
        "FSFRepair" or "FSFMechanoids" => 0.5,
        "FSFDrilling" => 0.65,
        "Mining" => 0.7,
        "FSFScan" => 0.35,
        "Growing" or "FSFHarvesting" or "FSFPruning" => 0.85,
        "Fishing" => 0.25,
        _ => 1
    };
}
