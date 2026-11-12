using Microsoft.VisualStudio.TestTools.UnitTesting;
using StrategicWork.Core;

namespace StrategicWork.Core.Tests;

[TestClass]
public sealed class TrainingPolicyTests
{
    private static StrategyPreset Policy(decimal loss, params string[] strategies) => new(2, "test",
        strategies.Select(id => new StrategyAllocation(id, 0, 1)).ToArray(),
        UnweightedRemainder.LeaveUnassigned, loss);

    private static PawnInput Pawn(string id, params (string Work, WorkCell Cell)[] jobs) =>
        new(id, jobs.ToDictionary(j => j.Work, j => j.Cell));

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(5, false)]
    [DataRow(8, true)]
    [DataRow(10, true)]
    public void ApprenticeMustBeWithinTheActualPerformanceBudget(int percent, bool useApprentice)
    {
        var master = Pawn("master", ("craft", new(1, true, 100, TrainingGain: 0)));
        var apprentice = Pawn("apprentice", ("craft", new(0, true, 92, TrainingGain: 5)));
        var plan = WorkPlanner.Plan([master, apprentice], [new("craft", "industry", Capacity: 1)],
            Policy(percent / 100m, "industry"), 6);
        Assert.AreEqual(useApprentice, plan.TrainingApplied);
        var selected = plan.Cells.Single(c => c.EstimatedLabor > 0);
        Assert.AreEqual(useApprentice ? "apprentice" : "master", selected.PawnId);
        var report = plan.Strategies.Single();
        Assert.IsTrue(report.PerformanceComparable);
        Assert.AreEqual(100d, report.ReferencePerformance, 1e-6);
        Assert.AreEqual(useApprentice ? 92d : 100d, report.PlannedPerformance, 1e-6);
        Assert.AreEqual(useApprentice ? 5d : 0d, report.PlannedTraining);
        Assert.AreEqual(0d, report.ReferenceTraining);
        Assert.AreEqual("master", plan.Cells.Single(c => c.ReferenceLabor > 0).PawnId);
    }

    [TestMethod]
    public void RushUsesTheZeroTrainingMatrixWithoutChangingThePreset()
    {
        var preset = Policy(0.25m, "research");
        var pawns = new[] { Pawn("studied", ("bench", new(3, true, 100))), Pawn("junior", ("bench", new(3, true, 90, TrainingGain: 9))) };
        WorkColumn[] jobs = [new("bench", "research", Capacity: 1)];
        var rush = WorkPlanner.Plan(pawns, jobs, preset, 6, rush: true);
        var zero = WorkPlanner.Plan(pawns, jobs, preset with { TrainingLossLimit = 0 }, 6);
        CollectionAssert.AreEqual(zero.Cells.ToArray(), rush.Cells.ToArray());
        Assert.IsFalse(rush.TrainingApplied);
        Assert.AreEqual(.25m, preset.TrainingLossLimit);
    }

    [TestMethod]
    public void SurplusInOneDirectionCannotPayForTooMuchLossInAnother()
    {
        // A tempting exchange doubles industrial output but loses 20% of the research benchmark.
        var first = Pawn("first", ("research", new(1, true, 10)), ("industry", new(0, true, 2, TrainingGain: 50)));
        var second = Pawn("second", ("research", new(0, true, 8, TrainingGain: 50)), ("industry", new(1, true, 1)));
        var plan = WorkPlanner.Plan([first, second],
            [new("research", "tech", Capacity: 1), new("industry", "prod", Capacity: 1)], Policy(.1m, "tech", "prod"), 6);
        Assert.IsFalse(plan.TrainingApplied);
        Assert.AreEqual("first", plan.Cells.Single(c => c.WorkId == "research" && c.EstimatedLabor > 0).PawnId);
    }

    [TestMethod]
    public void AFiniteOrderCanTrainSlowerPawnWithoutLosingDeliverableOutput()
    {
        var plan = WorkPlanner.Plan([Pawn("expert", ("craft", new(1, true, 100))),
            Pawn("student", ("craft", new(0, true, 92, TrainingGain: 1)))],
            [new("craft", "production", Capacity: 1, OutputDemand: 50)], Policy(.01m, "production"), 6);
        var report = plan.Strategies.Single();
        Assert.IsTrue(plan.TrainingApplied);
        Assert.AreEqual(100d, report.PlannedPerformance, 1e-6);
        var student = plan.Cells.Single(c => c.PawnId == "student");
        Assert.AreEqual(50d / 92d, student.EstimatedLabor, 1e-7);
        Assert.AreEqual(student.EstimatedLabor, report.PlannedTraining, 1e-7);
    }

    [TestMethod]
    public void UncertainOutputDisabledWorkersAndLocksCannotPurchaseTraining()
    {
        var plan = WorkPlanner.Plan([Pawn("expert", ("care", new(1, true, 10))),
            Pawn("unknown", ("care", new(0, true, 9, TrainingGain: 300, PerformanceComparable: false))),
            Pawn("forbidden", ("care", new(0, false, 10, TrainingGain: 500))),
            Pawn("protected", ("care", new(0, true, 10, true, 500)))],
            [new("care", "survival", Capacity: 1)], Policy(1, "survival"), 6);
        Assert.IsFalse(plan.TrainingApplied);
        Assert.AreEqual("expert", plan.Cells.Single(c => c.EstimatedLabor > 0).PawnId);
    }

    [TestMethod]
    public void AnUnknownActiveColumnIsNotAdvertisedAsComparable()
    {
        var plan = WorkPlanner.Plan([Pawn("modded", ("craft", new(1, true, 1, TrainingGain: 10, PerformanceComparable: false)))],
            [new("craft", "prod")], Policy(1, "prod"), 6);
        Assert.IsFalse(plan.Strategies.Single().PerformanceComparable);
        Assert.IsFalse(plan.TrainingApplied);
    }

    [TestMethod]
    public void TrainingCanChangeKnownWorkWhileKeepingUnknownWorkAndItsHoursExactlyFixed()
    {
        PawnInput[] pawns = [
            Pawn("expert", ("bench", new(0, true, 100))),
            Pawn("trainee", ("bench", new(0, true, 92, TrainingGain: 5))),
            Pawn("specialist", ("modTask", new(1, true, 1, PerformanceComparable: false)))
        ];
        var plan = WorkPlanner.Plan(pawns, [new("bench", "prod", Capacity: 1), new("modTask", "prod", Capacity: 1)],
            Policy(.1m, "prod"), 6);
        Assert.IsTrue(plan.TrainingApplied);
        Assert.IsFalse(plan.Strategies.Single().PerformanceComparable);
        Assert.AreEqual(1d, plan.Cells.Single(c => c.PawnId == "trainee").EstimatedLabor);
        var modJob = plan.Cells.Single(c => c.PawnId == "specialist");
        Assert.AreEqual(modJob.ReferencePriority, modJob.After);
        Assert.AreEqual(modJob.ReferenceLabor, modJob.EstimatedLabor);
    }

    [TestMethod]
    public void ExhaustedFiniteOrderStopsGeneratingExperienceAndReleasesWorkerForFallback()
    {
        var pawn = Pawn("one", ("finite", new(1, true, 10, TrainingGain: 5)),
            ("fallback", new(6, true, 1, TrainingGain: 0)));
        var plan = WorkPlanner.Plan([pawn], [
            new("finite", "prod", OutputDemand: 2), new("fallback", "support")],
            Policy(.1m, "prod", "support"), 6);
        Assert.AreEqual(.2d, plan.Cells.Single(c => c.WorkId == "finite").EstimatedLabor, 1e-7);
        Assert.AreEqual(.8d, plan.Cells.Single(c => c.WorkId == "fallback").EstimatedLabor, 1e-7);
        Assert.AreEqual(1d, plan.Strategies.Single(s => s.Id == "prod").PlannedTraining, 1e-7);
    }

    [TestMethod]
    public void OneAffordableApprenticeIsSelectedEvenWhenAWholeTrainingTeamWouldExceedBudget()
    {
        PawnInput[] people = [
            Pawn("smith", ("steel", new(1, true, 100))),
            Pawn("tailor", ("cloth", new(1, true, 100))),
            Pawn("juniorSmith", ("steel", new(0, true, 86, TrainingGain: 3))),
            Pawn("juniorTailor", ("cloth", new(0, true, 86, TrainingGain: 5)))
        ];
        WorkColumn[] jobs = [new("cloth", "industry", Capacity: 1), new("steel", "industry", Capacity: 1)];
        var result = WorkPlanner.Plan(people, jobs, Policy(.10m, "industry"), 6);
        var report = result.Strategies.Single();
        Assert.AreEqual(93d, report.PlannedPerformance, 1e-7);
        Assert.AreEqual(5d, report.PlannedTraining);
        CollectionAssert.AreEqual(new[] { "juniorTailor", "smith" },
            result.Cells.Where(c => c.EstimatedLabor > 0).Select(c => c.PawnId).Order().ToArray());
        var reversed = WorkPlanner.Plan([.. people.Reverse()], [.. jobs.Reverse()], Policy(.10m, "industry"), 6);
        CollectionAssert.AreEqual(result.Cells.ToArray(), reversed.Cells.ToArray());
    }

    [TestMethod]
    public void TwoStrategicDirectionsCannotBothClaimAFullShiftAtTheSamePhysicalBench()
    {
        WorkColumn[] jobs =
        [
            new("meal", "survival", Capacity: 1, SharedSiteIds: "Table_42"),
            new("med", "industry", Capacity: 1, SharedSiteIds: "Table_42")
        ];
        var people = new[] { Pawn("cook", ("meal", new(1, true, 1))), Pawn("doctor", ("med", new(1, true, 1))) };
        var result = WorkPlanner.Plan(people, jobs, Policy(.1m, "survival", "industry"), 6);
        Assert.AreEqual(1d, result.UnassignedLabor, 1e-7);
        foreach (var group in result.Strategies)
        {
            Assert.AreEqual(.5d, group.EstimatedLabor, 1e-7);
            Assert.AreEqual(ShortfallReason.Capacity, group.Reason);
        }
    }

    [TestMethod]
    public void ASharedBenchCanSpendLeftoverTimeOnAnotherColumnWhenFiniteOrdersRunOut()
    {
        WorkColumn[] jobs =
        [
            new("finite", "daily", Capacity: 1, OutputDemand: .25, SharedSiteIds: "table"),
            new("other", "work", Capacity: 1, SharedSiteIds: "table")
        ];
        var people = new[] { Pawn("a", ("finite", new(1, true, 1))), Pawn("b", ("other", new(1, true, 1))) };
        var result = WorkPlanner.Plan(people, jobs, Policy(0, "daily", "work"), 6);
        Assert.AreEqual(.25d, result.Cells.Single(c => c.PawnId == "a").EstimatedLabor, 1e-7);
        Assert.AreEqual(.75d, result.Cells.Single(c => c.PawnId == "b").EstimatedLabor, 1e-7);
    }
}
