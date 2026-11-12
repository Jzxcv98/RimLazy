using System.Xml.Linq;
using UnityEngine;
using Verse;

namespace StrategicWork.RimWorld;

public static class NativeJobSettings
{
    internal static readonly string[] Columns = [
        "FSFTaming", "FSFSlaughter", "FSFConvert", "FSFPrisonCare", "FSFHack", "FSFTeach",
        "FSFSelfTend", "FSFRescue", "FSFPollution", "FSFSnow", "FSFMerge", "FSFPaint",
        "FSFMechanoids", "FSFPruning", "FSFSmoothing"
    ];
    internal static readonly string[] Options = [
        "FSFAnomalyTasksInDarkStudy", "FSFDraftedWorkTasks", "FSFIdeoRolesAreCapable",
        "FSFMechanoidsAreCapable", "FSFRescueInFirefighting", "FSFSlavesAreCapable", "FSFWarcasketsAreCapable"
    ];
    private static string FileName => Path.Combine(GenFilePaths.ConfigFolderPath, "RimLazy.DetailSettings.xml");
    private const string LegacyPrefix = "FrozenSnowFox.FrozenSnowFoxComplexJobs;";

    internal static HashSet<string> Read()
    {
        try
        {
            if (!File.Exists(FileName)) return ReadLegacy();
            return XDocument.Load(FileName).Root?.Elements("enabled").Select(e => e.Value)
                .ToHashSet(StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            Log.Warning("[RimLazy] Cannot load detailed work options: " + ex.Message);
            return new(StringComparer.Ordinal);
        }
    }

    private static HashSet<string> ReadLegacy()
    {
        var options = new HashSet<string>(StringComparer.Ordinal);
        // XML Extensions persisted a set of shared key/value arrays for every mod using its settings page.
        // Missing settings, including the 15 optional work columns, default to false.
        var path = Path.Combine(GenFilePaths.ConfigFolderPath, "Mod_2574315206_XmlMod.xml");
        if (!File.Exists(path)) return options;
        var dictionary = XDocument.Load(path).Descendants("dataDict").FirstOrDefault();
        var keys = dictionary?.Element("keys")?.Elements("li").ToList();
        var values = dictionary?.Element("values")?.Elements("li").ToList();
        if (keys == null || values == null || keys.Count != values.Count) return options;
        var found = false;
        for (int i = 0; i < keys.Count; i++)
        {
            var name = keys[i].Value;
            if (!name.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var key = name[LegacyPrefix.Length..];
            if (key.EndsWith("Worktype", StringComparison.Ordinal))
                key = key[..^"Worktype".Length];
            key = key switch { "FSFClearSnow" => "FSFSnow", "FSFClearPollution" => "FSFPollution", _ => key };
            if ((!Columns.Contains(key) && !Options.Contains(key)) || !bool.TryParse(values[i].Value, out bool on)) continue;
            found = true;
            if (on) options.Add(key); else options.Remove(key);
        }
        if (found) Save(options);
        return options;
    }

    private static void Save(IEnumerable<string> enabled)
    {
        var path = FileName;
        var tmp = path + ".tmp";
        new XDocument(new XElement("RimLazy", enabled.Order(StringComparer.Ordinal)
            .Select(id => new XElement("enabled", id)))).Save(tmp);
        File.Move(tmp, path, true);
    }
    public static void Open() => Find.WindowStack.Add(new DetailOptionsWindow());

    private sealed class DetailOptionsWindow : Window
    {
        private readonly HashSet<string> enabled = Read();
        private Vector2 scroll;
        private float contentHeight = 850;
        private bool dirty;
        public DetailOptionsWindow() { doCloseButton = true; doCloseX = true; absorbInputAroundWindow = true; }
        public override Vector2 InitialSize => new(Math.Min(UI.screenWidth, 620), Math.Min(UI.screenHeight, 650));
        public override void DoWindowContents(Rect rect)
        {
            var outer = new Rect(rect.x, rect.y, rect.width, rect.height - 48);
            var inner = new Rect(0, 0, Math.Max(30, outer.width - 20), contentHeight);
            Widgets.BeginScrollView(outer, ref scroll, inner);
            var list = new Listing_Standard();
            list.Begin(inner);
            list.Label("RimLazy_Detail_Hint".Translate());
            DrawOptions(list, "Columns", Columns);
            DrawOptions(list, "Rules", Options);
            contentHeight = list.CurHeight + 12;
            list.End();
            Widgets.EndScrollView();
        }
        private void DrawOptions(Listing_Standard list, string title, IEnumerable<string> names)
        {
            list.GapLine();
            list.Label(("RimLazy_Detail_" + title).Translate());
            foreach (var key in names)
            {
                var before = enabled.Contains(key);
                var value = before;
                list.CheckboxLabeled(("RimLazy_Detail_" + key).Translate(), ref value);
                if (before == value) continue;
                if (value) enabled.Add(key); else enabled.Remove(key);
                dirty = true;
            }
        }
        public override void PostClose()
        {
            base.PostClose();
            if (!dirty) return;
            Save(enabled);
            Messages.Message("RimLazy_Detail_Saved".Translate(), global::RimWorld.MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
