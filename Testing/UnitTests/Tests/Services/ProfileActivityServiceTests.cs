using NUnit.Framework;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace UnitTests.Tests.Services;

[TestFixture]
public class ProfileActivityServiceTests
{
    [Test]
    public void IsClientRecentlyActive_UsesActivityWindow()
    {
        var service = new ProfileActivityService(new TimeUtil());
        var sessionId = new MongoId();

        service.AddActiveProfile(sessionId, 1);

        Assert.Multiple(() =>
        {
            Assert.That(service.IsClientRecentlyActive(sessionId), Is.True);
            Assert.That(service.IsClientRecentlyActive(sessionId, 0), Is.False);
        });
    }

    [Test]
    public void RemoveActiveProfile_ClearsOnlineAndRaidState()
    {
        var service = new ProfileActivityService(new TimeUtil());
        var sessionId = new MongoId();

        service.AddActiveProfile(sessionId, 1);
        service.SetRaidActive(sessionId, true);
        service.RemoveActiveProfile(sessionId);

        Assert.Multiple(() =>
        {
            Assert.That(service.ContainsActiveProfile(sessionId), Is.False);
            Assert.That(service.IsClientRecentlyActive(sessionId), Is.False);
            Assert.That(service.IsRaidActive(sessionId), Is.False);
        });
    }
}
