using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class TaskRotationServiceTests
{
    [Test]
    public void SelectForScope_UsesOnlyMatchingCustomPoolAndConfiguredCount()
    {
        var season = new BpSeason { DailyTaskCount = 2 };
        var tasks = new List<BpTaskTemplate>
        {
            Task("daily_fixed", "daily", "fixed"),
            Task("daily_random_1", "daily"),
            Task("daily_random_2", "daily"),
            Task("daily_random_3", "daily"),
            Task("weekly_random", "weekly"),
        };

        var selected = TaskRotationService.SelectForScope(tasks, season, "daily");

        Assert.That(selected, Has.Count.EqualTo(3));
        Assert.That(selected.All(task => task.Scope == "daily"), Is.True);
        Assert.That(selected.Any(task => task.Id == "daily_fixed"), Is.True);
        Assert.That(selected.Count(task => task.Rotation == "random"), Is.EqualTo(2));
    }

    [Test]
    public void SelectForScope_EmptyCustomPoolStaysEmpty()
    {
        var selected = TaskRotationService.SelectForScope([], new BpSeason { DailyTaskCount = 3 }, "daily");

        Assert.That(selected, Is.Empty);
    }

    [Test]
    public void NeedsReconciliation_DetectsDeletedTemplateAndMissingFixedTask()
    {
        var season = new BpSeason { DailyTaskCount = 1 };
        var tasks = new List<BpTaskTemplate>
        {
            Task("daily_fixed", "daily", "fixed"),
            Task("daily_random", "daily"),
        };
        var active = new List<BpActiveTask>
        {
            Active("deleted_task", "daily"),
            Active("daily_random", "daily"),
        };

        var needsReconciliation = TaskRotationService.NeedsReconciliation(tasks, season, active, "daily");

        Assert.That(needsReconciliation, Is.True);
    }

    [Test]
    public void NeedsReconciliation_AcceptsValidCurrentSelection()
    {
        var season = new BpSeason { DailyTaskCount = 1 };
        var tasks = new List<BpTaskTemplate>
        {
            Task("daily_fixed", "daily", "fixed"),
            Task("daily_random_1", "daily"),
            Task("daily_random_2", "daily"),
        };
        var active = new List<BpActiveTask>
        {
            Active("daily_fixed", "daily"),
            Active("daily_random_2", "daily"),
        };

        var needsReconciliation = TaskRotationService.NeedsReconciliation(tasks, season, active, "daily");

        Assert.That(needsReconciliation, Is.False);
    }

    [Test]
    public void ReconcileScope_AddsFixedTaskWithoutDiscardingValidRandomProgress()
    {
        var season = new BpSeason { DailyTaskCount = 1 };
        var tasks = new List<BpTaskTemplate>
        {
            Task("new_fixed", "daily", "fixed"),
            Task("current_random", "daily"),
            Task("other_random", "daily"),
        };
        var progress = new BpProgress
        {
            ActiveTasks = [new BpActiveTask { TaskId = "current_random", Scope = "daily", Progress = 7 }],
        };

        BattlePassTrackService.ReconcileScope(progress, tasks, season, "daily", 1234);

        Assert.That(progress.ActiveTasks.Any(task => task.TaskId == "new_fixed"), Is.True);
        Assert.That(progress.ActiveTasks.Single(task => task.TaskId == "current_random").Progress, Is.EqualTo(7));
        Assert.That(progress.ActiveTasks.Count(task => task.Scope == "daily"), Is.EqualTo(2));
    }

    [Test]
    public void ShouldRoll_UsesProvidedSeasonPeriodSnapshot()
    {
        var season = new BpSeason { DailyPeriodHours = 6 };
        const long last = 1_000_000;

        Assert.That(TaskRotationService.ShouldRoll(season, "daily", last, last + 6 * 3600 - 1), Is.False);
        Assert.That(TaskRotationService.ShouldRoll(season, "daily", last, last + 6 * 3600), Is.True);
    }

    private static BpTaskTemplate Task(string id, string scope, string rotation = "random")
    {
        return new BpTaskTemplate { Id = id, Scope = scope, Rotation = rotation };
    }

    private static BpActiveTask Active(string id, string scope)
    {
        return new BpActiveTask { TaskId = id, Scope = scope };
    }
}
