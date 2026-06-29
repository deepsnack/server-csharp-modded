using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class BattlePassServiceTests
{
    private BattlePassService _service;

    [OneTimeSetUp]
    public void Initialize()
    {
        _service = DI.GetInstance().GetService<BattlePassService>();
    }

    [TestCase("headless_123", true)]
    [TestCase("HEADLESS_server", true)]
    [TestCase("player_headless_123", false)]
    [TestCase("normal_player", false)]
    [TestCase(null, false)]
    public void IsHeadlessUsername_UsesFikaProfilePrefix(string? username, bool expected)
    {
        Assert.That(BattlePassService.IsHeadlessUsername(username), Is.EqualTo(expected));
    }

    [TestCase("xp", 400)]
    [TestCase("items", 0)]
    [TestCase("both", 400)]
    [TestCase("invalid-mode", 400)]
    [TestCase("", 400)]
    public void CreditTaskCompletion_AppliesConfiguredRewardMode(string rewardMode, int expectedXp)
    {
        var progress = new BpProgress();
        var season = new BpSeason { MaxLevel = 50, BaseXp = 1000, PremiumXpMultiplier = 1.0 };
        var template = new BpTaskTemplate { Title = "test", Xp = 400, RewardMode = rewardMode };

        var gained = _service.CreditTaskCompletion("000000000000000000000000", progress, season, template);

        Assert.That(gained, Is.EqualTo(expectedXp));
        Assert.That(progress.Xp, Is.EqualTo(expectedXp));
    }

    [Test]
    public void CreditTaskCompletion_AppliesPremiumMultiplier()
    {
        var progress = new BpProgress { PremiumUnlocked = true };
        var season = new BpSeason { MaxLevel = 50, BaseXp = 1000, PremiumXpMultiplier = 1.5 };
        var template = new BpTaskTemplate { Xp = 200, RewardMode = "xp" };

        var gained = _service.CreditTaskCompletion("000000000000000000000000", progress, season, template);

        Assert.That(gained, Is.EqualTo(300));
        Assert.That(progress.Xp, Is.EqualTo(300));
    }
}
