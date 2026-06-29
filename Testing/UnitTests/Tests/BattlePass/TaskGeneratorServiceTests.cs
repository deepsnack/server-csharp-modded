using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
[NonParallelizable]
public class TaskGeneratorServiceTests
{
    [Test]
    public void Generate_ReplacesOnlyGeneratedTasksForRequestedScope()
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "spt-bp-generator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            Directory.SetCurrentDirectory(tempDirectory);
            BattlePassStore.SaveTasks(
                [
                    Task("manual_daily", "daily"),
                    Task("gen_daily_old", "daily"),
                    Task("gen_weekly_keep", "weekly"),
                ]
            );
            BattlePassStore.SaveGenSpec(new BpGenSpec
            {
                Daily = new BpGenScopeSpec
                {
                    Enabled = true,
                    Count = 5,
                    ConditionTypes = ["Exploration"],
                    MinCount = 2,
                    MaxCount = 4,
                },
            });

            var service = DI.GetInstance().GetService<TaskGeneratorService>();
            var generated = service.Generate(["daily"]);
            var custom = BattlePassStore.GetTasks();   // 管理员自定义池（应不含任何生成任务）
            var gen = BattlePassStore.GetGenTasks();    // 独立生成池
            var spec = BattlePassStore.GetGenSpec();

            Assert.That(generated, Is.EqualTo(5));
            // 自定义池只剩手写任务，生成任务不再污染后台任务管理。
            Assert.That(custom.Any(t => t.Id == "manual_daily"), Is.True);
            Assert.That(custom.Any(t => t.Id.StartsWith("gen_", StringComparison.Ordinal)), Is.False);
            // 生成池：同 scope 旧任务被替换，其它 scope 保留，本次生成 5 条。
            Assert.That(gen.Any(t => t.Id == "gen_daily_old"), Is.False);
            Assert.That(gen.Any(t => t.Id == "gen_weekly_keep"), Is.True);
            Assert.That(gen.Count(t => t.Id.StartsWith("gen_daily_", StringComparison.Ordinal)), Is.EqualTo(5));
            Assert.That(gen.Where(t => t.Id.StartsWith("gen_daily_", StringComparison.Ordinal)).All(t => t.ConditionType == "Exploration"), Is.True);
            Assert.That(spec.Daily.LastGenUtc, Is.GreaterThan(0));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static BpTaskTemplate Task(string id, string scope)
    {
        return new BpTaskTemplate { Id = id, Scope = scope };
    }
}
