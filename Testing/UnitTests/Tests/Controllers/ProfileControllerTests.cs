using NUnit.Framework;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Profile;

namespace UnitTests.Tests.Controllers;

[TestFixture]
public class ProfileControllerTests
{
    private ProfileController _sut;
    private ProfileHelper _profileHelper;

    [SetUp]
    public void Setup()
    {
        _sut = DI.GetInstance().GetService<ProfileController>();
        _profileHelper = DI.GetInstance().GetService<ProfileHelper>();
    }

    [Test]
    public void GetMiniProfileFromHeaderProjectsActiveProfileWithoutFullProfile()
    {
        var profileId = new MongoId("555555555555555555555555");
        var sptData = new Spt { Version = "4.0.13" };
        var header = new LazyProfileHeader
        {
            ProfileInfo = CreateProfileInfo(profileId),
            Nickname = "HeaderUser",
            Side = "Usec",
            Level = 12,
            Experience = 12345,
            SptData = sptData,
            FilePath = "unused.json",
        };

        var result = _sut.GetMiniProfileFromHeader(profileId, header);

        Assert.Multiple(() =>
        {
            Assert.That(result.ProfileId, Is.EqualTo(profileId.ToString()));
            Assert.That(result.Username, Is.EqualTo("account"));
            Assert.That(result.Nickname, Is.EqualTo("HeaderUser"));
            Assert.That(result.Side, Is.EqualTo("Usec"));
            Assert.That(result.CurrentLevel, Is.EqualTo(12));
            Assert.That(result.CurrentExperience, Is.EqualTo(12345));
            Assert.That(result.PreviousExperience, Is.EqualTo(_profileHelper.GetExperience(12)));
            Assert.That(result.NextLevel, Is.EqualTo(_profileHelper.GetExperience(13)));
            Assert.That(result.SptData, Is.SameAs(sptData));
        });
    }

    [Test]
    public void GetMiniProfileFromHeaderUsesLauncherDefaultsForEmptyProfile()
    {
        var profileId = new MongoId("666666666666666666666666");
        var header = new LazyProfileHeader { ProfileInfo = CreateProfileInfo(profileId), FilePath = "unused.json" };

        var result = _sut.GetMiniProfileFromHeader(profileId, header);

        Assert.Multiple(() =>
        {
            Assert.That(result.ProfileId, Is.EqualTo(profileId.ToString()));
            Assert.That(result.Nickname, Is.EqualTo("unknown"));
            Assert.That(result.Side, Is.EqualTo("unknown"));
            Assert.That(result.CurrentLevel, Is.Zero);
            Assert.That(result.CurrentExperience, Is.Zero);
            Assert.That(result.SptData, Is.Not.Null);
        });
    }

    private static Info CreateProfileInfo(MongoId profileId)
    {
        return new Info
        {
            ProfileId = profileId,
            Username = "account",
            Edition = "Standard",
            IsWiped = false,
        };
    }
}
