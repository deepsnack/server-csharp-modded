using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class BattlePassClothingSearchServiceTests
{
    private BattlePassClothingSearchService _clothingSearch = null!;
    private DatabaseService _databaseService = null!;
    private LocaleService _localeService = null!;

    [OneTimeSetUp]
    public void Initialize()
    {
        _clothingSearch = DI.GetInstance().GetService<BattlePassClothingSearchService>();
        _databaseService = DI.GetInstance().GetService<DatabaseService>();
        _localeService = DI.GetInstance().GetService<LocaleService>();
    }

    [Test]
    public void Search_IncludesUpperOrLowerCustomizationWithoutTraderOffer()
    {
        var clothing = _databaseService.GetCustomization()
            .FirstOrDefault(pair => pair.Value.Parent is CustomisationTypeId.UPPER or CustomisationTypeId.LOWER);
        Assert.That(clothing.Value, Is.Not.Null, "测试数据库应至少包含一件上下装 customization");

        var id = clothing.Key.ToString();
        var results = _clothingSearch.Search(id, 100);

        Assert.That(results.Any(result => result.SuitId == id), Is.True);
    }

    [Test]
    public void Search_MatchesChineseLocaleNameWhenAvailable()
    {
        var ch = _localeService.GetLocaleDb("ch");
        var sample = _databaseService.GetCustomization()
            .Select(pair => new
            {
                Id = pair.Key.ToString(),
                Item = pair.Value,
                Name = ch.GetValueOrDefault($"{pair.Key} Name"),
            })
            .FirstOrDefault(x =>
                x.Item.Parent is CustomisationTypeId.SUITS or CustomisationTypeId.UPPER or CustomisationTypeId.LOWER
                && !string.IsNullOrWhiteSpace(x.Name));

        if (sample is null)
        {
            Assert.Inconclusive("测试数据库没有可用于中文服装搜索的 locale 样例");
            return;
        }

        var results = _clothingSearch.Search(sample.Name, 100);

        Assert.That(results.Any(result => result.SuitId == sample.Id), Is.True);
    }
}
