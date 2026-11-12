using System.Reflection;
using System.Xml.Linq;
using RimWorld;
using StrategicWork.RimWorld;
using UnityEngine;
using Verse;

namespace RimLazy.GameSmoke;

internal static class WorkTableChecks
{
    internal static void VerifyPrioritySettingsImport()
    {
        var directory = GenFilePaths.ConfigFolderPath;
        string source = Path.Combine(directory, "Mod_1994006442_PriorityMaster.xml");
        byte[] original = File.ReadAllBytes(source);
        var xml = XDocument.Load(source);
        var data = xml.Root!.Element("ModSettings")!;
        var palette = data.Element("HexColors")!.Elements("li").Select(c => c.Value).ToArray();
        var settings = RimLazyMod.PrioritySettings;
        PlannerProbe.Check(settings.Maximum == (int)data.Element("HighestPriority")! &&
            settings.DefaultPriority == (int)data.Element("DefaultPriority")! &&
            settings.Gradient == bool.Parse(data.Element("Gradient")!.Value) &&
            palette.SequenceEqual(settings.Palette, StringComparer.OrdinalIgnoreCase),
            "Imported the player's saved six-tier settings, default, gradient and both color stops");
        var persisted = Directory.EnumerateFiles(directory, "*.xml").Select(path => XDocument.Load(path).Root?.Element("ModSettings"))
            .SingleOrDefault(element => element?.Attribute("Class")?.Value == typeof(NativePrioritySettings).FullName);
        PlannerProbe.Check(persisted != null &&
            (int?)persisted.Element("maximumPriority") is null or 6 && (int?)persisted.Element("defaultPriority") is null or 3 &&
            (persisted.Element("gradient") == null || bool.Parse(persisted.Element("gradient")!.Value)) &&
            palette.SequenceEqual(persisted.Element("palette")!.Elements("li").Select(c=>c.Value), StringComparer.OrdinalIgnoreCase),
            "Native settings persisted to an independent RimLazy configuration file");
        // The player's current values happen to equal all four factory defaults. A separate settings object and
        // distinctive data exercise the import routine without replacing the live settings or touching the user's file.
        try
        {
            data.SetElementValue("HighestPriority", 9);
            data.SetElementValue("DefaultPriority", 7);
            data.SetElementValue("Gradient", "False");
            data.Element("HexColors")!.ReplaceNodes(new XElement("li","#123456"),new XElement("li","#ABCDEF"));
            xml.Save(source);
            var distinctive = new NativePrioritySettings();
            var import = typeof(NativePrioritySettings).GetMethod("ImportLegacySettings",BindingFlags.Instance | BindingFlags.NonPublic)!;
            var imported = import.Invoke(distinctive,null);
            PlannerProbe.Check(imported is true && distinctive.Maximum == 9 && distinctive.DefaultPriority == 7 &&
                !distinctive.Gradient && distinctive.Palette.SequenceEqual(["#123456","#ABCDEF"]),
                "Importer preserves distinctive nondefault limits and per-tier colors from PriorityMaster");
        }
        finally { File.WriteAllBytes(source,original); }
    }

    internal static void Verify(Map map)
    {
        var tab = DefDatabase<MainButtonDef>.GetNamed("Work");
        Find.MainTabsRoot.SetCurrentTab(tab, false);
        PlannerProbe.Check(Find.MainTabsRoot.OpenTab == tab, "Opened the normal Work tab");
        var table = (PawnTable?)typeof(MainTabWindow_PawnTable).GetField("table", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(tab.TabWindow) ?? throw new InvalidOperationException("Work tab did not initialize its pawn table");
        var column = table.Columns
            .Where(c => c.workType != null && WorkSplitChecks.SplitIds.Contains(c.workType.defName) &&
                        c.workType.workGiversByPriority.Any(g => !g.emergency))
            .OrderByDescending(c => table.PawnsListForReading.Count(p => !p.WorkTypeIsDisabled(c.workType)))
            .First();
        var work = column.workType;
        var pawns = table.PawnsListForReading.Where(p => p.workSettings is { EverWork:true } && !p.WorkTypeIsDisabled(work)).ToArray();
        var pawn = pawns.First();
        var before = pawns.ToDictionary(p => p, p => p.workSettings.GetPriority(work));
        var previousEvent = Event.current;
        var settings = RimLazyMod.PrioritySettings;
        try
        {
            PlannerProbe.Check(NativeWorkPriorities.Active && NativeWorkPriorities.Maximum == 6 && NativeWorkPriorities.Default == 3,
                "Native range is six and default is three on the normal work table");
            pawn.workSettings.SetPriority(work, 4);
            ClickCell(pawn, work, 1);
            PlannerProbe.Check(pawn.workSettings.GetPriority(work) == 5, "Work cell right-click selects tier five");
            ClickCell(pawn, work, 1);
            PlannerProbe.Check(pawn.workSettings.GetPriority(work) == 6, "Work cell right-click selects tier six");
            CheckSchedulerOrder(pawn, work);
            var tip = WidgetsWork.TipForPawnWorker(pawn,work,false);
            var high = WidgetsWork.ColorOfPriority(1);
            var low = WidgetsWork.ColorOfPriority(6);
            PlannerProbe.Check(!tip.Contains("Priority6", StringComparison.OrdinalIgnoreCase) && tip.Contains('6') &&
                               high.g > high.r && low.r > low.g, "Sixth-tier tooltip resolves and gradient endpoints are green/red");
            PlannerProbe.Check(!settings.TrySetRange(4,3,out _) && NativeWorkPriorities.Maximum == 6,
                "A live sixth tier prevents shrinking the range to four");
            ClickCell(pawn, work, 1);
            ClickCell(pawn, work, 0);
            PlannerProbe.Check(pawn.workSettings.GetPriority(work) == 6, "Work cell wraps through disabled and back to six");
            foreach (var person in pawns) person.workSettings.SetPriority(work, 5);
            Event.current = MouseDown(1, true);
            var headerClick = typeof(PawnColumnWorker_WorkPriority).GetMethod("HeaderClicked", BindingFlags.Instance | BindingFlags.NonPublic)!;
            headerClick.Invoke(column.Worker, [new Rect(0,0,40,60), table]);
            PlannerProbe.Check(pawns.All(p => p.workSettings.GetPriority(work)==6), "Shift-right-click adjusts the actual column to six");
            Event.current = MouseDown(0, true);
            headerClick.Invoke(column.Worker, [new Rect(0,0,40,60), table]);
            PlannerProbe.Check(pawns.All(p => p.workSettings.GetPriority(work)==5), "Shift-left-click adjusts the actual column to five");
            PlannerProbe.Check(settings.TrySetRange(99, 3, out _) && NativeWorkPriorities.Maximum == 99,
                "RimLazy can expand the normal table to ninety-nine tiers");
            pawn.workSettings.SetPriority(work, 98);
            ClickCell(pawn, work, 1);
            PlannerProbe.Check(pawn.workSettings.GetPriority(work)==99, "Normal work cell can select tier ninety-nine");
        }
        finally
        {
            Event.current = previousEvent;
            foreach (var old in before) old.Key.workSettings.SetPriority(work, old.Value);
            if (!settings.TrySetRange(6, 3, out string reason)) throw new InvalidOperationException(reason);
        }
    }

    private static void CheckSchedulerOrder(Pawn pawn, WorkTypeDef sixth)
    {
        var fifth = DefDatabase<WorkTypeDef>.AllDefsListForReading.First(w => w != sixth &&
            WorkSplitChecks.SplitIds.Contains(w.defName) && !pawn.WorkTypeIsDisabled(w) && w.workGiversByPriority.Any(g=>!g.emergency));
        var settings = pawn.workSettings;
        int saved = settings.GetPriority(fifth);
        try
        {
            settings.SetPriority(fifth, 5);
            var ordered = settings.WorkGiversInOrderNormal;
            int first = ordered.FindIndex(g => g.def.workType == fifth);
            int second = ordered.FindIndex(g => g.def.workType == sixth);
            PlannerProbe.Check(first >= 0 && second > first, "Game schedules fifth-tier split work before sixth-tier split work");
        }
        finally { settings.SetPriority(fifth, saved); }
    }

    private static Event MouseDown(int button, bool shift) => new()
    {
        type=EventType.MouseDown, button=button, shift=shift, mousePosition=new Vector2(-1989,-1989)
    };

    private static void ClickCell(Pawn pawn, WorkTypeDef work, int button)
    {
        Event.current = MouseDown(button, false);
        WidgetsWork.DrawWorkBoxFor(-2000, -2000, pawn, work, false);
    }
}
