// AI 辅助生成
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using StrategicWork.Core;
using UnityEngine;
using Verse;

namespace StrategicWork.RimWorld;

public sealed class ColonyWorkWindow : MainTabWindow
{
    private enum Page { Strategy, Mapping, Locks, Results }
    private const float Row = 34f;
    private Page page;
    private Map? map;
    private ColonyWorkState? state;
    private ColonyCapture? capture;
    private WorkPlan? plan;
    private double plannedLabor;
    private List<PlannedCell> changes = [];
    private Dictionary<string, string> pawnLabels = [];
    private Dictionary<string, string> workLabels = [];
    private bool captureAttempted;
    private bool applied;
    private string notice = "";
    private string presetName = "";
    private string priorityBuffer = "";
    private string? selectedPawn;
    private Vector2 scroll;

    public override Vector2 RequestedTabSize => new(Math.Min(860, UI.screenWidth), Math.Min(665, UI.screenHeight - 70));

    public override void PostOpen()
    {
        base.PostOpen();
        Bind(Find.CurrentMap);
    }

    public override void PostClose()
    {
        Bind(null);
        base.PostClose();
    }

    private void Bind(Map? newMap)
    {
        map = newMap;
        state = map?.GetComponent<ColonyWorkState>();
        page = Page.Strategy;
        presetName = "";
        priorityBuffer = (state?.PriorityOverride ?? 0).ToString(CultureInfo.InvariantCulture);
        selectedPawn = null;
        scroll = Vector2.zero;
        Invalidate();
    }

    private void Invalidate(bool refreshCapture = true)
    {
        if (refreshCapture)
        {
            capture = null;
            captureAttempted = false;
        }
        plan = null;
        changes.Clear();
        applied = false;
        notice = "";
    }

    private void Capture()
    {
        captureAttempted = true;
        capture = null;
        capture = ColonyAdapter.Capture(map!, state!.Overrides, state.LockedCells, state.PriorityOverride);
        pawnLabels = capture.Pawns.ToDictionary(p => p.Id, p => p.Label);
        workLabels = capture.Works.ToDictionary(w => w.Id, w => w.Label);
    }

    private void Solve()
    {
        plan = null;
        changes.Clear();
        Capture();
        var current = capture!;
        plan = WorkPlanner.Plan(current.Inputs, current.Columns, state!.Preset, current.MaxPriority);
        plannedLabor = current.Inputs.Sum(p => p.Labor);
        changes = plan.Cells.Where(c => c.Before != c.After).ToList();
        applied = false;
    }

    private void Perform(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            if (ex is not (InvalidOperationException or ArgumentException))
                Log.Error($"[RimLazy] Work allocation failed: {ex}");
            notice = ex.Message;
        }
    }

    private void Show(Page target)
    {
        page = target;
        scroll = Vector2.zero;
        if (applied && target is Page.Mapping or Page.Locks) captureAttempted = false;
    }

    public override void DoWindowContents(Rect area)
    {
        if (map != Find.CurrentMap) Bind(Find.CurrentMap);
        if (map == null || state == null) { Widgets.Label(area, T("SelectMap")); return; }
        if (!captureAttempted) Perform(Capture);

        float spacing = 4f;
        float tabWidth = (area.width - 3 * spacing) / 4;
        foreach (var tab in Enum.GetValues<Page>())
        {
            var button = new Rect(area.x + (int)tab * (tabWidth + spacing), area.y, tabWidth, Row);
            if (Widgets.ButtonText(button, T("Page_" + tab))) Show(tab);
            if (tab == page) Widgets.DrawLineHorizontal(button.x, button.yMax - 2, button.width);
        }
        decimal minimumPercent = state.Preset.Strategies.Sum(s => s.MinimumShare) * 100;
        string footer = minimumPercent > 100 ? T("InvalidMinimum", minimumPercent.ToString("0")) : notice;
        if (capture?.ManualPriorities == false)
            footer = footer.Length == 0 ? T("EnableManual") : footer + "\n" + T("EnableManual");
        float footerHeight = footer.Length == 0 ? 0 : Math.Min(80, Math.Max(26, Text.CalcHeight(footer, area.width)));
        var actions = new Rect(area.x, area.yMax - Row, area.width, Row);
        if (footerHeight > 0)
        {
            var status = new Rect(area.x, actions.y - footerHeight - 4, area.width, footerHeight);
            Widgets.Label(status, footer);
            TooltipHandler.TipRegion(status, footer);
        }
        var body = new Rect(area.x, area.y + Row + 9, area.width,
            Math.Max(10, actions.y - area.y - Row - 22 - footerHeight));
        switch (page)
        {
            case Page.Strategy: DrawStrategy(body); break;
            case Page.Mapping: DrawMapping(body); break;
            case Page.Locks: DrawLocks(body); break;
            case Page.Results: DrawResults(body); break;
        }
        DrawActions(actions);
    }

    private void DrawActions(Rect rect)
    {
        float width = (rect.width - 12) / 3;
        var preview = new Rect(rect.x, rect.y, width, rect.height);
        var apply = new Rect(preview.xMax + 6, rect.y, width, rect.height);
        var undo = new Rect(apply.xMax + 6, rect.y, width, rect.height);
        bool valid = state!.Preset.Strategies.Sum(s => s.MinimumShare) <= 1m;
        if (Widgets.ButtonText(preview, T("Preview"), active: valid))
            Perform(() => { Solve(); notice = T("Previewed", changes.Count); Show(Page.Results); });
        if (capture?.ManualPriorities == false)
        {
            var work = DefDatabase<MainButtonDef>.GetNamedSilentFail("Work");
            if (Widgets.ButtonText(apply, T("OpenWork"), active: work != null) && work != null)
            {
                Find.MainTabsRoot.SetCurrentTab(work, true);
                return;
            }
        }
        else if (Widgets.ButtonText(apply, T("Apply"), active: valid && capture != null))
            Perform(() => {
                if (plan == null || applied) Solve();
                WorkUndo history;
                try { history = ColonyAdapter.Apply(capture!, plan!); }
                catch
                {
                    Invalidate(); // The next attempt must use a new snapshot of the live table.
                    throw;
                }
                if (history.Edits.Count > 0) state.LastUndo = history;
                applied = true;
                notice = T("Applied", history.Edits.Count);
                Show(Page.Results);
            });
        if (Widgets.ButtonText(undo, T("Undo"), active: state.LastUndo != null && capture?.ManualPriorities == true))
            Perform(() => {
                ColonyAdapter.Undo(map!, state.LastUndo!, state.LockedCells);
                state.LastUndo = null;
                Invalidate();
                notice = T("Undone");
            });
    }

    private void DrawStrategy(Rect area)
    {
        var config = state!;
        // Strategy controls scroll as well on a small screen or at a large UI scale.
        float viewWidth = area.width - 18;
        float hintHeight = Math.Max(38, Text.CalcHeight(T("AllocationHelp"), viewWidth) + 5);
        decimal minimumTotal = config.Preset.Strategies.Sum(s => s.MinimumShare) * 100;
        bool spareWithNoWeights = minimumTotal < 100 && config.Preset.Strategies.All(s => s.RelativeWeight == 0);
        string spareHint = spareWithNoWeights
            ? T(config.Preset.UnweightedRemainder == UnweightedRemainder.PreserveExisting ? "ZeroKeep" : "ZeroLeave",
                (100 - minimumTotal).ToString("0"))
            : "";
        float spareHeight = spareWithNoWeights ? Text.CalcHeight(spareHint, viewWidth) + 6 : 0;
        var view = new Rect(0, 0, viewWidth, hintHeight + 11 * Row + 20 + spareHeight);
        Widgets.BeginScrollView(area, ref scroll, view);
        try
        {
            float y = 0;
            Widgets.Label(new Rect(0, y, view.width, hintHeight), T("AllocationHelp"));
            y += hintHeight;
            float first = view.width * 0.28f;
            float field = (view.width - first) / 2;
            Widgets.Label(new Rect(0, y, first, Row), T("Direction"));
            Widgets.Label(new Rect(first, y, field, Row), T("Minimum"));
            Widgets.Label(new Rect(first + field, y, field, Row), T("Weight"));
            y += Row;
            foreach (var allocation in config.Preset.Strategies)
            {
                Widgets.Label(new Rect(0, y + 3, first, Row), Direction(allocation.Id));
                int minimum = Slider(new Rect(first, y, field - 10, Row), (int)(allocation.MinimumShare * 100), true);
                int weight = Slider(new Rect(first + field, y, field - 4, Row), (int)allocation.RelativeWeight, false);
                if (minimum != allocation.MinimumShare * 100 || weight != allocation.RelativeWeight)
                {
                    config.SetAllocation(new(allocation.Id, minimum / 100m, weight));
                    Invalidate(refreshCapture: false);
                }
                y += Row;
            }
            decimal total = config.Preset.Strategies.Sum(s => s.MinimumShare) * 100;
            Widgets.Label(new Rect(0, y, view.width, Row), T(total > 100 ? "InvalidMinimum" : "MinimumTotal", total.ToString("0")));
            y += Row;
            bool preserve = config.Preset.UnweightedRemainder == UnweightedRemainder.PreserveExisting;
            bool prior = preserve;
            Widgets.CheckboxLabeled(new Rect(0, y, view.width, Row), T("PreserveRemainder"), ref preserve);
            if (prior != preserve)
            {
                config.Preset = config.Preset with { UnweightedRemainder = preserve ? UnweightedRemainder.PreserveExisting : UnweightedRemainder.LeaveUnassigned };
                Invalidate(refreshCapture: false);
            }
            y += Row + 6;
            if (spareWithNoWeights)
            {
                Widgets.Label(new Rect(0, y, view.width, spareHeight), spareHint);
                y += spareHeight;
            }
            if (Widgets.ButtonText(new Rect(0, y, first, Row - 3), T("LoadPreset"))) PresetMenu();
            presetName = Widgets.TextField(new Rect(first + 5, y, field - 10, Row - 3), presetName, 48);
            TooltipHandler.TipRegion(new Rect(first, y, field, Row), T("NamePreset"));
            if (Widgets.ButtonText(new Rect(first + field, y, field, Row - 3), T("SavePreset"),
                    active: total <= 100 && !string.IsNullOrWhiteSpace(presetName)))
            {
                string name = presetName.Trim();
                config.SavedPresets[name] = config.Preset;
                notice = T("Saved", name);
            }
        }
        finally { Widgets.EndScrollView(); }
    }

    private static int Slider(Rect rect, int current, bool percent)
    {
        const float number = 45;
        Widgets.Label(new Rect(rect.x, rect.y + 3, number, rect.height), current + (percent ? "%" : ""));
        return (int)Math.Round(Widgets.HorizontalSlider(new Rect(rect.x + number, rect.y, rect.width - number, rect.height),
            current, 0, 100, roundTo: 1));
    }

    private void PresetMenu()
    {
        void Select(StrategyPreset preset, string name)
        {
            state!.Preset = preset;
            presetName = name;
            Invalidate(refreshCapture: false);
        }
        var menu = new List<FloatMenuOption> {
            new(T("DefaultPreset"), () => Select(ColonyWorkState.DefaultPreset(), ""))
        };
        foreach (var pair in state!.SavedPresets.OrderBy(p => p.Key, StringComparer.Ordinal))
            menu.Add(new(pair.Key, () => Select(pair.Value, pair.Key)));
        Find.WindowStack.Add(new FloatMenu(menu));
    }

    private void DrawMapping(Rect area)
    {
        var data = capture;
        float y = area.y;
        string limits = data == null ? T("NoCapture") : T("Detected", data.MaxPriority, data.PrioritySource);
        float limitsHeight = Math.Max(Row, Text.CalcHeight(limits, area.width) + 4);
        Widgets.Label(new Rect(area.x, y, area.width, limitsHeight), limits);
        y += limitsHeight;
        string limitHelp = T("OverrideHint");
        float fieldHeight = Math.Max(Row, Text.CalcHeight(limitHelp, area.width - 83) + 4);
        Widgets.Label(new Rect(area.x, y, area.width - 83, fieldHeight), limitHelp);
        int levels = state!.PriorityOverride;
        Widgets.TextFieldNumeric(new Rect(area.xMax - 78, y, 78, Row - 3), ref levels, ref priorityBuffer, 0, ColonyAdapter.MaxConfiguredPriority);
        if (levels != state.PriorityOverride)
        {
            state.PriorityOverride = levels;
            Invalidate();
        }
        if (data == null) return;
        y += fieldHeight;
        string help = T("MappingHelp");
        float h = Math.Max(Row, Text.CalcHeight(help, area.width) + 5);
        Widgets.Label(new Rect(area.x, y, area.width, h), help);
        y += h;
        var list = new Rect(area.x, y, area.width, Math.Max(1, area.yMax - y));
        Rows(list, data.Works.Count, (index, row) => {
            var work = data.Works[index];
            float split = row.width * 0.51f;
            Widgets.Label(new Rect(0, row.y + 3, split - 5, Row), work.Label);
            TooltipHandler.TipRegion(new Rect(0, row.y, split, Row), work.Label + "\n" + work.Id);
            bool custom = state.Overrides.ContainsKey(work.Id);
            var label = work.StrategyId is { Length: > 0 } strategy ? Direction(strategy) : T("KeepColumn");
            if (!custom) label = T("Automatic", label);
            if (Widgets.ButtonText(new Rect(split, row.y, row.width - split, Row - 3), label)) MappingMenu(work.Id);
        });
    }

    private void MappingMenu(string workId)
    {
        void Set(string? value)
        {
            if (value == null) state!.Overrides.Remove(workId);
            else state!.Overrides[workId] = value;
            Invalidate();
        }
        var options = new List<FloatMenuOption> {
            new(T("AutoMapping"), () => Set(null)), new(T("KeepColumn"), () => Set(""))
        };
        foreach (var direction in ColonyWorkState.Directions)
            options.Add(new(Direction(direction), () => Set(direction)));
        Find.WindowStack.Add(new FloatMenu(options));
    }

    private void DrawLocks(Rect area)
    {
        var data = capture;
        if (data == null || data.Pawns.Count == 0) { Widgets.Label(area, T("NoWorkers")); return; }
        if (selectedPawn == null || !pawnLabels.ContainsKey(selectedPawn)) selectedPawn = data.Pawns[0].Id;
        var selected = selectedPawn;
        float y = area.y;
        if (Widgets.ButtonText(new Rect(area.x, y, Math.Min(area.width, 340), Row), pawnLabels[selected]))
        {
            Find.WindowStack.Add(new FloatMenu(data.Pawns.Select(p => new FloatMenuOption(p.Label, () => {
                selectedPawn = p.Id; scroll = Vector2.zero;
            })).ToList()));
        }
        y += Row + 5;
        float hint = Math.Max(Row, Text.CalcHeight(T("LocksHelp"), area.width) + 4);
        Widgets.Label(new Rect(area.x, y, area.width, hint), T("LocksHelp"));
        y += hint;
        var pawn = data.Inputs.First(p => p.Id == selected);
        Rows(new Rect(area.x, y, area.width, Math.Max(1, area.yMax - y)), data.Works.Count, (index, row) => {
            var work = data.Works[index];
            if (!pawn.Cells.TryGetValue(work.Id, out var cell)) return;
            var key = ColonyAdapter.CellKey(pawn.Id, work.Id);
            bool before = state!.LockedCells.Contains(key);
            bool locked = before;
            string priority = cell.Eligible ? Priority(cell.Current) : T("Ineligible");
            Widgets.CheckboxLabeled(row, work.Label + "  ·  " + priority, ref locked, disabled: !cell.Eligible && !locked);
            if (locked == before) return;
            if (locked) state.LockedCells.Add(key);
            else state.LockedCells.Remove(key);
            Invalidate();
        });
    }

    private void DrawResults(Rect area)
    {
        if (plan == null) { Widgets.Label(area, T("PreviewFirst")); return; }
        var estimates = plan.Strategies;
        double total = plannedLabor;
        float y = area.y;
        string advice = T("ResultsHelp");
        float helpHeight = Math.Max(Row, Text.CalcHeight(advice, area.width) + 4);
        Widgets.Label(new Rect(area.x, y, area.width, helpHeight), advice);
        y += helpHeight;
        var list = new Rect(area.x, y, area.width, Math.Max(1, area.yMax - y));
        Rows(list, estimates.Count + 2 + changes.Count, (i, row) => {
            string line;
            if (i < estimates.Count)
            {
                var s = estimates[i];
                line = T("Estimate", Direction(s.Id), Percent(s.TargetLabor, total), Percent(s.EstimatedLabor, total));
                if (s.Reason != ShortfallReason.None) line += "  ·  " + T("Reason_" + s.Reason);
            }
            else if (i == estimates.Count) line = T("Remainder", Percent(plan.UnassignedLabor, total), Percent(plan.UnmappedLabor, total));
            else if (i == estimates.Count + 1) line = T("ChangedCells", changes.Count);
            else
            {
                var cell = changes[i - estimates.Count - 2];
                line = T("Change", pawnLabels.GetValueOrDefault(cell.PawnId, cell.PawnId), workLabels.GetValueOrDefault(cell.WorkId, cell.WorkId),
                    Priority(cell.Before), Priority(cell.After));
            }
            Widgets.Label(row, line);
            TooltipHandler.TipRegion(row, line);
        });
    }

    private void Rows(Rect area, int count, Action<int, Rect> draw)
    {
        var view = new Rect(0, 0, Math.Max(1, area.width - 18), count * Row);
        Widgets.BeginScrollView(area, ref scroll, view);
        try
        {
            int first = Math.Max(0, (int)(scroll.y / Row));
            int last = Math.Min(count, first + (int)Math.Ceiling(area.height / Row) + 2);
            for (int i = first; i < last; i++) draw(i, new Rect(0, i * Row, view.width, Row));
        }
        finally { Widgets.EndScrollView(); }
    }

    private static string Percent(double value, double total) => (total > 0 ? value / total : 0).ToString("P0");
    private static string Priority(int value) => value == 0 ? T("Off") : value.ToString();
    private static string Direction(string id) => T("Direction_" + id);
    private static string T(string key) => ("RimLazy_" + key).Translate().ToString();
    private static string T(string key, params NamedArgument[] args) => ("RimLazy_" + key).Translate(args).ToString();
}
