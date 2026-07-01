using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public partial class QuestSkipFrontendTests
{
    [Test]
    public void PlayerQuestSkipDto_ContainsNoQuestOrConditionMongoIds()
    {
        var dto = new QuestSkipStateResult
        {
            Success = true,
            TicketCount = 1,
            Tasks =
            [
                new QuestSkipTaskView
                {
                    TitleZh = "每日任务：歼灭行动",
                    Objectives =
                    [
                        new QuestSkipObjectiveView
                        {
                            ActionId = Guid.NewGuid().ToString("N"),
                            ConditionNameZh = "击杀目标",
                            ConditionDescriptionZh = "在海关击杀五名目标",
                        },
                    ],
                },
            ],
        };

        var json = JsonSerializer.Serialize(dto);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("questId").IgnoreCase);
            Assert.That(json, Does.Not.Contain("conditionId").IgnoreCase);
            Assert.That(MongoIdRegex().IsMatch(json), Is.False);
        });
    }

    [Test]
    public void QuestSkipPlayerAssets_DoNotContainRawQuestInternals()
    {
        var pageDir = FindPageSourceDirectory();
        var visibleAssets = string.Join('\n', new[] { "quest-skip.html", "quest-skip.js" }.Select(file => File.ReadAllText(Path.Combine(pageDir, file))));

        Assert.Multiple(() =>
        {
            Assert.That(MongoIdRegex().IsMatch(visibleAssets), Is.False);
            Assert.That(InternalTypeRegex().IsMatch(visibleAssets), Is.False);
            Assert.That(visibleAssets, Does.Not.Contain("UNKNOWN"));
            Assert.That(visibleAssets, Does.Contain("消耗 <strong>1 张任务跳过券</strong>"));
        });
    }

    [Test]
    public void EveryPlayerPage_UsesStickyUnifiedNavigation()
    {
        var pageDir = FindPageSourceDirectory();
        foreach (var file in new[] { "index.html", "quest-skip.html", "lottery.html", "titles.html" })
        {
            var html = File.ReadAllText(Path.Combine(pageDir, file));
            Assert.That(html, Does.Contain("player-topbar"), file);
            Assert.That(html, Does.Contain("player-nav"), file);
            Assert.That(html, Does.Contain("quest-skip.html"), file);
        }

        var css = File.ReadAllText(Path.Combine(pageDir, "style.css"));
        Assert.That(css, Does.Match(@"\.player-topbar\s*\{[^}]*position:\s*sticky"));
    }

    [Test]
    public void AdminTrackPage_HasBulkAppendRewardControls()
    {
        var adminDir = Path.Combine(FindPageSourceDirectory(), "admin");
        var html = File.ReadAllText(Path.Combine(adminDir, "index.html"));
        var script = File.ReadAllText(Path.Combine(adminDir, "script.js"));

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("批量添加一种奖励"));
            Assert.That(html, Does.Contain("bulk-reward-track"));
            Assert.That(script, Does.Contain("applyBulkReward"));
            Assert.That(script, Does.Contain("已有奖励不会被覆盖"));
        });
    }

    private static string FindPageSourceDirectory()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "Libraries", "SPTarkov.Server.Assets", "SPT_Data", "battlepass", "page");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("找不到 BattlePass 页面源码目录");
    }

    [GeneratedRegex("(?i)(?<![0-9a-f])[0-9a-f]{24}(?![0-9a-f])")]
    private static partial Regex MongoIdRegex();

    [GeneratedRegex("(?i)CounterCreator|HandoverItem|FindItem|LeaveItemAtLocation|PlaceBeacon|WeaponAssembly|SellItemToTrader|UNKNOWN")]
    private static partial Regex InternalTypeRegex();
}
