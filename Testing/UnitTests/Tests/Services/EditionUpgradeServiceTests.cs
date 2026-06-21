using NUnit.Framework;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;
using BotCustomization = SPTarkov.Server.Core.Models.Eft.Common.Tables.Customization;

namespace UnitTests.Tests.Services;

[TestFixture]
public class EditionUpgradeServiceTests
{
    // ---------- 等级序与版本解析 ----------

    [Test]
    public void EditionLadder_HasFiveStandardEditionsInOrder()
    {
        Assert.AreEqual(5, EditionUpgradeService.EditionLadder.Count);
        Assert.AreEqual("Standard", EditionUpgradeService.EditionLadder[0]);
        Assert.AreEqual("Left Behind", EditionUpgradeService.EditionLadder[1]);
        Assert.AreEqual("Prepare To Escape", EditionUpgradeService.EditionLadder[2]);
        Assert.AreEqual("Edge Of Darkness", EditionUpgradeService.EditionLadder[3]);
        Assert.AreEqual("Unheard", EditionUpgradeService.EditionLadder[4]);
    }

    // ---------- SignatureOf ----------

    [Test]
    public void SignatureOf_DifferentTplProducesDifferentSig()
    {
        var a = NewItem("aaaaaaaaaaaaaaaaaaaaaaaa");
        var b = NewItem("bbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.AreNotEqual(EditionUpgradeService.SignatureOf(a), EditionUpgradeService.SignatureOf(b));
    }

    [Test]
    public void SignatureOf_SameTplDifferentStackProducesDifferentSig()
    {
        var a = NewItem("aaaaaaaaaaaaaaaaaaaaaaaa", stack: 1);
        var b = NewItem("aaaaaaaaaaaaaaaaaaaaaaaa", stack: 60);
        Assert.AreNotEqual(EditionUpgradeService.SignatureOf(a), EditionUpgradeService.SignatureOf(b));
    }

    [Test]
    public void SignatureOf_SameTplDifferentDurabilityProducesDifferentSig()
    {
        var a = NewItem("aaaaaaaaaaaaaaaaaaaaaaaa", durability: 100);
        var b = NewItem("aaaaaaaaaaaaaaaaaaaaaaaa", durability: 80);
        Assert.AreNotEqual(EditionUpgradeService.SignatureOf(a), EditionUpgradeService.SignatureOf(b));
    }

    // ---------- ComputeItemBundleDiff ----------

    [Test]
    public void ComputeItemBundleDiff_TargetExclusiveItemIsAdded()
    {
        // 源模板：stash 内 1 件 X；目标模板：stash 内 1 件 X + 1 件 Y；玩家：stash 内 1 件 X
        var (src, tgt, pmc) = BuildSidesWithStash(
            sourceRoots: new[] { "aaaaaaaaaaaaaaaaaaaaaaaa" },
            targetRoots: new[] { "aaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbb" },
            playerOwnedRoots: new[] { "aaaaaaaaaaaaaaaaaaaaaaaa" }
        );

        var bundles = EditionUpgradeService.ComputeItemBundleDiff(src, tgt, pmc);

        Assert.AreEqual(1, bundles.Count);
        Assert.AreEqual("bbbbbbbbbbbbbbbbbbbbbbbb", bundles[0].RootTemplate);
    }

    [Test]
    public void ComputeItemBundleDiff_PlayerHavingItemDoesNotResendIt()
    {
        // 玩家已经把目标独有物品自行从市场买到 → 不重复发
        var (src, tgt, pmc) = BuildSidesWithStash(
            sourceRoots: new[] { "aaaaaaaaaaaaaaaaaaaaaaaa" },
            targetRoots: new[] { "aaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbb" },
            playerOwnedRoots: new[] { "aaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbb" }
        );

        var bundles = EditionUpgradeService.ComputeItemBundleDiff(src, tgt, pmc);

        Assert.AreEqual(0, bundles.Count);
    }

    [Test]
    public void ComputeItemBundleDiff_DuplicateTplInTargetIsCountedSeparately()
    {
        // 目标模板有 2 件相同 _tpl，玩家只有 1 件，差 1 件
        var (src, tgt, pmc) = BuildSidesWithStash(
            sourceRoots: System.Array.Empty<string>(),
            targetRoots: new[] { "bbbbbbbbbbbbbbbbbbbbbbbb", "bbbbbbbbbbbbbbbbbbbbbbbb" },
            playerOwnedRoots: new[] { "bbbbbbbbbbbbbbbbbbbbbbbb" }
        );

        var bundles = EditionUpgradeService.ComputeItemBundleDiff(src, tgt, pmc);

        Assert.AreEqual(1, bundles.Count);
    }

    [Test]
    public void ComputeItemBundleDiff_BundledChildrenTransferTogether()
    {
        // 目标 stash 根 = 武器（带一个 mod 子物品），玩家无；应整组发出 2 件
        var stashId = new MongoId();
        var rootId = new MongoId();
        var modId = new MongoId();

        var src = new TemplateSide
        {
            Character = new PmcData
            {
                Inventory = new BotBaseInventory { Stash = stashId, Items = new List<Item>() }
            }
        };
        var tgt = new TemplateSide
        {
            Character = new PmcData
            {
                Inventory = new BotBaseInventory
                {
                    Stash = stashId,
                    Items = new List<Item>
                    {
                        new Item { Id = rootId, Template = new MongoId("aaaaaaaaaaaaaaaa00000000"), ParentId = stashId.ToString(), SlotId = "hideout" },
                        new Item { Id = modId, Template = new MongoId("bbbbbbbbbbbbbbbb00000000"), ParentId = rootId.ToString(), SlotId = "mod_handguard" }
                    }
                }
            }
        };
        var pmc = new PmcData { Inventory = new BotBaseInventory { Stash = new MongoId(), Items = new List<Item>() } };

        var bundles = EditionUpgradeService.ComputeItemBundleDiff(src, tgt, pmc);

        Assert.AreEqual(1, bundles.Count);
        Assert.AreEqual(2, bundles[0].Items.Count);

        // 子物品的 ParentId 应被重写到新分配的根 id
        var newRoot = bundles[0].Items[0];
        var newChild = bundles[0].Items[1];
        Assert.AreEqual(newRoot.Id.ToString(), newChild.ParentId);
        Assert.AreNotEqual(rootId, newRoot.Id, "捆绑根物品 id 应重新分配以避免与玩家档冲突");
    }

    // ---------- ComputeHideoutStashDiff ----------

    [Test]
    public void ComputeHideoutStashDiff_TargetExclusiveAreaIsAdded()
    {
        var src = new TemplateSide { Character = new PmcData { Inventory = new BotBaseInventory { HideoutAreaStashes = new Dictionary<string, MongoId>() } } };
        var tgt = new TemplateSide { Character = new PmcData { Inventory = new BotBaseInventory { HideoutAreaStashes = new Dictionary<string, MongoId> { ["27"] = new MongoId() } } } };
        var pmc = new PmcData { Inventory = new BotBaseInventory { HideoutAreaStashes = new Dictionary<string, MongoId>() } };

        var diff = EditionUpgradeService.ComputeHideoutStashDiff(src, tgt, pmc);

        Assert.AreEqual(1, diff.Count);
        Assert.IsTrue(diff.ContainsKey("27"));
    }

    [Test]
    public void ComputeHideoutStashDiff_PlayerAlreadyHasAreaIsSkipped()
    {
        var existing = new MongoId();
        var src = new TemplateSide { Character = new PmcData { Inventory = new BotBaseInventory { HideoutAreaStashes = new Dictionary<string, MongoId>() } } };
        var tgt = new TemplateSide { Character = new PmcData { Inventory = new BotBaseInventory { HideoutAreaStashes = new Dictionary<string, MongoId> { ["27"] = new MongoId() } } } };
        var pmc = new PmcData { Inventory = new BotBaseInventory { HideoutAreaStashes = new Dictionary<string, MongoId> { ["27"] = existing } } };

        var diff = EditionUpgradeService.ComputeHideoutStashDiff(src, tgt, pmc);

        Assert.AreEqual(0, diff.Count, "玩家已有该区域不应被覆盖");
    }

    // ---------- ComputeDogTagDiff ----------

    [Test]
    public void ComputeDogTagDiff_TplChangeIsDetected()
    {
        var srcTpl = new MongoId();
        var tgtTpl = new MongoId();
        var src = new TemplateSide { Character = new PmcData { Customization = new BotCustomization { DogTag = srcTpl } } };
        var tgt = new TemplateSide { Character = new PmcData { Customization = new BotCustomization { DogTag = tgtTpl } } };
        var pmc = new PmcData { Customization = new BotCustomization { DogTag = srcTpl } };

        var change = EditionUpgradeService.ComputeDogTagDiff(src, tgt, pmc);

        Assert.IsNotNull(change);
        Assert.AreEqual(tgtTpl, change!.NewTemplate);
    }

    [Test]
    public void ComputeDogTagDiff_PlayerAlreadyOnTargetTplReturnsNull()
    {
        var srcTpl = new MongoId();
        var tgtTpl = new MongoId();
        var src = new TemplateSide { Character = new PmcData { Customization = new BotCustomization { DogTag = srcTpl } } };
        var tgt = new TemplateSide { Character = new PmcData { Customization = new BotCustomization { DogTag = tgtTpl } } };
        var pmc = new PmcData { Customization = new BotCustomization { DogTag = tgtTpl } };

        var change = EditionUpgradeService.ComputeDogTagDiff(src, tgt, pmc);

        Assert.IsNull(change);
    }

    // ---------- ComputeTraderUpgrades ----------

    [Test]
    public void ComputeTraderUpgrades_OnlyUpsLoyaltyWhenTargetIsHigher()
    {
        var traderId = new MongoId();
        var src = new TemplateSide
        {
            Trader = new ProfileTraderTemplate { InitialLoyaltyLevel = new Dictionary<MongoId, int?> { [traderId] = 1 } }
        };
        var tgt = new TemplateSide
        {
            Trader = new ProfileTraderTemplate { InitialLoyaltyLevel = new Dictionary<MongoId, int?> { [traderId] = 3 } }
        };
        var pmc = new PmcData { TradersInfo = new Dictionary<MongoId, TraderInfo> { [traderId] = new TraderInfo { LoyaltyLevel = 2 } } };

        var ups = EditionUpgradeService.ComputeTraderUpgrades(src, tgt, pmc);

        Assert.AreEqual(1, ups.Count);
        Assert.AreEqual(3, ups[0].NewLoyaltyLevel);
    }

    [Test]
    public void ComputeTraderUpgrades_DoesNotDowngradePlayerHigherThanTemplate()
    {
        var traderId = new MongoId();
        var src = new TemplateSide
        {
            Trader = new ProfileTraderTemplate { InitialLoyaltyLevel = new Dictionary<MongoId, int?> { [traderId] = 1 } }
        };
        var tgt = new TemplateSide
        {
            Trader = new ProfileTraderTemplate { InitialLoyaltyLevel = new Dictionary<MongoId, int?> { [traderId] = 3 } }
        };
        var pmc = new PmcData { TradersInfo = new Dictionary<MongoId, TraderInfo> { [traderId] = new TraderInfo { LoyaltyLevel = 4 } } };

        var ups = EditionUpgradeService.ComputeTraderUpgrades(src, tgt, pmc);

        Assert.AreEqual(0, ups.Count, "玩家自己已刷到更高等级则不再覆盖");
    }

    // ---------- helpers ----------

    private static Item NewItem(string tplHex24, double? stack = null, double? durability = null)
    {
        return new Item
        {
            Id = new MongoId(),
            Template = new MongoId(tplHex24),
            Upd = (stack.HasValue || durability.HasValue) ? new Upd
            {
                StackObjectsCount = stack,
                Repairable = durability.HasValue ? new UpdRepairable { Durability = durability, MaxDurability = durability } : null
            } : null,
        };
    }

    private static (TemplateSide src, TemplateSide tgt, PmcData pmc) BuildSidesWithStash(
        string[] sourceRoots,
        string[] targetRoots,
        string[] playerOwnedRoots)
    {
        var sourceStashId = new MongoId();
        var targetStashId = new MongoId();
        var playerStashId = new MongoId();

        Item ToRoot(string tpl, MongoId stash) => new()
        {
            Id = new MongoId(),
            Template = new MongoId(tpl),
            ParentId = stash.ToString(),
            SlotId = "hideout",
        };

        var src = new TemplateSide
        {
            Character = new PmcData
            {
                Inventory = new BotBaseInventory
                {
                    Stash = sourceStashId,
                    Items = sourceRoots.Select(t => ToRoot(t, sourceStashId)).ToList(),
                }
            }
        };

        var tgt = new TemplateSide
        {
            Character = new PmcData
            {
                Inventory = new BotBaseInventory
                {
                    Stash = targetStashId,
                    Items = targetRoots.Select(t => ToRoot(t, targetStashId)).ToList(),
                }
            }
        };

        var pmc = new PmcData
        {
            Inventory = new BotBaseInventory
            {
                Stash = playerStashId,
                Items = playerOwnedRoots.Select(t => ToRoot(t, playerStashId)).ToList(),
            }
        };

        return (src, tgt, pmc);
    }
}
