using System.Reflection;
using NUnit.Framework;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
[NonParallelizable]
public class BattlePassTraderModItemTests
{
    private const string ModTpl = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private BattlePassTraderSync _traderSync;
    private DatabaseService _databaseService;

    [OneTimeSetUp]
    public void Initialize()
    {
        _traderSync = DI.GetInstance().GetService<BattlePassTraderSync>();
        _databaseService = DI.GetInstance().GetService<DatabaseService>();
    }

    [Test]
    public void BattlePassMod_LoadsAfterModItemIndexAndBeforeItemControlFinalization()
    {
        var injectable = typeof(BattlePassMod).GetCustomAttribute<Injectable>();

        Assert.That(injectable, Is.Not.Null);
        Assert.That(injectable!.TypePriority, Is.GreaterThan(OnLoadOrder.PostDBModLoader + 89000));
        Assert.That(injectable.TypePriority, Is.LessThan(OnLoadOrder.PostDBModLoader + 90000));
    }

    [Test]
    public void BuildAssort_IncludesModTemplateOncePostDbModHasCreatedIt()
    {
        var templates = _databaseService.GetItems();
        var modTpl = new MongoId(ModTpl);
        var offer = new BpTraderOffer { Id = "mod_item_offer", Tpl = ModTpl, Stock = -1 };
        templates.Remove(modTpl);

        try
        {
            var beforeModCreation = _traderSync.BuildAssort([offer]);
            Assert.That(beforeModCreation.Items, Is.Empty);

            templates[modTpl] = templates.Values.First();
            var afterModCreation = _traderSync.BuildAssort([offer]);

            // 货架物品按默认完整形态注入：根件在首位（枪/甲/盔可能带子件，故不强求恰好 1 项）。
            Assert.That(afterModCreation.Items, Is.Not.Empty);
            Assert.That(afterModCreation.Items[0].Template, Is.EqualTo(modTpl));
            Assert.That(afterModCreation.BarterScheme, Contains.Key(BattlePassTraderSync.OfferRootItemId(offer.Id)));
            Assert.That(afterModCreation.LoyalLevelItems, Contains.Key(BattlePassTraderSync.OfferRootItemId(offer.Id)));
        }
        finally
        {
            templates.Remove(modTpl);
        }
    }
}
