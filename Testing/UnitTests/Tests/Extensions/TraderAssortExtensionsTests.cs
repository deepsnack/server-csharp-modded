using NUnit.Framework;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace UnitTests.Tests.Extensions;

[TestFixture]
public class TraderAssortExtensionsTests
{
    [Test]
    public void RemoveItemsFromAssortRemovesOfferContainingBlockedModItem()
    {
        var blockedModTpl = new MongoId("888888888888888888888888");
        var blockedRootId = new MongoId("999999999999999999999999");
        var blockedChildId = new MongoId("aaaaaaaaaaaaaaaaaaaaaaaa");
        var allowedRootId = new MongoId("bbbbbbbbbbbbbbbbbbbbbbbb");
        var assort = new TraderAssort
        {
            Items =
            [
                new Item
                {
                    Id = blockedRootId,
                    Template = new MongoId("cccccccccccccccccccccccc"),
                    ParentId = "hideout",
                    SlotId = "hideout",
                },
                new Item
                {
                    Id = blockedChildId,
                    Template = blockedModTpl,
                    ParentId = blockedRootId.ToString(),
                    SlotId = "mod_scope",
                },
                new Item
                {
                    Id = allowedRootId,
                    Template = new MongoId("dddddddddddddddddddddddd"),
                    ParentId = "hideout",
                    SlotId = "hideout",
                },
            ],
            BarterScheme = new Dictionary<MongoId, List<List<BarterScheme>>>
            {
                [blockedRootId] = [],
                [allowedRootId] = [],
            },
            LoyalLevelItems = new Dictionary<MongoId, int>
            {
                [blockedRootId] = 1,
                [allowedRootId] = 1,
            },
        };

        assort.RemoveItemsFromAssort(new HashSet<MongoId> { blockedModTpl });

        Assert.Multiple(() =>
        {
            Assert.That(assort.Items.Select(item => item.Id), Is.EqualTo(new[] { allowedRootId }));
            Assert.That(assort.BarterScheme, Does.Not.ContainKey(blockedRootId));
            Assert.That(assort.LoyalLevelItems, Does.Not.ContainKey(blockedRootId));
            Assert.That(assort.BarterScheme, Does.ContainKey(allowedRootId));
            Assert.That(assort.LoyalLevelItems, Does.ContainKey(allowedRootId));
        });
    }
}
