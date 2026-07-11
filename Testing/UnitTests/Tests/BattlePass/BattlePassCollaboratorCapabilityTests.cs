using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass.Administration;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class BattlePassCollaboratorCapabilityTests
{
    [Test]
    public void LegacyTaskAndTraderGrant_ReceivesQuestCapabilities()
    {
        var caps = BattlePassCollaboratorGrantPolicy.NormalizeCapabilitySet([
            "tasks.read",
            "tasks.submit",
            "trader.read",
            "trader.submit",
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(caps.Contains("quests.read"), Is.True);
            Assert.That(caps.Contains("quests.submit"), Is.True);
        });
    }

    [Test]
    public void SingleLegacyModule_DoesNotReceiveQuestCapabilities()
    {
        var caps = BattlePassCollaboratorGrantPolicy.NormalizeCapabilitySet([
            "tasks.read",
            "tasks.submit",
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(caps.Contains("quests.read"), Is.False);
            Assert.That(caps.Contains("quests.submit"), Is.False);
        });
    }

    [Test]
    public void DefaultCapabilities_IncludeQuestManagement()
    {
        var caps = BattlePassCollaboratorGrantPolicy.DefaultCapabilities();

        Assert.Multiple(() =>
        {
            Assert.That(caps, Does.Contain("quests.read"));
            Assert.That(caps, Does.Contain("quests.submit"));
        });
    }
}
