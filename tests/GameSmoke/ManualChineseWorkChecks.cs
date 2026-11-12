using Verse;

namespace RimLazy.GameSmoke;

internal static class ManualChineseWorkChecks
{
    internal static void Verify()
    {
        var lang = LanguageDatabase.activeLanguage;
        PlannerProbe.Check(Prefs.DevMode && lang.LegacyFolderName == "ChineseSimplified",
            "Manual regression runs with Simplified Chinese and developer diagnostics active");
        var errors = lang.loadErrors.Concat(lang.defInjections.SelectMany(package => package.loadErrors)).ToArray();
        if (errors.Length > 0 || lang.anyError)
            throw new InvalidOperationException($"Translation loader reported {errors.Length} errors: {string.Join(" / ", errors.Take(5))}");
        PlannerProbe.Check(true, "All keyed and DefInjected language packages loaded without errors");

        var optionalIds = WorkSplitChecks.OptionalIds;
        var enabled = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(def => optionalIds.Contains(def.defName)).ToList();
        foreach (var def in enabled)
        {
            string[] fields = [def.labelShort, def.pawnLabel, def.gerundLabel, def.verb, def.description];
            if (fields.Any(field => field == null || !field.Any(c => c is >= '\u3400' and <= '\u9fff')))
                throw new InvalidOperationException($"Missing Simplified Chinese field for enabled optional work {def.defName}");
        }
        PlannerProbe.Check(DefDatabase<WorkTypeDef>.GetNamed("FSFNurse").labelShort == "护理",
            "Always-available work retains its normal Simplified Chinese DefInjected label");
        PlannerProbe.Check(true, $"All five work fields localized on {enabled.Count} enabled optional work columns");
    }
}
