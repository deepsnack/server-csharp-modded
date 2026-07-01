using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class QuestSkipTicketServiceTests
{
    private DatabaseService _databaseService;
    private BattlePassItemBuilder _itemBuilder;

    [OneTimeSetUp]
    public void Initialize()
    {
        var di = DI.GetInstance();
        di.GetService<QuestSkipTicketService>().Register();
        _databaseService = di.GetService<DatabaseService>();
        _itemBuilder = di.GetService<BattlePassItemBuilder>();
    }

    [Test]
    public void Ticket_ClonesLabsKeycardAppearanceAndDisablesTrading()
    {
        var ticket = _databaseService.GetItems()[QuestSkipTicketService.TicketTpl];
        var source = _databaseService.GetItems()[ItemTpl.KEYCARD_TERRAGROUP_LABS_ACCESS];

        Assert.Multiple(() =>
        {
            Assert.That(ticket.Properties?.Prefab?.Path, Is.EqualTo(source.Properties?.Prefab?.Path));
            Assert.That(ticket.Properties?.StackMaxSize, Is.EqualTo(1));
            Assert.That(ticket.Properties?.CanSellOnRagfair, Is.False);
            Assert.That(ticket.Properties?.CanRequireOnRagfair, Is.False);
            Assert.That(ticket.Properties?.IsUnsaleable, Is.True);
            Assert.That(ticket.Properties?.IsUnbuyable, Is.True);
            // 券必须「可给予」：IsUngivable=true 会让客户端无法从邮箱取出，导致商店/奖励轨/任务/激活码/抽奖发放全部失效。
            // 不可出售/不可跳蚤由 IsUnsaleable + CanSellOnRagfair/CanRequireOnRagfair=false 承担。
            Assert.That(ticket.Properties?.IsUngivable, Is.Not.True);
            Assert.That(ticket.Properties?.QuestItem, Is.False);
        });
    }

    [Test]
    public void Ticket_CanUseExistingRewardItemBuilder()
    {
        var items = _itemBuilder.BuildRewardStacks(QuestSkipTicketService.TicketTpl, 3);

        Assert.Multiple(() =>
        {
            Assert.That(items, Has.Count.EqualTo(3));
            Assert.That(items, Has.All.Matches<SPTarkov.Server.Core.Models.Eft.Common.Tables.Item>(item =>
                item.Template == QuestSkipTicketService.TicketTpl));
        });
    }
}
