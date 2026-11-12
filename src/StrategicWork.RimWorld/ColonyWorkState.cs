// AI 辅助生成
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StrategicWork.Core;
using Verse;

namespace StrategicWork.RimWorld;

// RimWorld constructs one component per map, including when an existing save is opened.
public sealed class ColonyWorkState : MapComponent
{
    public static readonly string[] Directions = ["survival", "expansion", "research", "industry", "defense"];
    public StrategyPreset Preset { get; set; } = DefaultPreset();
    public Dictionary<string, string> Overrides { get; private set; } = new(StringComparer.Ordinal);
    public HashSet<string> LockedCells { get; private set; } = new(StringComparer.Ordinal);
    public int PriorityOverride { get; set; }
    public WorkUndo? LastUndo { get; set; }
    public Dictionary<string, StrategyPreset> SavedPresets { get; private set; } = new(StringComparer.Ordinal);

    public ColonyWorkState(Map map) : base(map) { }

    public static StrategyPreset DefaultPreset() => new(1, "custom", [
        new("survival", 0.15m, 35m), new("expansion", 0.05m, 25m), new("research", 0m, 15m),
        new("industry", 0m, 25m), new("defense", 0m, 0m)
    ], UnweightedRemainder.PreserveExisting);

    public void SetAllocation(StrategyAllocation edited)
    {
        Preset = Preset with { Strategies = Preset.Strategies.Select(s => s.Id == edited.Id ? edited : s).ToArray() };
    }

    public void SetTrainingLossPercent(int percent)
    {
        if (percent < 0 || percent > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        Preset = Preset with { TrainingLossLimit = percent / 100m };
    }

    // Scribe owns the save lifecycle. The payload only contains IDs and values, no runtime game objects.
    public override void ExposeData()
    {
        base.ExposeData();
        string payload = Scribe.mode == LoadSaveMode.Saving
            ? JsonSerializer.Serialize(new MapData(Preset, Overrides, LockedCells, PriorityOverride, LastUndo, SavedPresets))
            : "";
        Scribe_Values.Look(ref payload, "rimLazyWorkSettings", "");
        if (Scribe.mode != LoadSaveMode.LoadingVars || string.IsNullOrEmpty(payload)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<MapData>(payload);
            if (saved == null) return;
            Preset = saved.Preset ?? DefaultPreset();
            Overrides = saved.Overrides ?? new(StringComparer.Ordinal);
            LockedCells = saved.LockedCells ?? new(StringComparer.Ordinal);
            PriorityOverride = saved.PriorityOverride;
            LastUndo = saved.LastUndo;
            SavedPresets = saved.SavedPresets ?? new(StringComparer.Ordinal);
        }
        catch (JsonException e)
        {
            Log.Warning($"[RimLazy] Could not read this map's work settings: {e.Message}");
        }
    }

    private sealed record MapData(
        StrategyPreset? Preset, Dictionary<string, string>? Overrides, HashSet<string>? LockedCells,
        int PriorityOverride, WorkUndo? LastUndo, Dictionary<string, StrategyPreset>? SavedPresets);
}
