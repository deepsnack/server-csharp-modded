using NUnit.Framework;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;

namespace UnitTests.Tests.Controllers;

[TestFixture]
public class AchievementControllerTests
{
    [Test]
    public void CalculateAchievementPercentagesAggregatesProfilesInOnePass()
    {
        var first = new MongoId("111111111111111111111111");
        var second = new MongoId("222222222222222222222222");
        var third = new MongoId("333333333333333333333333");
        IReadOnlySet<MongoId>[] profiles =
        [
            new HashSet<MongoId> { first, second },
            new HashSet<MongoId> { first },
            new HashSet<MongoId> { third },
        ];

        var result = AchievementController.CalculateAchievementPercentages([first, second, third], profiles);

        Assert.Multiple(() =>
        {
            Assert.That(result[first.ToString()], Is.EqualTo(67));
            Assert.That(result[second.ToString()], Is.EqualTo(33));
            Assert.That(result[third.ToString()], Is.EqualTo(33));
        });
    }

    [Test]
    public void CalculateAchievementPercentagesReturnsZeroWithoutProfiles()
    {
        var achievementId = new MongoId("444444444444444444444444");

        var result = AchievementController.CalculateAchievementPercentages(
            [achievementId],
            Array.Empty<IReadOnlySet<MongoId>>()
        );

        Assert.That(result[achievementId.ToString()], Is.Zero);
    }
}
