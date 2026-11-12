using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace StrategicWork.RimWorld;

internal static class WorkProfile
{
    // Player overrides always win. Preserve emergency/self-care and unidentified mod columns by default.
    internal static string? SuggestStrategy(WorkTypeDef work)
    {
        switch (work.defName)
        {
            case "Firefighter": case "Patient": case "PatientBedRest": case "BasicWorker":
            case "FSFSelfTend": case "FSFRescue": return null;
            case "Warden": case "Childcare": case "Fishing": return "survival";
            case "DarkStudy": return "research";
            case "Art": return "industry";
        }
        if (Specialties.For(work) is { } role) return role.Strategy;
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

    internal static double AvailableLabor(Pawn pawn, bool rush)
    {
        if (pawn.Downed || pawn.InMentalState || (rush && pawn.Drafted)) return 0;
        int start = GenLocalDate.HourOfDay(pawn.Map);
        int hours = rush ? 1 : GenDate.HoursPerDay;
        double total = 0;
        for (int i = 0; i < hours; i++)
        {
            var assignment = pawn.timetable?.GetAssignment((start + i) % GenDate.HoursPerDay);
            // Work and Anything still compete with personal needs in the game's think tree.
            total += assignment == TimeAssignmentDefOf.Work ? 0.90 :
                assignment == null || assignment == TimeAssignmentDefOf.Anything ? 0.70 :
                assignment == TimeAssignmentDefOf.Sleep ? 0.08 : 0.18;
        }
        return total / hours;
    }
}
