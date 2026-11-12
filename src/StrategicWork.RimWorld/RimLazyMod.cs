using System.Globalization;
using UnityEngine;
using Verse;

namespace StrategicWork.RimWorld;

public sealed class RimLazyMod : Mod
{
    public static NativePrioritySettings PrioritySettings { get; private set; } = new();
    private string? maxText;
    private string defaultText = "", paletteText = "", notice = "";
    private bool gradientDraft;

    public RimLazyMod(ModContentPack content) : base(content)
    {
        PrioritySettings = GetSettings<NativePrioritySettings>();
        if (PrioritySettings.ImportLegacySettings()) WriteSettings();
    }

    public override string SettingsCategory() => "RimLazy";

    public override void DoSettingsWindowContents(Rect area)
    {
        if (maxText == null)
        {
            maxText = PrioritySettings.Maximum.ToString();
            defaultText = PrioritySettings.DefaultPriority.ToString();
            gradientDraft = PrioritySettings.Gradient;
            paletteText = string.Join(", ",PrioritySettings.Palette);
        }
        var listing = new Listing_Standard();
        listing.Begin(area);
        maxText = Field(listing,"RimLazy_MaxPriority",maxText);
        defaultText = Field(listing,"RimLazy_DefaultPriority",defaultText);
        listing.CheckboxLabeled("RimLazy_PriorityGradient".Translate(), ref gradientDraft);
        listing.Label((gradientDraft ? "RimLazy_GradientStops" : "RimLazy_IndividualColors").Translate());
        paletteText = Widgets.TextArea(listing.GetRect(50), paletteText);
        var sample = listing.GetRect(30);
        var displayCount = Math.Min(16, PrioritySettings.Maximum);
        for (int i = 0; i < displayCount; i++)
        {
            int rank = 1 + (int)Math.Round(i * (PrioritySettings.Maximum - 1d) / Math.Max(1, displayCount - 1));
            var swatch = new Rect(sample.x + i * sample.width / displayCount, sample.y + 3, sample.width / displayCount - 2, 18);
            Widgets.DrawBoxSolid(swatch, PrioritySettings.ColorAt(rank, PrioritySettings.Maximum));
            TooltipHandler.TipRegion(swatch, "RimLazy_PriorityTooltip".Translate(rank,PrioritySettings.Maximum));
        }
        if (listing.ButtonText("RimLazy_SaveWorkSettings".Translate()))
        {
            var stops = paletteText.Split([',',' ','\n','\r','\t',';','，'],StringSplitOptions.RemoveEmptyEntries);
            if (stops.Length is 0 or > NativePrioritySettings.Limit || stops.Any(s => !NativePrioritySettings.TryReadColor(s, out _)))
                notice = "RimLazy_InvalidColors".Translate();
            else if (!int.TryParse(maxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int max) ||
                     !int.TryParse(defaultText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int initial))
                notice = "RimLazy_InvalidPrioritySettings".Translate();
            else if (PrioritySettings.TrySetRange(max, initial, out notice))
            {
                PrioritySettings.SetPalette(gradientDraft, stops);
                WriteSettings();
                notice = "RimLazy_WorkSettingsSaved".Translate();
            }
        }
        if (!string.IsNullOrEmpty(notice)) listing.Label(notice);
        listing.Gap();
        if (listing.ButtonText("RimLazy_JobSettingsButton".Translate())) NativeJobSettings.Open();
        listing.End();
    }

    private static string Field(Listing_Standard listing, string key, string value)
    {
        var row = listing.GetRect(32);
        Widgets.Label(new Rect(row.x, row.y + 3, row.width - 110, row.height),key.Translate());
        return Widgets.TextField(new Rect(row.xMax - 100, row.y, 100, 28),value);
    }
}

[StaticConstructorOnStartup]
internal static class PriorityBootstrap
{
    static PriorityBootstrap() => NativeWorkPriorities.Install();
}
