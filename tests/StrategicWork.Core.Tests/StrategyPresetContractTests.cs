using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StrategicWork.Core;

namespace StrategicWork.Core.Tests;

[TestClass]
public class StrategyPresetContractTests
{
    [TestMethod]
    public void ResearchOnlyFixtureRetainsExplicitZeroAndRemainderPolicy()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
        };
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "research-only.json");
        var preset = JsonSerializer.Deserialize<StrategyPreset>(File.ReadAllText(path), options)!;
        Assert.AreEqual(1, preset.SchemaVersion);
        Assert.AreEqual(new StrategyAllocation("survival", 0, 0), preset.Strategies.Single(x => x.Id == "survival"));
        Assert.AreEqual(new StrategyAllocation("research", 0, 100), preset.Strategies.Single(x => x.Id == "research"));
        Assert.AreEqual(UnweightedRemainder.LeaveUnassigned, preset.UnweightedRemainder);
        Assert.AreEqual(0m, preset.TrainingLossLimit, "A v0.2 preset must favor current performance when upgraded.");
    }
}
