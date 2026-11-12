using Microsoft.VisualStudio.TestTools.UnitTesting;
using StrategicWork.Core;

namespace StrategicWork.Core.Tests;

[TestClass]
public sealed class WorkPlannerTests
{
    private static StrategyPreset Policy(params StrategyAllocation[] goals) =>
        new(1, "test", goals, UnweightedRemainder.LeaveUnassigned);

    private static PawnInput Pawn(string id, params (string work, WorkCell cell)[] cells) =>
        new(id, cells.ToDictionary(x => x.work, x => x.cell));

    private static PlannedCell At(WorkPlan plan, string p, string w) =>
        plan.Cells.Single(x => x.PawnId == p && x.WorkId == w);

    [TestMethod]
    public void AssignsGeneralistToCoverTheJobThatTheSpecialistCannotDo()
    {
        WorkColumn[] works = [new("mine", "build"), new("study", "tech")];
        PawnInput[] people = [
            Pawn("expert", ("mine", new(0, true, 8)), ("study", new(0, false, 20))),
            Pawn("general", ("mine", new(0, true, 10)), ("study", new(0, true, 6)))
        ];
        var result = WorkPlanner.Plan(people, works, Policy(new("build", .5m, 0), new("tech", .5m, 0)), 6);
        Assert.AreEqual(1d, At(result, "expert", "mine").EstimatedLabor);
        Assert.AreEqual(1d, At(result, "general", "study").EstimatedLabor);
        Assert.AreEqual(0, At(result, "expert", "study").After);
        Assert.AreEqual(0d, result.UnassignedLabor);
    }

    [TestMethod]
    public void ResearchBenchLimitsTotalAndReservesTheBenchForTheBestResearcher()
    {
        WorkColumn[] works = [new("research", "tech", Capacity: 1), new("grow", "food")];
        PawnInput[] people = [.. Enumerable.Range(1, 3).Select(i => Pawn("p" + i,
            ("research", new(0, true, i)), ("grow", new(1, true, 1))))];
        var result = WorkPlanner.Plan(people, works, Policy(new("tech", 0, 10), new("food", 0, 0)), 6);
        var tech = result.Strategies.Single(s => s.Id == "tech");
        Assert.AreEqual(3d, tech.TargetLabor);
        Assert.AreEqual(1d, tech.EstimatedLabor);
        Assert.AreEqual(ShortfallReason.Capacity, tech.Reason);
        Assert.AreEqual(1, At(result, "p3", "research").After);
        Assert.IsTrue(result.Cells.Where(c => c.WorkId == "grow").All(c => c.After == 0));
        Assert.AreEqual(2d, result.UnassignedLabor);
    }

    [TestMethod]
    [DataRow(4)]
    [DataRow(6)]
    public void EmptyOrdersKeepOneEligibleStandbyAtLowestTierWithoutForecastingLabor(int tiers)
    {
        WorkColumn[] columns = [new("research", "tech", Capacity: 0), new("smith", "industry", Capacity: 0)];
        PawnInput[] pawns = [
            Pawn("best", ("research", new(0, true, 12)), ("smith", new(0, true, 7))),
            Pawn("backup", ("research", new(2, true, 5))),
            Pawn("forbidden", ("research", new(0, false, 20))),
            Pawn("locked", ("research", new(0, true, 20, true))),
            Pawn("downed", ("research", new(0, true, 30))) with { Labor = 0 }
        ];
        var plan = WorkPlanner.Plan(pawns, columns, Policy(new("tech", 0, 1), new("industry", 0, 0)), tiers);
        Assert.AreEqual(tiers, At(plan, "best", "research").After);
        Assert.AreEqual(1, plan.Cells.Count(c => c.After > 0));
        Assert.AreEqual(0d, plan.Cells.Sum(c => c.EstimatedLabor));
        Assert.AreEqual(4d, plan.UnassignedLabor);
        Assert.AreEqual(ShortfallReason.Capacity, plan.Strategies.Single(s => s.Id == "tech").Reason);
    }

    [TestMethod]
    public void ExistingLockedReserveAlreadyCoversAnEmptyQueue()
    {
        var reserve = Pawn("reserve", ("bill", new(3, true, 2, true)));
        var expert = Pawn("expert", ("bill", new(0, true, 19)));
        var plan = WorkPlanner.Plan([expert, reserve], [new("bill", "industry", Capacity: 0)],
            Policy(new StrategyAllocation("industry", 0, 1)), 6);
        Assert.AreEqual(0, At(plan, "expert", "bill").After);
        Assert.AreEqual(3, At(plan, "reserve", "bill").After);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    [DataRow(6)]
    [DataRow(12)]
    public void DiscoversAnyNumberOfColumnsAndKeepsOneLeadAcrossAvailableTiers(int maxPriority)
    {
        var works = Enumerable.Range(0, 31).Select(i => new WorkColumn("FSFWork" + i, "care", i)).ToArray();
        var person = Pawn("one", works.Select(w =>
            (w.Id, new WorkCell(0, true, w.Id == "FSFWork0" ? 100 : 1))).ToArray());
        var plan = WorkPlanner.Plan([person], works, Policy(new StrategyAllocation("care", 0, 1)), maxPriority);
        var tiers = plan.Cells.Select(c => c.After).Distinct().Order().ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(1, maxPriority).ToArray(), tiers);
        if (maxPriority > 1)
        {
            Assert.AreEqual(1, plan.Cells.Count(c => c.After == 1));
            Assert.AreEqual(1, At(plan, "one", "FSFWork0").After);
        }
        else Assert.AreEqual(1d, At(plan, "one", "FSFWork30").EstimatedLabor);
    }

    [TestMethod]
    public void LockedUnmappedIneligibleAndOutOfDomainCellsSurviveCalculation()
    {
        WorkColumn[] works = [new("locked", "a"), new("incapable", "a"), new("unmapped", null),
            new("newModSetting", "a"), new("newModJob", "a"), new("missing", "a")];
        var p = Pawn("p", ("locked", new(0, true, 1, true)), ("incapable", new(2, false, 10)),
            ("unmapped", new(6, true, 100)), ("newModSetting", new(9, true, 0)), ("newModJob", new(0, true, 10)));
        var plan = WorkPlanner.Plan([p], works, Policy(new StrategyAllocation("a", 0, 1)), 6);
        Assert.AreEqual(0, At(plan, "p", "locked").After);
        Assert.AreEqual(2, At(plan, "p", "incapable").After);
        Assert.AreEqual(6, At(plan, "p", "unmapped").After);
        Assert.AreEqual(9, At(plan, "p", "newModSetting").After);
        Assert.IsTrue(At(plan, "p", "newModJob").After > 0);
        Assert.IsFalse(plan.Cells.Any(c => c.WorkId == "missing"));
    }

    [TestMethod]
    public void LockedJobAndFiniteQueuesAffectTheEstimatedShares()
    {
        WorkColumn[] works = [new("fixed", "other", 100, .25), new("order", "prod", 1, .5), new("craft", "prod", 0)];
        var p = Pawn("p", ("fixed", new(1, true, 0, true)), ("order", new(0, true, 10)), ("craft", new(0, true, 1)));
        var result = WorkPlanner.Plan([p], works, Policy(new("other", 0, 0), new("prod", 0, 1)), 4);
        Assert.AreEqual(1, At(result, "p", "fixed").After);
        Assert.AreEqual(.25, At(result, "p", "fixed").EstimatedLabor, 1e-8);
        Assert.AreEqual(.5, At(result, "p", "order").EstimatedLabor, 1e-8);
        Assert.AreEqual(.25, At(result, "p", "craft").EstimatedLabor, 1e-8);
        Assert.AreEqual(ShortfallReason.PriorityOrder, result.Strategies.Single(s => s.Id == "prod").Reason);
    }

    [TestMethod]
    public void AllZeroPolicyDistinguishesPreservingExistingFromLeavingUnassigned()
    {
        var p = Pawn("p", ("work", new(6, true, 5)));
        var policy = Policy(new StrategyAllocation("a", 0, 0));
        var preserve = WorkPlanner.Plan([p], [new("work", "a")], policy with { UnweightedRemainder = UnweightedRemainder.PreserveExisting }, 6);
        var clear = WorkPlanner.Plan([p], [new("work", "a")], policy, 6);
        Assert.AreEqual(6, At(preserve, "p", "work").After);
        Assert.AreEqual(0, At(clear, "p", "work").After);
        Assert.AreEqual(1d, clear.UnassignedLabor);
    }

    [TestMethod]
    public void MinimumsAndWeightsUseDistinctBudgetsAndInvalidMinimumsAreRejected()
    {
        var policy = Policy(new("a", .25m, 1), new("b", .25m, 3));
        var report = WorkPlanner.Plan([Pawn("p")], [], policy, 6);
        Assert.AreEqual(.375, report.Strategies[0].TargetLabor, 1e-8);
        Assert.AreEqual(.625, report.Strategies[1].TargetLabor, 1e-8);
        Assert.AreEqual(ShortfallReason.NoMappedWork, report.Strategies[1].Reason);
        Assert.ThrowsException<ArgumentException>(() =>
            WorkPlanner.Plan([Pawn("p")], [], Policy(new("a", .8m, 0), new("b", .4m, 0)), 6));
    }

    [TestMethod]
    public void PermutationsAndRepeatRunsReturnTheSameAssignments()
    {
        var columns = Enumerable.Range(0, 9).Select(i => new WorkColumn("w" + i, "g" + (i % 3), i % 2)).ToArray();
        var pawns = Enumerable.Range(0, 7).Select(i => Pawn("p" + i,
            columns.Select((w, j) => (w.Id, new WorkCell(i % 3, (i + j) % 5 != 0, (i + j) % 4))).ToArray())).ToArray();
        var policy = Policy(new("g0", 0, 2), new("g1", .15m, 1), new("g2", 0, 2));
        var expected = WorkPlanner.Plan(pawns, columns, policy, 6);
        for (int i = 0; i < 100; i++)
        {
            var plan = WorkPlanner.Plan([.. pawns.Reverse()], [.. columns.Reverse()], policy, 6);
            CollectionAssert.AreEqual(expected.Cells.ToArray(), plan.Cells.ToArray());
        }
        Assert.AreEqual(7d, expected.Cells.Sum(c => c.EstimatedLabor) + expected.UnassignedLabor, 1e-7);
    }

    [TestMethod]
    public void RoundingManyModdedJobsKeepsFeasibleFiveWayLaborShares()
    {
        var rng = new Random(8172);
        var columns = Enumerable.Range(0, 80).Select(w => new WorkColumn($"w{w:D3}", "s" + w % 5, w)).ToArray();
        var pawns = Enumerable.Range(0, 100).Select(p => new PawnInput("p" + p,
            columns.ToDictionary(w => w.Id, w => new WorkCell(rng.Next(0, 7), rng.NextDouble() > .05, rng.Next(1, 20))))).ToArray();
        var policy = Policy(Enumerable.Range(0, 5).Select(s => new StrategyAllocation("s" + s, 0, 1)).ToArray());
        var plan = WorkPlanner.Plan(pawns, columns, policy, 6);
        foreach (var estimate in plan.Strategies)
            Assert.AreEqual(20d, estimate.EstimatedLabor, 1e-8, estimate.Id);
    }
}
