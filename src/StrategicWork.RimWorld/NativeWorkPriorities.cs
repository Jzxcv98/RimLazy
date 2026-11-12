using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace StrategicWork.RimWorld;

public static class NativeWorkPriorities
{
    private static readonly FieldInfo RawPriorities = AccessTools.Field(typeof(Pawn_WorkSettings), "priorities");
    private static int savedFloor;
    public static bool Active { get; private set; }
    public static int ConfiguredOrSavedMax => Math.Max(RimLazyMod.PrioritySettings.Maximum, savedFloor);
    public static int Maximum => Active ? PriorityDomain.NativeTableMaximum(ConfiguredOrSavedMax) : Pawn_WorkSettings.LowestPriority;
    public static int Default => Active ? Math.Min(RimLazyMod.PrioritySettings.DefaultPriority, Maximum) : Pawn_WorkSettings.DefaultPriority;
    internal static void UpdateSavedFloor(int value) => savedFloor = Math.Clamp(value, 0, NativePrioritySettings.Limit);

    internal static int HighestSavedPriority()
    {
        if (Current.Game?.World == null || Current.ProgramState != ProgramState.Playing) return 0;
        var highest = 0;
        foreach (var pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
        {
            if (pawn.Faction != Faction.OfPlayer || pawn.workSettings is not { Initialized: true } settings ||
                RawPriorities.GetValue(settings) is not DefMap<WorkTypeDef, int> values) continue;
            foreach (var work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                highest = Math.Max(highest,values[work]);
        }
        return highest;
    }

    internal static void Install()
    {
        if (ModsConfig.IsActive("lauriichan.prioritymaster") || PriorityDomain.UsesDetailedSchedules()) return;
        var harmony = new Harmony("rimlazy.strategicwork.priority");
        var patched = new List<MethodInfo>();
        try
        {
            void Transpile(Type type, string name, string patch)
            {
                var target = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
                patched.Add(target);
                harmony.Patch(target, transpiler: new HarmonyMethod(typeof(NativeWorkPriorities), patch));
            }
            Transpile(typeof(WidgetsWork), nameof(WidgetsWork.DrawWorkBoxFor), nameof(CellOrHeader));
            Transpile(typeof(PawnColumnWorker_WorkPriority), "HeaderClicked", nameof(CellOrHeader));
            Transpile(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority), nameof(SetterLimit));
            Transpile(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.GetPriority), nameof(SimpleModeDefault));
            Transpile(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.EnableAndInitialize), nameof(InitializationDefaults));
            Transpile(typeof(GameInitData), nameof(GameInitData.PrepForMapGen), nameof(InitializationDefaults));
            Transpile(typeof(WidgetsWork), nameof(WidgetsWork.TipForPawnWorker), nameof(TooltipPriority));
            var color = AccessTools.Method(typeof(WidgetsWork), nameof(WidgetsWork.ColorOfPriority));
            patched.Add(color);
            harmony.Patch(color, postfix: new HarmonyMethod(typeof(NativeWorkPriorities), nameof(PriorityColor)));
            Active = true;
        }
        catch (Exception ex)
        {
            foreach (var method in patched) harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
            Log.Error($"[RimLazy] Unable to enable extended priorities for this game build: {ex}");
        }
    }

    private static void PriorityColor(int prio, ref Color __result)
    {
        if (Active && prio > 0) __result = RimLazyMod.PrioritySettings.ColorAt(prio, Maximum);
    }

    // Change only the work box / shift-click thresholds. Other literal 4s in the same
    // methods are text alignment and tutorial constants and must keep their values.
    private static IEnumerable<CodeInstruction> CellOrHeader(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int bounds = 0, defaults = 0;
        for (int i = 0; i + 1 < code.Count; i++)
        {
            var current = code[i];
            var next = code[i + 1];
            if (current.opcode == OpCodes.Ldc_I4_4 &&
                (next.opcode.FlowControl == FlowControl.Cond_Branch || next.opcode.Name?.StartsWith("stloc", StringComparison.Ordinal) == true))
            {
                Call(current, nameof(Maximum), true);
                bounds++;
            }
            else if (ReplaceDefaultArgument(current, next)) defaults++;
        }
        if (bounds != 2 || defaults != 1) throw new InvalidOperationException("Work table input pattern changed.");
        return code;
    }

    private static IEnumerable<CodeInstruction> SetterLimit(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var target = code.Single(x => x.opcode == OpCodes.Ldc_I4_4);
        Call(target, nameof(Maximum), true);
        return code;
    }

    private static IEnumerable<CodeInstruction> SimpleModeDefault(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        Call(code.Single(x => x.opcode == OpCodes.Ldc_I4_3), nameof(Default), true);
        return code;
    }

    private static IEnumerable<CodeInstruction> InitializationDefaults(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var code = instructions.ToList();
        int count = 0;
        for (int i=0; i+1<code.Count; i++)
            if (ReplaceDefaultArgument(code[i], code[i+1])) count++;
        int expected = original.DeclaringType == typeof(GameInitData) ? 3 : 2;
        if (count != expected) throw new InvalidOperationException("Starting work defaults changed.");
        return code;
    }

    private static bool ReplaceDefaultArgument(CodeInstruction instruction, CodeInstruction next)
    {
        if (instruction.opcode != OpCodes.Ldc_I4_3 || next.operand is not MethodInfo method ||
            method.DeclaringType != typeof(Pawn_WorkSettings) || method.Name != nameof(Pawn_WorkSettings.SetPriority)) return false;
        Call(instruction, nameof(Default), true);
        return true;
    }

    private static IEnumerable<CodeInstruction> TooltipPriority(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int key = code.FindIndex(x => x.opcode == OpCodes.Ldstr && Equals(x.operand, "Priority"));
        if (key < 0) throw new InvalidOperationException("Priority tooltip key changed.");
        var translation = code.Skip(key + 1).Take(7).Single(x => x.operand is MethodInfo m && m.Name == "Translate" &&
            m.GetParameters().Length == 1 && m.ReturnType == typeof(TaggedString));
        Call(translation, nameof(TranslatePriority), false);
        return code;
    }

    public static TaggedString TranslatePriority(string key)
    {
        if (key.StartsWith("Priority",StringComparison.Ordinal) && int.TryParse(key.AsSpan(8),out var value) && value > 0 && Active)
            return "RimLazy_PriorityTooltip".Translate(value, Maximum);
        return key.Translate();
    }

    private static void Call(CodeInstruction instruction, string member, bool property)
    {
        instruction.opcode = OpCodes.Call;
        instruction.operand = property ? AccessTools.PropertyGetter(typeof(NativeWorkPriorities), member)
            : AccessTools.Method(typeof(NativeWorkPriorities), member);
    }
}

// A save may have been played under another profile with a wider range. Retain
// its actual values and expose them in the table even if this profile uses fewer ranks.
public sealed class SavedPriorityRange(Game game) : GameComponent
{
    public override void StartedNewGame() => NativeWorkPriorities.UpdateSavedFloor(0);
    public override void LoadedGame()
    {
        NativeWorkPriorities.UpdateSavedFloor(0);
        LongEventHandler.ExecuteWhenFinished(() =>
        {
            if (Current.Game == game && NativeWorkPriorities.Active)
                NativeWorkPriorities.UpdateSavedFloor(NativeWorkPriorities.HighestSavedPriority());
        });
    }
}
