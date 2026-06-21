using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class BattlePassServiceTests
{
    [TestCase("headless_123", true)]
    [TestCase("HEADLESS_server", true)]
    [TestCase("player_headless_123", false)]
    [TestCase("normal_player", false)]
    [TestCase(null, false)]
    public void IsHeadlessUsername_UsesFikaProfilePrefix(string? username, bool expected)
    {
        Assert.That(BattlePassService.IsHeadlessUsername(username), Is.EqualTo(expected));
    }
}
