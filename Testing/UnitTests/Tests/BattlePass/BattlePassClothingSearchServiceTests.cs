using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Json;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
[NonParallelizable]
public class BattlePassClothingSearchServiceTests
{
    private static readonly MongoId BackportSuiteId = new("68bc315be6068f34a7016e94");
    private static readonly MongoId BackportOutfitId = new("694c14f7a745487130a150fa");
    private static readonly MongoId BackportTopId = new("68bc29c8b50500fb3d079f3e");
    private static readonly MongoId BackportHandsId = new("68bc2de3e6068f34a7016e92");
    private static readonly MongoId RagmanId = new("5ac3b934156ae10c4430e83c");

    private BattlePassClothingSearchService _clothingSearch = null!;
    private BattlePassClothingCatalogService _clothingCatalog = null!;
    private BattlePassService _battlePassService = null!;
    private DatabaseService _databaseService = null!;
    private LocaleService _localeService = null!;
    private NotificationService _notificationService = null!;
    private SaveServer _saveServer = null!;

    [OneTimeSetUp]
    public void Initialize()
    {
        _clothingSearch = DI.GetInstance().GetService<BattlePassClothingSearchService>();
        _clothingCatalog = DI.GetInstance().GetService<BattlePassClothingCatalogService>();
        _battlePassService = DI.GetInstance().GetService<BattlePassService>();
        _databaseService = DI.GetInstance().GetService<DatabaseService>();
        _localeService = DI.GetInstance().GetService<LocaleService>();
        _notificationService = DI.GetInstance().GetService<NotificationService>();
        _saveServer = DI.GetInstance().GetService<SaveServer>();
    }

    [Test]
    public void Search_IncludesUpperOrLowerCustomizationWithoutTraderOffer()
    {
        var clothing = _databaseService.GetCustomization()
            .FirstOrDefault(pair =>
                string.Equals(pair.Value.Type, "Item", StringComparison.OrdinalIgnoreCase)
                && pair.Value.Parent is CustomisationTypeId.UPPER or CustomisationTypeId.LOWER);
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
                string.Equals(x.Item.Type, "Item", StringComparison.OrdinalIgnoreCase)
                && x.Item.Parent is CustomisationTypeId.SUITS or CustomisationTypeId.UPPER or CustomisationTypeId.LOWER
                && !string.IsNullOrWhiteSpace(x.Name));

        if (sample is null)
        {
            Assert.Inconclusive("测试数据库没有可用于中文服装搜索的 locale 样例");
            return;
        }

        var results = _clothingSearch.Search(sample.Name, 100);

        Assert.That(results.Any(result => result.SuitId == sample.Id), Is.True);
    }

    [Test]
    public void Catalog_RejectsNodeAndComponentEvenWhenTraderOffersReferenceThem()
    {
        var nodeId = new MongoId();
        var bodyId = new MongoId();
        var nodeOfferId = new MongoId();
        var componentOfferId = new MongoId();
        var customization = _databaseService.GetCustomization();
        var ragman = _databaseService.GetTrader(RagmanId);
        Assert.That(ragman, Is.Not.Null);

        var node = new CustomizationItem
        {
            Id = nodeId,
            Name = "SyntheticUpperCategory",
            Parent = CustomisationTypeId.SUITS,
            Type = "Node",
            Properties = new CustomizationProperties { Body = bodyId, Side = ["Usec"] },
        };
        var component = new CustomizationItem
        {
            Id = bodyId,
            Name = "SyntheticBodyComponent",
            Parent = CustomisationTypeId.BODY,
            Type = "Item",
            Properties = new CustomizationProperties { Side = ["Usec"] },
        };
        var nodeOffer = new Suit { Id = nodeOfferId, Tid = RagmanId, SuiteId = nodeId, IsActive = true };
        var componentOffer = new Suit { Id = componentOfferId, Tid = RagmanId, SuiteId = bodyId, IsActive = true };

        customization[nodeId] = node;
        customization[bodyId] = component;
        ragman!.Suits ??= [];
        ragman.Suits.Add(nodeOffer);
        ragman.Suits.Add(componentOffer);
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(_clothingCatalog.IsRegisteredSuite(nodeId), Is.False);
                Assert.That(_clothingCatalog.IsRegisteredSuite(bodyId), Is.False);
                Assert.That(_clothingSearch.Search(nodeId.ToString(), 100), Is.Empty);
                Assert.That(_clothingSearch.Search(nodeOfferId.ToString(), 100), Is.Empty);
                Assert.That(_clothingSearch.Search(bodyId.ToString(), 100), Is.Empty);
                Assert.That(_clothingSearch.Search(componentOfferId.ToString(), 100), Is.Empty);
            });
        }
        finally
        {
            ragman.Suits.Remove(nodeOffer);
            ragman.Suits.Remove(componentOffer);
            customization.Remove(nodeId);
            customization.Remove(bodyId);
        }
    }

    [Test]
    public void Search_BackportStyleSuite_MatchesEveryRegisteredLocaleAndReturnsSuiteId()
    {
        var customization = _databaseService.GetCustomization();
        var ragman = _databaseService.GetTrader(RagmanId);
        Assert.That(ragman, Is.Not.Null);
        var hadSuite = customization.TryGetValue(BackportSuiteId, out var previousSuite);
        var hadTop = customization.TryGetValue(BackportTopId, out var previousTop);
        var hadHands = customization.TryGetValue(BackportHandsId, out var previousHands);
        var globalLocales = _databaseService.GetLocales().Global;
        var previousEnLocale = globalLocales["en"];
        var previousChLocale = globalLocales["ch"];
        var enLocale = new Dictionary<string, string>(previousEnLocale.Value!);
        var chLocale = new Dictionary<string, string>(previousChLocale.Value!);

        var suite = new CustomizationItem
        {
            Id = BackportSuiteId,
            Name = "DefaultUsecUpperSuite",
            Parent = CustomisationTypeId.UPPER,
            Type = "Item",
            Properties = new CustomizationProperties
            {
                Name = "DefaultUsecUpperSuite",
                ShortName = "DefaultUsecUpperSuite",
                Body = BackportTopId,
                Hands = BackportHandsId,
                Side = ["Usec"],
            },
        };
        var offer = new Suit
        {
            Id = BackportOutfitId,
            Tid = RagmanId,
            SuiteId = BackportSuiteId,
            IsActive = true,
        };
        var top = new CustomizationItem
        {
            Id = BackportTopId,
            Name = "DefaultUsecUpper",
            Parent = CustomisationTypeId.BODY,
            Type = "Item",
            Properties = new CustomizationProperties { Side = ["Usec"] },
        };
        var hands = new CustomizationItem
        {
            Id = BackportHandsId,
            Name = "DefaultUsecHands",
            Parent = CustomisationTypeId.HANDS,
            Type = "Item",
            Properties = new CustomizationProperties { Side = ["Usec"] },
        };

        customization[BackportSuiteId] = suite;
        customization[BackportTopId] = top;
        customization[BackportHandsId] = hands;
        ragman!.Suits ??= [];
        var previousOffers = ragman.Suits.ToList();
        ragman.Suits.RemoveAll(candidate => candidate.Id == BackportOutfitId || candidate.SuiteId == BackportSuiteId);
        ragman.Suits.Add(offer);
        SetLocaleName(enLocale, BackportSuiteId, "Champion upper");
        SetLocaleName(chLocale, BackportSuiteId, "冠军上装");
        globalLocales["en"] = new LazyLoad<Dictionary<string, string>>(() => new(enLocale));
        globalLocales["ch"] = new LazyLoad<Dictionary<string, string>>(() => new(chLocale));
        try
        {
            Assert.That(enLocale[BackportSuiteId.ToString()], Is.EqualTo("Champion upper"));
            Assert.That(chLocale[BackportSuiteId.ToString()], Is.EqualTo("冠军上装"));
            var suiteIdResult = _clothingSearch.Search(BackportSuiteId.ToString(), 100)
                .Single(result => result.SuitId == BackportSuiteId.ToString());
            Assert.That(
                suiteIdResult.SearchTerms,
                Does.Contain("Champion upper"),
                $"未从 en locale 收集名称；实际术语：{string.Join(" | ", suiteIdResult.SearchTerms)}"
            );
            Assert.That(
                suiteIdResult.SearchTerms,
                Does.Contain("冠军上装"),
                $"未从 ch locale 收集名称；实际术语：{string.Join(" | ", suiteIdResult.SearchTerms)}"
            );
            var englishResults = _clothingSearch.Search("Champion upper", 100);
            var chineseResults = _clothingSearch.Search("冠军上装", 100);
            var offerResults = _clothingSearch.Search(BackportOutfitId.ToString(), 100);
            var topResults = _clothingSearch.Search(BackportTopId.ToString(), 100);
            var handsResults = _clothingSearch.Search(BackportHandsId.ToString(), 100);

            Assert.Multiple(() =>
            {
                Assert.That(englishResults.Any(result => result.SuitId == BackportSuiteId.ToString()), Is.True);
                Assert.That(chineseResults.Any(result => result.SuitId == BackportSuiteId.ToString()), Is.True);
                Assert.That(offerResults.Any(result => result.SuitId == BackportSuiteId.ToString()), Is.True);
                Assert.That(topResults.Any(result => result.SuitId == BackportSuiteId.ToString()), Is.True);
                Assert.That(handsResults.Any(result => result.SuitId == BackportSuiteId.ToString()), Is.True);
                Assert.That(topResults.Any(result => result.SuitId == BackportTopId.ToString()), Is.False);
                Assert.That(handsResults.Any(result => result.SuitId == BackportHandsId.ToString()), Is.False);
                Assert.That(
                    englishResults.Single(result => result.SuitId == BackportSuiteId.ToString()).OfferId,
                    Is.EqualTo(BackportOutfitId.ToString())
                );
            });

            ragman.Suits.Remove(offer);
            var nonTraderResults = _clothingSearch.Search(BackportSuiteId.ToString(), 100);
            Assert.That(nonTraderResults.Single(result => result.SuitId == BackportSuiteId.ToString()).IsTraderSuit, Is.False);
        }
        finally
        {
            ragman.Suits.Clear();
            ragman.Suits.AddRange(previousOffers);
            RestoreCustomization(customization, BackportSuiteId, hadSuite, previousSuite);
            RestoreCustomization(customization, BackportTopId, hadTop, previousTop);
            RestoreCustomization(customization, BackportHandsId, hadHands, previousHands);
            globalLocales["en"] = previousEnLocale;
            globalLocales["ch"] = previousChLocale;
        }
    }

    [Test]
    public void AddClothingUnlock_BackportStyleSuite_StoresSuiteIdRatherThanComponentIds()
    {
        var profile = new SptProfile();

        var added = BattlePassService.AddClothingUnlock(profile, BackportSuiteId);

        Assert.Multiple(() =>
        {
            Assert.That(added, Is.True);
            Assert.That(profile.CustomisationUnlocks, Has.Count.EqualTo(1));
            Assert.That(profile.CustomisationUnlocks![0].Id, Is.EqualTo(BackportSuiteId));
            Assert.That(profile.CustomisationUnlocks[0].Id, Is.Not.EqualTo(BackportTopId));
            Assert.That(profile.CustomisationUnlocks[0].Id, Is.Not.EqualTo(BackportHandsId));
            Assert.That(profile.CustomisationUnlocks[0].Type, Is.EqualTo(CustomisationType.SUITE));
            Assert.That(profile.CustomisationUnlocks[0].Source, Is.EqualTo(CustomisationSource.UNLOCKED_IN_GAME));
        });
    }

    [Test]
    public void Search_AppliesStableNameOrderingBeforeLimit()
    {
        const string prefix = "BP-Limit-Order-";
        var alphaId = new MongoId();
        var zuluId = new MongoId();
        var customization = _databaseService.GetCustomization();
        customization[alphaId] = new CustomizationItem
        {
            Id = alphaId,
            Name = $"{prefix}Alpha",
            Parent = CustomisationTypeId.UPPER,
            Type = "Item",
            Properties = new CustomizationProperties { Name = $"{prefix}Alpha", Side = ["Usec"] },
        };
        customization[zuluId] = new CustomizationItem
        {
            Id = zuluId,
            Name = $"{prefix}Zulu",
            Parent = CustomisationTypeId.UPPER,
            Type = "Item",
            Properties = new CustomizationProperties { Name = $"{prefix}Zulu", Side = ["Usec"] },
        };

        try
        {
            var results = _clothingSearch.Search(prefix, 1);
            Assert.Multiple(() =>
            {
                Assert.That(results, Has.Count.EqualTo(1));
                Assert.That(results[0].SuitId, Is.EqualTo(alphaId.ToString()));
            });
        }
        finally
        {
            customization.Remove(alphaId);
            customization.Remove(zuluId);
        }
    }

    [Test]
    public void GrantRewards_BackportStyleSuite_WritesOnlySuiteEntitlement()
    {
        var customization = _databaseService.GetCustomization();
        var profileId = new MongoId();
        var username = $"bp-clothing-{profileId}";
        var hadSuite = customization.TryGetValue(BackportSuiteId, out var previousSuite);
        var hadTop = customization.TryGetValue(BackportTopId, out var previousTop);
        var hadHands = customization.TryGetValue(BackportHandsId, out var previousHands);
        customization[BackportSuiteId] = new CustomizationItem
        {
            Id = BackportSuiteId,
            Name = "DefaultUsecUpperSuite",
            Parent = CustomisationTypeId.UPPER,
            Type = "Item",
            Properties = new CustomizationProperties
            {
                Body = BackportTopId,
                Hands = BackportHandsId,
                Side = ["Usec"],
            },
        };
        customization[BackportTopId] = new CustomizationItem
        {
            Id = BackportTopId,
            Name = "DefaultUsecUpper",
            Parent = CustomisationTypeId.BODY,
            Type = "Item",
            Properties = new CustomizationProperties { Side = ["Usec"] },
        };
        customization[BackportHandsId] = new CustomizationItem
        {
            Id = BackportHandsId,
            Name = "DefaultUsecHands",
            Parent = CustomisationTypeId.HANDS,
            Type = "Item",
            Properties = new CustomizationProperties { Side = ["Usec"] },
        };
        _saveServer.CreateProfile(
            new SPTarkov.Server.Core.Models.Eft.Profile.Info
            {
                ProfileId = profileId,
                Username = username,
                Edition = "Standard",
            }
        );

        try
        {
            var rejectedComponent = Assert.Catch<InvalidOperationException>(() =>
                _battlePassService.GrantRewards(
                    profileId.ToString(),
                    new BpProgress(),
                    [new BpReward { Type = "clothing", SuitId = BackportTopId.ToString() }],
                    "test"
                )
            );
            var unlockCountAfterComponent = _saveServer.GetProfile(profileId).CustomisationUnlocks?.Count ?? 0;
            var message = _battlePassService.GrantRewards(
                profileId.ToString(),
                new BpProgress(),
                [new BpReward { Type = "clothing", SuitId = BackportSuiteId.ToString() }],
                "test"
            );
            var profile = _saveServer.GetProfile(profileId);
            var persistedProfilePath = FindTestProfileFile(username);
            var persistedProfileJson = persistedProfilePath is null ? "" : File.ReadAllText(persistedProfilePath);

            Assert.Multiple(() =>
            {
                Assert.That(rejectedComponent!.Message, Does.Contain("不是当前已注册的可发放套装"));
                Assert.That(unlockCountAfterComponent, Is.Zero);
                Assert.That(message, Does.Contain("已解锁 1 件服装"));
                Assert.That(profile.CustomisationUnlocks, Has.Count.EqualTo(1));
                Assert.That(profile.CustomisationUnlocks![0].Id, Is.EqualTo(BackportSuiteId));
                Assert.That(profile.CustomisationUnlocks.Any(item => item.Id == BackportTopId), Is.False);
                Assert.That(profile.CustomisationUnlocks.Any(item => item.Id == BackportHandsId), Is.False);
                Assert.That(persistedProfilePath, Is.Not.Null, "服装奖励应在返回成功前写入档案文件");
                Assert.That(persistedProfileJson, Does.Contain(BackportSuiteId.ToString()));
            });
        }
        finally
        {
            _notificationService.GetMessageQueue().Remove(profileId);
            _saveServer.DeleteProfileById(profileId);
            RestoreCustomization(customization, BackportSuiteId, hadSuite, previousSuite);
            RestoreCustomization(customization, BackportTopId, hadTop, previousTop);
            RestoreCustomization(customization, BackportHandsId, hadHands, previousHands);
            DeleteTestProfileFile(username);
        }
    }

    private static void SetLocaleName(Dictionary<string, string> locale, MongoId id, string value)
    {
        locale[id.ToString()] = value;
        locale[$"{id} Name"] = value;
    }

    private static void RestoreCustomization(
        Dictionary<MongoId, CustomizationItem> customization,
        MongoId id,
        bool existed,
        CustomizationItem? previous
    )
    {
        if (existed)
        {
            customization[id] = previous!;
        }
        else
        {
            customization.Remove(id);
        }
    }

    private static string? FindTestProfileFile(string username)
    {
        return new[] { Directory.GetCurrentDirectory(), TestContext.CurrentContext.WorkDirectory }
            .Select(root => System.IO.Path.Combine(root, "user", "profiles", $"{username}.json"))
            .FirstOrDefault(File.Exists);
    }

    private static void DeleteTestProfileFile(string username)
    {
        foreach (var root in new[] { Directory.GetCurrentDirectory(), TestContext.CurrentContext.WorkDirectory })
        {
            var path = System.IO.Path.Combine(root, "user", "profiles", $"{username}.json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
