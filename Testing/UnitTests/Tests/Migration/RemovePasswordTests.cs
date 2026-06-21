using System.Text.Json.Nodes;
using NUnit.Framework;
using SPTarkov.Server.Core.Migration;
using SPTarkov.Server.Core.Migration.Migrations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.Migration;

[TestFixture]
public class RemovePasswordTests
{
    private const string ProfileId = "0123456789abcdef01234567";
    private const string PasswordHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private string tempDirectory = null!;
    private string passwordFile = null!;

    [SetUp]
    public void SetUp()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"spt-password-migration-{Guid.NewGuid():N}");
        passwordFile = Path.Combine(tempDirectory, "passwords.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, true);
        }
    }

    [Test]
    public void CanMigrate_ProfileHasPassword_ReturnsTrue()
    {
        var migration = new RemovePassword(new PasswordStoreService(passwordFile));

        Assert.IsTrue(migration.CanMigrate(CreateProfile(), Array.Empty<IProfileMigration>()));
    }

    [Test]
    public void Migrate_PersistsHashBeforeRemovingPassword()
    {
        var passwordStore = new PasswordStoreService(passwordFile);
        var migration = new RemovePassword(passwordStore);

        var result = migration.Migrate(CreateProfile());

        Assert.IsNotNull(result);
        Assert.IsNull(result!["info"]?["password"]);
        Assert.AreEqual(PasswordHash, new PasswordStoreService(passwordFile).GetHash(new MongoId(ProfileId)));
    }

    [Test]
    public void Migrate_DoesNotOverwriteAuthoritativePasswordStoreEntry()
    {
        var passwordStore = new PasswordStoreService(passwordFile);
        Assert.IsTrue(passwordStore.SetPassword(new MongoId(ProfileId), "new-password"));
        var migration = new RemovePassword(passwordStore);

        var result = migration.Migrate(CreateProfile());

        Assert.IsNotNull(result);
        Assert.IsTrue(passwordStore.Verify(new MongoId(ProfileId), "new-password"));
    }

    private static JsonObject CreateProfile()
    {
        return new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["id"] = ProfileId,
                ["password"] = PasswordHash,
            },
        };
    }
}
