using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace StrategicWork.RimWorld;

// A private copy of the game's stat calculation is used exclusively for counterfactuals. Normal
// gameplay and the game's own stat cache never see these substitutions.
internal sealed class StatEvaluation
{
    private readonly Pawn pawn;
    private readonly bool rush;
    private readonly float activeFraction;
    private readonly bool oneShot;
    private readonly bool unfamiliarInspiration;
    private readonly bool indirectGear;
    private readonly HashSet<Thing> movable = [];
    private readonly Dictionary<(StatDef, Conditions), float> memo = [];
    private readonly Dictionary<(StatDef, Conditions), (bool Approximate, bool Gear)> diagnostics = [];
    private readonly Dictionary<StatDef, float> actual = [];
    private readonly Dictionary<StatDef, float> controls = [];
    [ThreadStatic] private static StatEvaluation? evaluating;
    private Conditions conditions;

    private readonly record struct Conditions(bool Personal, bool SuppressInspiration, IntVec3 Site, Thing? Tool, Thing? Removed = null);
    public bool Approximate { get; private set; }
    public bool EquipmentMatters { get; private set; }
    public void BeginAssessment() { Approximate = false; EquipmentMatters = false; }

    public StatEvaluation(Pawn pawn, bool rush, int horizonTicks)
    {
        this.pawn = pawn;
        this.rush = rush;
        if (pawn.apparel != null)
            foreach (var item in pawn.apparel.WornApparel)
                if (CanTransfer(pawn,item)) movable.Add(item);
        if (pawn.equipment?.Primary is { } weapon && weapon.TryGetComp<CompBiocodable>()?.CodedPawn != pawn)
            movable.Add(weapon);
        indirectGear = movable.Any(HasIndirectWearEffects);

        var inspiration = pawn.mindState?.inspirationHandler?.CurState;
        if (inspiration == null) return;
        oneShot = inspiration.def.defName is "Inspired_Creativity" or "Inspired_Surgery" or
            "Inspired_Taming" or "Inspired_Recruitment" or "Inspired_Trade";
        unfamiliarInspiration = !oneShot && inspiration.def.defName is not ("Frenzy_Go" or "Frenzy_Work" or "Frenzy_Shoot");
        activeFraction = Math.Clamp((inspiration.def.baseDurationDays * GenDate.TicksPerDay - inspiration.Age)
            / Math.Max(1, horizonTicks), 0f, 1f);
    }

    public float Value(StatDef stat) => Value(stat, IntVec3.Invalid);
    public float Value(StatDef stat, IntVec3 site) => OverHorizon(stat, !rush, site, null);
    public float EquippedEstimate(StatDef stat) => EquippedEstimate(stat, IntVec3.Invalid);
    public float EquippedEstimate(StatDef stat, IntVec3 site) => OverHorizon(stat, false, site, null);
    public float Current(StatDef stat) => Evaluate(stat, new(false, oneShot, IntVec3.Invalid, null));
    internal float WithAdditionalTool(StatDef stat, Thing tool) => OverHorizon(stat, false, IntVec3.Invalid, tool);
    internal float WithoutTool(StatDef stat, Thing tool) => OverHorizon(stat, false, IntVec3.Invalid, null, tool);

    internal static bool CanTransfer(Pawn wearer, Apparel apparel) =>
        wearer.outfits?.forcedHandler?.IsForced(apparel) != true && !wearer.apparel.IsLocked(apparel) &&
        apparel.TryGetComp<CompBiocodable>()?.CodedPawn == null;

    internal static bool HasIndirectWearEffects(Thing item) =>
        item.def.modExtensions?.Any(ext => ext.GetType().Name.Contains("ApparelExtension", StringComparison.Ordinal)) == true ||
        item.def.comps?.Any(comp => comp.GetType().Name.Contains("Hediff", StringComparison.OrdinalIgnoreCase)) == true;

    private float OverHorizon(StatDef stat, bool personal, IntVec3 site, Thing? tool, Thing? removed = null)
    {
        var now = new Conditions(personal, oneShot, site, tool, removed);
        float current = Evaluate(stat, now);
        if (oneShot || !pawn.Inspired || activeFraction >= 1) return current;
        float after = Evaluate(stat, now with { SuppressInspiration = true });
        return after + activeFraction * (current - after);
    }

    private float Evaluate(StatDef stat, Conditions query)
    {
        var key = (stat, query);
        if (memo.TryGetValue(key, out float cached))
        {
            if (diagnostics.TryGetValue(key, out var diag))
            { Approximate |= diag.Approximate; EquipmentMatters |= diag.Gear; }
            return cached;
        }
        bool oldApproximate = Approximate, oldGear = EquipmentMatters;
        BeginAssessment();
        try
        {
            float value = Compute(stat, query);
            diagnostics[key] = (Approximate, EquipmentMatters);
            return memo[key] = value;
        }
        finally { Approximate |= oldApproximate; EquipmentMatters |= oldGear; }
    }

    private float Compute(StatDef stat, Conditions query)
    {
        if ((query.Personal && indirectGear) ||
            (unfamiliarInspiration && pawn.InspirationDef is { } inspiration &&
             (inspiration.statOffsets.GetStatOffsetFromList(stat) != 0 || inspiration.statFactors.GetStatFactorFromList(stat) != 1)))
            Approximate = true;
        if (!actual.TryGetValue(stat, out float live)) actual[stat] = live = pawn.GetStatValue(stat);
        if (!float.IsFinite(live)) { Approximate = true; return 0; }
        if (!query.Personal && !query.SuppressInspiration && !query.Site.IsValid && query.Tool == null && query.Removed == null) return live;
        var real = new Conditions(false, false, IntVec3.Invalid, null);
        if (!Projection.Ready || !Projection.Supports(stat.Worker.GetType()))
        {
            Approximate = true;
            // A one-use value that cannot be separated cannot be used as an advantage.
            return HasDirectOneShot(stat) ? stat.defaultBaseValue : live;
        }
        var previous = evaluating;
        var previousConditions = conditions;
        evaluating = this;
        // Break possible cycles in custom stat dependencies with the real game value.
        memo[(stat, query)] = live;
        try
        {
            if (!controls.TryGetValue(stat, out float control))
            {
                conditions = real;
                control = Calculate(stat);
                controls[stat] = control;
            }
            if (Math.Abs(control - live) > 0.0001f * Math.Max(1, Math.Abs(live)))
            {
                Approximate = true;
                return memo[(stat, query)] = HasDirectOneShot(stat) ? stat.defaultBaseValue : live;
            }
            conditions = query;
            float estimate = Calculate(stat);
            if (!float.IsFinite(estimate)) { Approximate = true; return live; }
            return memo[(stat, query)] = estimate;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            Approximate = true;
            Log.WarningOnce($"[RimLazy] Using the live value for unsupported projection {stat.defName}: {ex.Message}",
                Gen.HashCombineInt(8391651, stat.shortHash));
            return HasDirectOneShot(stat) ? stat.defaultBaseValue : live;
        }
        finally { evaluating = previous; conditions = previousConditions; }
    }

    private bool HasDirectOneShot(StatDef stat) => oneShot && pawn.InspirationDef is { } def &&
        (def.statOffsets.GetStatOffsetFromList(stat) != 0 || def.statFactors.GetStatFactorFromList(stat) != 1);

    private float Calculate(StatDef stat)
    {
        var request = StatRequest.For(pawn);
        float value = Projection.Raw(stat.Worker, request, true);
        Projection.Finish(stat.Worker, request, ref value, true);
        return value;
    }

    private static class Projection
    {
        private static readonly Dictionary<Type, bool> Workers = [];
        private static readonly MethodInfo GearMethod = AccessTools.Method(typeof(StatWorker), nameof(StatWorker.StatOffsetFromGear));
        private static readonly MethodInfo InspiredGetter = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Inspired));
        private static readonly MethodInfo StageGetter = AccessTools.PropertyGetter(typeof(Hediff), nameof(Hediff.CurStage));
        private static readonly MethodInfo ReadStat = AccessTools.Method(typeof(StatExtension), nameof(StatExtension.GetStatValue),
            [typeof(Thing), typeof(StatDef), typeof(bool), typeof(int)]);
        private static readonly MethodInfo Transform = AccessTools.Method(typeof(StatPart), nameof(StatPart.TransformValue));
        private static readonly MethodInfo BaseValue = AccessTools.Method(typeof(StatWorker), "GetBaseValueFor", [typeof(StatRequest)]);
        private static readonly AccessTools.FieldRef<StatWorker, StatDef> WorkerStat = AccessTools.FieldRefAccess<StatWorker, StatDef>("stat");
        private static readonly AccessTools.FieldRef<StatPart_Glow, SimpleCurve> GlowCurve = AccessTools.FieldRefAccess<StatPart_Glow, SimpleCurve>("factorFromGlowCurve");
        private static readonly Func<StatPart_Glow, Thing, bool> GlowActive = AccessTools.MethodDelegate<Func<StatPart_Glow, Thing, bool>>(AccessTools.Method(typeof(StatPart_Glow), "ActiveFor"));
        private static readonly Lazy<HashSet<HediffDef>> AuraDefs = new(FindAuraDefs);
        internal static readonly bool Ready;

        static Projection()
        {
            try
            {
                var transpiler = AccessTools.Method(typeof(Projection), nameof(Substitute));
                Harmony.ReversePatch(AccessTools.Method(typeof(StatWorker), nameof(StatWorker.GetValueUnfinalized)),
                    new HarmonyMethod(typeof(Projection), nameof(Raw)), transpiler);
                Harmony.ReversePatch(AccessTools.Method(typeof(StatWorker), nameof(StatWorker.FinalizeValue)),
                    new HarmonyMethod(typeof(Projection), nameof(Finish)), transpiler);
                Ready = true;
            }
            catch (Exception ex) { Log.Warning($"[RimLazy] Stat projections unavailable on this build: {ex.Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float Raw(StatWorker worker, StatRequest request, bool post) => throw new NotSupportedException();
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Finish(StatWorker worker, StatRequest request, ref float val, bool post) => throw new NotSupportedException();

        internal static bool Supports(Type worker)
        {
            if (!Workers.TryGetValue(worker, out bool supported))
                Workers[worker] = supported = worker.GetMethod(nameof(StatWorker.GetValueUnfinalized))?.DeclaringType == typeof(StatWorker) &&
                    worker.GetMethod(nameof(StatWorker.FinalizeValue))?.DeclaringType == typeof(StatWorker);
            return supported;
        }

        private static IEnumerable<CodeInstruction> Substitute(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var op in instructions)
            {
                string? replace = op.Calls(GearMethod) ? nameof(Gear) : op.Calls(InspiredGetter) ? nameof(Inspired) :
                    op.Calls(ReadStat) ? nameof(Dependency) : op.Calls(StageGetter) ? nameof(Stage) :
                    op.Calls(Transform) ? nameof(Part) : null;
                if (replace != null)
                {
                    op.opcode = OpCodes.Call;
                    op.operand = AccessTools.Method(typeof(Projection), replace);
                }
                yield return op;
                // Gear offsets enter after the base value but before all skill/health multipliers.
                // The additional tool is a scenario only, never inserted into the pawn's trackers.
                if (op.Calls(BaseValue))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Projection), nameof(AdditionalGear)));
                }
            }
        }

        private static float AdditionalGear(float value, StatWorker worker) => evaluating!.conditions.Tool is { } item
            ? value + StatWorker.StatOffsetFromGear(item, WorkerStat(worker)) : value;
        private static float Gear(Thing item, StatDef stat)
        {
            float offset = StatWorker.StatOffsetFromGear(item, stat);
            var e = evaluating!;
            if (offset != 0 && e.movable.Contains(item))
            {
                e.EquipmentMatters = true;
                if (e.conditions.Personal || item == e.conditions.Removed) return 0;
            }
            return offset;
        }
        private static bool Inspired(Pawn pawn) => !evaluating!.conditions.SuppressInspiration && pawn.Inspired;
        private static float Dependency(Thing thing, StatDef stat, bool post, int stale)
        {
            var e = evaluating!;
            return thing == e.pawn && post ? e.Evaluate(stat, e.conditions) : thing.GetStatValue(stat, post, stale);
        }
        private static HediffStage? Stage(Hediff effect)
        {
            var stage = effect.CurStage;
            if (stage == null || !evaluating!.conditions.Personal || !AuraDefs.Value.Contains(effect.def)) return stage;
            if (stage.capMods is { Count: > 0 }) evaluating.Approximate = true; // live capacities may still reflect the aura
            return null;
        }
        private static void Part(StatPart part, StatRequest request, ref float value)
        {
            var e = evaluating!;
            if (part.GetType() == typeof(StatPart_Glow) && request.Thing == e.pawn)
            {
                var glow = (StatPart_Glow)part;
                if (e.conditions.Site.IsValid && e.pawn.Spawned && e.conditions.Site.InBounds(e.pawn.Map))
                {
                    if (GlowActive(glow, e.pawn))
                        value *= GlowCurve(glow).Evaluate(e.pawn.Map.glowGrid.GroundGlowAt(e.conditions.Site));
                    return;
                }
                if (e.conditions.Personal) return; // common, fully lit reference site
            }
            part.TransformValue(request, ref value);
        }
        private static HashSet<HediffDef> FindAuraDefs()
        {
            var result = new HashSet<HediffDef>();
            foreach (var def in DefDatabase<HediffDef>.AllDefsListForReading)
                if (def.comps != null)
                    foreach (var comp in def.comps.OfType<HediffCompProperties_GiveHediffsInRange>())
                        if (comp.hediff != null) result.Add(comp.hediff);
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
                if (def.building != null && def.comps != null)
                    foreach (var comp in def.comps)
                        if (comp.GetType().Name.Contains("GiveHediffInRange", StringComparison.OrdinalIgnoreCase) &&
                            AccessTools.Field(comp.GetType(), "hediff")?.GetValue(comp) is HediffDef effect) result.Add(effect);
            return result;
        }
    }
}
