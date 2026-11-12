using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace StrategicWork.RimWorld;

// The game's positional list remains in the save for vanilla readers. When our
// named data is present, the loaded Def order can change without moving a pawn's ranks.
[StaticConstructorOnStartup]
internal static class WorkPriorityPersistence
{
    private const string SaveKey = "rimLazyWorkPrioritiesById";
    private static readonly ConditionalWeakTable<Pawn_WorkSettings, Dictionary<string, int>> MissingColumns = new();

    static WorkPriorityPersistence()
    {
        var harmony = new Harmony("rimlazy.strategicwork.savedpriorities");
        harmony.Patch(AccessTools.Method(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.ExposeData)),
            postfix: new HarmonyMethod(typeof(WorkPriorityPersistence), nameof(ExposeNamedPriorities)));
    }

    private static void ExposeNamedPriorities(Pawn_WorkSettings __instance, DefMap<WorkTypeDef, int>? ___priorities)
    {
        if (___priorities == null || Scribe.mode is not (LoadSaveMode.Saving or LoadSaveMode.LoadingVars)) return;
        var defs = DefDatabase<WorkTypeDef>.AllDefsListForReading;
        Dictionary<string, int>? byId = null;
        if (Scribe.mode == LoadSaveMode.Saving)
        {
            byId = MissingColumns.TryGetValue(__instance, out var missing)
                ? new Dictionary<string, int>(missing, StringComparer.Ordinal)
                : new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var work in defs)
            {
                int priority = ___priorities[work];
                if (priority != 0) byId[work.defName] = priority;
                else byId.Remove(work.defName);
            }
        }
        Scribe_Collections.Look(ref byId, SaveKey, LookMode.Value, LookMode.Value);
        if (Scribe.mode != LoadSaveMode.LoadingVars || byId == null) return;
        foreach (var work in defs)
        {
            ___priorities[work] = byId.GetValueOrDefault(work.defName);
            byId.Remove(work.defName);
        }
        // Keep temporarily absent columns across repeated save/load cycles.
        MissingColumns.Remove(__instance);
        if (byId.Count > 0) MissingColumns.Add(__instance, byId);
    }
}
