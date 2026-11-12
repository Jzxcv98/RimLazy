using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace StrategicWork.RimWorld;

/// <summary>Work-table mods with contiguous priorities (1 is highest, 0 is disabled) may publish their live limit.</summary>
public static class PriorityCompatibility
{
    internal static readonly Dictionary<string, Func<int>> Readers = new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterRange(string packageId, Func<int> getMaximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(getMaximum);
        Readers[packageId] = getMaximum;
    }
}

internal static class PriorityDomain
{
    internal static (int Max, string Source) Resolve(int observed, int requested)
    {
        int vanilla = (int)typeof(Pawn_WorkSettings).GetField(nameof(Pawn_WorkSettings.LowestPriority))!.GetRawConstantValue()!;
        var limits = PriorityCompatibility.Readers.Where(p => ModsConfig.IsActive(p.Key))
            .Select(p => (Id: p.Key, Max: p.Value())).ToList();
        if (ModsConfig.IsActive("lauriichan.prioritymaster") && GetPriorityMasterLimit() is int maximum)
            limits.Add(("PriorityMaster", maximum));
        if (limits.Any(p => p.Max <= 0)) throw ColonyAdapter.Problem("RimLazy_InvalidRange");

        int ceiling;
        string source;
        if (limits.Count > 0)
        {
            ceiling = limits.Min(p => p.Max);
            source = "RimLazy_RangeProvider".Translate(string.Join(", ", limits.Select(p => p.Id))).ToString();
        }
        else if (observed > vanilla)
        {
            ceiling = observed;
            source = "RimLazy_RangeObserved".Translate().ToString();
        }
        else
        {
            ceiling = vanilla;
            source = "RimLazy_RangeDefault".Translate().ToString();
        }
        if (requested == 0) return (ceiling, source);
        if (requested < 1 || requested > ColonyAdapter.MaxConfiguredPriority || requested > ceiling)
            throw new InvalidOperationException("RimLazy_RangeUnverified".Translate(ceiling).ToString());
        return (requested, "RimLazy_RangeOverride".Translate(source).ToString());
    }

    internal static bool UsesDetailedSchedules() => ModsConfig.IsActive("fluffy.worktab") ||
        AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "WorkTab" &&
            a.GetType("WorkTab.PriorityManager", false) != null);

    private static int? GetPriorityMasterLimit()
    {
        // Read the running settings object, including multiplayer overrides. Never load an inactive Workshop DLL.
        var type = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "PriorityMod")
            ?.GetType("PriorityMod.Core.PriorityMaster", false);
        var settings = type?.GetField("settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var getLimit = settings?.GetType().GetMethod("GetMaxPriority", Type.EmptyTypes);
        return getLimit?.ReturnType == typeof(int) ? (int?)getLimit.Invoke(settings, null) : null;
    }
}
