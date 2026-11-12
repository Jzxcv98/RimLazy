using StrategicWork.Core;
using StrategicWork.RimWorld;
using Verse;

namespace RimLazy.GameSmoke;

internal static class NormalizedBudgetChecks
{
    private const string PresetName = "QA 120% minimum shares";
    private static readonly StrategyAllocation[] Inputs =
    [
        new("survival", .15m, 35), new("expansion", .05m, 25), new("research", 0, 15),
        new("industry", 1m, 25), new("defense", 0, 0)
    ];

    internal static void Verify(Map map, ColonyWorkState state, ColonyWorkWindow window)
    {
        var original = state.Preset;
        var previousUndo = state.LastUndo;
        var screenshotPreset = original with { Strategies = Inputs };
        window.SelectPreset(screenshotPreset, "");
        window.SaveCurrentPreset(PresetName);
        window.SelectPreset(original, "");
        window.SelectPreset(state.SavedPresets[PresetName], PresetName);
        try
        {
            VerifyTargets(window.PreviewAllocation(false), "Routine");
            window.SetRushFocus("research");
            VerifyTargets(window.PreviewAllocation(true), "Focused rush");
            var undo = window.ApplyPreview();
            var readback = ColonyAdapter.Capture(map, state.Overrides, state.LockedCells);
            var pawns = readback.Inputs.ToDictionary(p => p.Id);
            PlannerProbe.Check(undo.Edits.All(e => pawns[e.PawnId].Cells[e.WorkId].Current == e.After),
                "Applied 120% minimum shares through the Planner window and read back the edited cells");
            if (undo.Edits.Count > 0) ColonyAdapter.Undo(map, undo, state.LockedCells);
            VerifySavedInput(state);
        }
        finally
        {
            state.LastUndo = previousUndo;
            window.SelectPreset(original, "");
        }
    }

    private static void VerifyTargets(WorkPlan plan, string mode)
    {
        double total = plan.Strategies.Sum(s => s.TargetLabor);
        PlannerProbe.Check(total > 0 && Inputs.All(a =>
            Math.Abs(plan.Strategies.Single(s => s.Id == a.Id).TargetLabor / total - (double)a.MinimumShare / 1.2) < 1e-8),
            $"{mode} preview scales 15:5:0:100:0 to 12.5%, 4.17%, 0%, 83.33%, 0% without allocating leftover labor");
    }

    internal static void VerifySavedInput(ColonyWorkState state) =>
        PlannerProbe.Check(state.SavedPresets.TryGetValue(PresetName, out var result) && result.Strategies.SequenceEqual(Inputs),
            "Named preset keeps the original 15%, 5%, 100% slider inputs and relative weights");
}
