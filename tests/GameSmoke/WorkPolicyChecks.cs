using System.Reflection;
using RimWorld;
using StrategicWork.Core;
using StrategicWork.RimWorld;
using Verse;

namespace RimLazy.GameSmoke;

internal static class WorkPolicyChecks
{
    private static readonly FieldInfo InspirationSlot = typeof(InspirationHandler).GetField("curState",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
    private static readonly FieldInfo InspirationAge = typeof(Inspiration).GetField("age",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
    private static void Equal(float expected, float actual, string label)
    {
        if (Math.Abs(expected - actual) > Math.Max(0.00002f, Math.Abs(expected) * 0.001f))
            throw new InvalidOperationException($"{label}: expected {expected:G6}, got {actual:G6}");
        PlannerProbe.Check(true, label);
    }

    internal static void Verify(Map map)
    {
        var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.skills?.GetSkill(SkillDefOf.Cooking).TotallyDisabled != false)
            .First(p => !p.Downed);
        var handler = pawn.mindState.inspirationHandler;
        var previous = handler.CurState;
        StatEvaluation Snapshot(bool rush) => new(pawn, rush, GenDate.TicksPerDay);
        try
        {
            // The disposable fixture gets a known state independent of random pawn skills and event rolls.
            InspirationSlot.SetValue(handler, null);
            float movement = pawn.GetStatValue(StatDefOf.MoveSpeed);
            float trade = pawn.GetStatValue(StatDefOf.TradePriceImprovement);
            var move = new Inspiration { pawn = pawn, def = DefDatabase<InspirationDef>.GetNamed("Frenzy_Go") };
            InspirationSlot.SetValue(handler, move);
            float faster = pawn.GetStatValue(StatDefOf.MoveSpeed);
            PlannerProbe.Check(faster > movement, "Game applies Go frenzy to actual movement speed");
            Equal(faster, Snapshot(true).Value(StatDefOf.MoveSpeed), "Go frenzy enters current speed exactly once");
            InspirationAge.SetValue(move, (int)(move.def.baseDurationDays * GenDate.TicksPerDay) - GenDate.TicksPerDay / 2);
            Equal((movement + faster) / 2, Snapshot(true).Value(StatDefOf.MoveSpeed),
                "Half-day remaining frenzy only affects half the one-day forecast");
            var single = new Inspiration { pawn = pawn, def = DefDatabase<InspirationDef>.GetNamed("Inspired_Trade") };
            InspirationSlot.SetValue(handler, single);
            PlannerProbe.Check(pawn.GetStatValue(StatDefOf.TradePriceImprovement) > trade, "Single-use trading benefit is in the live stat");
            Equal(trade, Snapshot(true).Value(StatDefOf.TradePriceImprovement), "Planning strips the one-use benefit from a live stat");
            PlannerProbe.Check(ReferenceEquals(single,handler.CurState), "Preview leaves one-use inspiration unconsumed");
            InspirationSlot.SetValue(handler, null);
            var state = map.GetComponent<ColonyWorkState>();
            int[] Ranks()
            {
                var capture = ColonyAdapter.Capture(map, state.Overrides, state.LockedCells, state.PriorityOverride);
                return WorkPlanner.Plan(capture.Inputs,capture.Columns,state.Preset,capture.MaxPriority).Cells.Select(c=>c.After).ToArray();
            }
            var ranks = Ranks();
            var creative = new Inspiration { pawn = pawn, def = DefDatabase<InspirationDef>.GetNamed("Inspired_Creativity") };
            InspirationSlot.SetValue(handler, creative);
            var rng = typeof(Rand).GetProperty("StateCompressed", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            var randomBefore = rng.GetValue(null);
            PlannerProbe.Check(Ranks().SequenceEqual(ranks) && ReferenceEquals(handler.CurState,creative),
                "Creative inspiration neither changes the work matrix nor gets consumed by preview");
            PlannerProbe.Check(Equals(randomBefore,rng.GetValue(null)), "Forecast preserves the game's random state");
            InspirationSlot.SetValue(handler, null);
            VerifyGear(pawn, Snapshot);
        }
        finally { InspirationSlot.SetValue(handler, previous); }
    }

    private static void VerifyGear(Pawn pawn, Func<bool,StatEvaluation> snapshot)
    {
        var hat = (Apparel)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RimLazyQA_ChefHat"), ThingDefOf.Cloth);
        var old = pawn.apparel.WornApparel.Where(a => !ApparelUtility.CanWearTogether(a.def, hat.def, pawn.RaceProps.body)).ToList();
        var poison = DefDatabase<StatDef>.GetNamed("FoodPoisonChance");
        try
        {
            foreach (var item in old) pawn.apparel.Remove(item);
            var initial = snapshot(false);
            float baseSpeed = initial.Value(StatDefOf.MoveSpeed, pawn.Position);
            float baseRisk = initial.Value(poison, pawn.Position);
            float actualRisk = pawn.GetStatValue(poison);
            pawn.apparel.Wear(hat, false);
            var stable = snapshot(false);
            Equal(baseSpeed, stable.Value(StatDefOf.MoveSpeed, pawn.Position), "Movable gear leaves personal mobility stable");
            Equal(baseRisk, stable.Value(poison, pawn.Position), "Movable chef hat leaves personal kitchen risk stable");
            float dressed = pawn.GetStatValue(poison);
            PlannerProbe.Check(dressed < actualRisk, "Chef hat reduces actual game food poisoning chance");
            Equal(dressed, snapshot(true).Value(poison, pawn.Position), "Rush kitchen risk includes currently worn gear");
            PlannerProbe.Check(pawn.apparel.Wearing(hat), "Preview leaves actual clothing in place");
        }
        finally
        {
            if (pawn.apparel.Wearing(hat)) pawn.apparel.Remove(hat);
            foreach (var item in old) pawn.apparel.Wear(item, false);
            hat.Destroy();
        }
    }
}
