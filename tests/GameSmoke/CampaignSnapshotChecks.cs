using System.Reflection;
using System.Text.Json;
using RimWorld;
using StrategicWork.Core;
using StrategicWork.RimWorld;
using Verse;

namespace RimLazy.GameSmoke;

internal static class CampaignSnapshotChecks
{
    internal static void Verify(Map map, ColonyWorkState state)
    {
        Find.TickManager.Pause();
        var tab=DefDatabase<MainButtonDef>.GetNamed("RimLazy_Planner");
        Find.MainTabsRoot.SetCurrentTab(tab, false);
        var window=(ColonyWorkWindow)tab.TabWindow;
        ColonyCapture Capture(bool rush) => ColonyAdapter.Capture(map,state.Overrides,state.LockedCells,state.PriorityOverride,rush);
        var initialTick=Find.TickManager.TicksGame;

        VerifyUnfinishedOrder(map, state, window);
        foreach (bool rush in new[] { false, true })
        {
            if(rush) window.SetRushFocus("survival");
            var snapshot=Capture(rush);
            var plan=window.PreviewAllocation(rush);
            var undo=window.ApplyPreview();
            var actual=Capture(rush);
            PlannerProbe.Check(actual.Inputs.Count>0 && plan.Cells.All(c=>actual.Inputs.Single(p=>p.Id==c.PawnId).Cells[c.WorkId].Current==c.After),
                $"Immediate {(rush?"rush":"routine")} preview writes the displayed matrix on campaign checkpoint");
            if(undo.Edits.Count>0) ColonyAdapter.Undo(map,undo,state.LockedCells);
            var restored=Capture(rush);
            PlannerProbe.Check(snapshot.Inputs.All(p=>p.Cells.All(c=>restored.Inputs.Single(q=>q.Id==p.Id).Cells[c.Key].Current==c.Value.Current)),
                "Undo returns every campaign work priority to its starting value");
        }

        var before=Capture(false);
        window.PreviewAllocation(false);
        var check=(from p in before.Inputs from cell in p.Cells
                   where cell.Value.Eligible && !cell.Value.Locked && cell.Value.Current>0
                   select (pawn:map.mapPawns.FreeColonistsSpawned.Single(x=>x.GetUniqueLoadID()==p.Id),
                           work:DefDatabase<WorkTypeDef>.GetNamed(cell.Key), original:cell.Value.Current)).First();
        check.pawn.workSettings.SetPriority(check.work,0);
        try
        {
            bool rejected=false;
            try {window.ApplyPreview();} catch(InvalidOperationException){rejected=true;}
            PlannerProbe.Check(rejected && check.pawn.workSettings.GetPriority(check.work)==0,
                "Manual edit after preview is still rejected and preserved on the campaign checkpoint");
        }
        finally {check.pawn.workSettings.SetPriority(check.work,check.original);}
        PlannerProbe.Check(initialTick==Find.TickManager.TicksGame,"All campaign preview and write checks ran on the same paused tick");
    }

    private static void VerifyUnfinishedOrder(Map map, ColonyWorkState state, ColonyWorkWindow window)
    {
        // The failing tick's checkpoint was written after 0.3.1 had released the bow bill's
        // binding. Its original creator, partially completed product and remaining work survive.
        var bill = map.listerBuildings.allBuildingsColonist.OfType<IBillGiver>()
            .SelectMany(t => t.BillStack.Bills.OfType<Bill_ProductionWithUft>())
            .Single(b => b.recipe.defName == "Make_Bow_Short");
        var bench = (Thing)bill.billStack.billGiver;
        var bow = map.listerThings.AllThings.OfType<UnfinishedThing>().Single(t => t.Recipe == bill.recipe);
        var creator = bow.Creator;
        var giver = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(g => g.fixedBillGiverDefs?.Contains(bench.def) == true);
        var work = giver.workType;
        var oldBinding = bill.BoundUft;
        PlannerProbe.Check(creator.Downed, "Checkpoint preserves the downed creator of the real unfinished bow");
        try
        {
            bill.SetBoundUft(bow);
            window.PreviewAllocation(false);
            bool afterPreview = ReferenceEquals(bill.BoundUft, bow);
            var preview = (ColonyCapture)typeof(ColonyWorkWindow).GetField("planCapture",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            var second = ColonyAdapter.Capture(map,state.Overrides,state.LockedCells,state.PriorityOverride);
            var diffs = preview.Columns.Zip(second.Columns).Where(x=>x.First!=x.Second)
                .Select(x => new {before=x.First,after=x.Second}).ToArray();
            string? failure = null;
            try { var undo = window.ApplyPreview(); if(undo.Edits.Count>0) ColonyAdapter.Undo(map,undo,state.LockedCells); }
            catch(InvalidOperationException error) { failure=error.Message; }
            File.WriteAllText(Path.Combine(GenFilePaths.SaveDataFolderPath,"replay-binding.json"),JsonSerializer.Serialize(new {
                savedBinding=oldBinding?.GetUniqueLoadID(), testedItem=bow.GetUniqueLoadID(), formerWorker=creator.GetUniqueLoadID(),
                remainingWork=bow.workLeft, work=work.defName, creatorDowned=creator.Downed,
                originalPriority=creator.workSettings.GetPriority(work), bench=bench.def.defName, boundAfterPreview=afterPreview, diffs, failure
            }));
            PlannerProbe.Check(afterPreview && ReferenceEquals(bill.BoundUft,bow),"Preview preserves the unfinished bow binding when its creator is downed");
            PlannerProbe.Check(diffs.Length==0 && failure==null,"Two same-tick captures of the interrupted order agree and its preview can be applied");
            var before=ColonyAdapter.Capture(map,state.Overrides,state.LockedCells,state.PriorityOverride);
            window.PreviewAllocation(false);
            bool suspended=bill.suspended;
            try
            {
                bill.suspended=true;
                bool rejected=false;
                try {window.ApplyPreview();} catch(InvalidOperationException) {rejected=true;}
                var after=ColonyAdapter.Capture(map,state.Overrides,state.LockedCells,state.PriorityOverride);
                PlannerProbe.Check(rejected && before.Inputs.Zip(after.Inputs).All(p=>p.First.Cells.All(c=>c.Value.Current==p.Second.Cells[c.Key].Current)),
                    "Suspending the actual bow order invalidates the preview without modifying the grid");
            }
            finally {bill.suspended=suspended;}
        }
        finally { bill.SetBoundUft(oldBinding); }
    }
}
