using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace StrategicWork.RimWorld;

public sealed class NativePrioritySettings : ModSettings
{
    public const int Limit = 99;
    internal const string Green = "#00FF00";
    internal const string Red = "#FF0000";
    private int maximum = 6;
    private int defaultPriority = 3;
    private bool gradient = true;
    private List<string> palette = [Green, Red];
    private Color[] colors = [];
    internal bool WasLoaded { get; private set; }
    public int Maximum => maximum;
    public int DefaultPriority => defaultPriority;
    public bool Gradient => gradient;
    public IReadOnlyList<string> Palette => palette;

    public bool TrySetRange(int max, int defaultValue, out string reason)
    {
        if (max is < 1 or > Limit || defaultValue < 1 || defaultValue > max)
        {
            reason = "RimLazy_InvalidPrioritySettings".Translate();
            return false;
        }
        if (max < NativeWorkPriorities.ConfiguredOrSavedMax)
        {
            var occupied = NativeWorkPriorities.HighestSavedPriority();
            if (occupied > max)
            {
                reason = "RimLazy_PriorityInUse".Translate(occupied);
                return false;
            }
        }
        maximum = max;
        defaultPriority = defaultValue;
        NativeWorkPriorities.UpdateSavedFloor(0);
        reason = "";
        return true;
    }

    public bool SetPalette(bool useGradient, IEnumerable<string> hexColors)
    {
        var values = hexColors.Select(s => s.Trim().ToUpperInvariant()).ToList();
        if (values.Count == 0 || values.Count > Limit || values.Any(s => !TryReadColor(s, out _))) return false;
        gradient = useGradient;
        palette = values;
        RefreshColors();
        return true;
    }

    public Color ColorAt(int priority, int max)
    {
        if (priority < 1) return Color.gray;
        if (colors.Length == 0) RefreshColors();
        if (!gradient) return colors[Math.Min(priority, colors.Length)-1];
        float index = Math.Clamp((priority - 1f) / Math.Max(1, max - 1), 0, 1) * (colors.Length - 1);
        int low = (int)index;
        return Color.Lerp(colors[low], colors[Math.Min(low + 1, colors.Length - 1)], index - low);
    }

    internal static bool TryReadColor(string? hex, out Color color)
    {
        color = Color.white;
        if (hex?.Length != 7 || hex[0] != '#' ||
            !int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
        return true;
    }

    private void RefreshColors() => colors = palette.Select(s => TryReadColor(s, out var c) ? c : Color.white).ToArray();

    public override void ExposeData()
    {
        Scribe_Values.Look(ref maximum, "maximumPriority", 6);
        Scribe_Values.Look(ref defaultPriority, "defaultPriority", 3);
        Scribe_Values.Look(ref gradient, "gradient", true);
        Scribe_Collections.Look(ref palette, "palette", LookMode.Value);
        if (Scribe.mode != LoadSaveMode.LoadingVars) return;
        WasLoaded = true;
        maximum = Math.Clamp(maximum, 1, Limit);
        defaultPriority = Math.Clamp(defaultPriority, 1, maximum);
        if (palette == null || palette.Count == 0 || palette.Any(s => !TryReadColor(s, out _))) palette = [Green, Red];
        RefreshColors();
    }

    // Import the player's existing configuration on first use, including custom per-priority colors.
    internal bool ImportLegacySettings()
    {
        if (WasLoaded) return false;
        try
        {
            string? path = Directory.EnumerateFiles(GenFilePaths.ConfigFolderPath, "Mod_*_PriorityMaster.xml")
                .Order(StringComparer.Ordinal).FirstOrDefault();
            if (path == null) return false;
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 100_000 });
            var data = XDocument.Load(reader).Root?.Element("ModSettings");
            if (data == null) return false;
            if (int.TryParse(data.Element("HighestPriority")?.Value, out var cap)) maximum = Math.Clamp(cap, 1, Limit);
            if (int.TryParse(data.Element("DefaultPriority")?.Value, out var initial)) defaultPriority = Math.Clamp(initial, 1, maximum);
            else defaultPriority = Math.Min(defaultPriority, maximum);
            bool useGradient = !bool.TryParse(data.Element("Gradient")?.Value, out var savedGradient) || savedGradient;
            if (data.Element("HexColors") is { } list) SetPalette(useGradient, list.Elements("li").Select(li => li.Value));
            return true;
        }
        catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException)
        {
            Log.Warning($"[RimLazy] Could not import PriorityMaster settings: {e.Message}");
            return false;
        }
    }
}
