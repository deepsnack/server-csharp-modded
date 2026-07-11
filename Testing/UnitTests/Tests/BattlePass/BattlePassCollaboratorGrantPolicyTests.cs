using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass.Administration;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
public class BattlePassCollaboratorGrantPolicyTests
{
    [Test]
    public void Find_NormalizesWhitespaceAndCase()
    {
        var grant = new BpCollaboratorGrant { ProfileId = "  ABC123  ", Enabled = true };

        var found = BattlePassCollaboratorGrantPolicy.Find([grant], "abc123", requireEnabled: true);

        Assert.That(found, Is.SameAs(grant));
    }

    [Test]
    public void Find_RejectsDisabledGrantWhenEnabledIsRequired()
    {
        var grant = new BpCollaboratorGrant { ProfileId = "abc123", Enabled = false };

        var found = BattlePassCollaboratorGrantPolicy.Find([grant], "abc123", requireEnabled: true);

        Assert.That(found, Is.Null);
    }

    [Test]
    public void RemoveAll_PermanentlyRemovesDuplicateHistoricalRecords()
    {
        var grants = new List<BpCollaboratorGrant>
        {
            new() { ProfileId = "abc123", Enabled = true },
            new() { ProfileId = " ABC123 ", Enabled = false },
            new() { ProfileId = "other", Enabled = true },
        };

        var removed = BattlePassCollaboratorGrantPolicy.RemoveAll(grants, "AbC123");

        Assert.That(removed, Is.EqualTo(2));
        Assert.That(grants.Select(g => g.ProfileId), Is.EqualTo(new[] { "other" }));
    }
}
