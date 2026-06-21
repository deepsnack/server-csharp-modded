using System.Text.Json;
using NUnit.Framework;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.Services;

[TestFixture]
public class PasswordStoreServiceTests
{
    private static readonly MongoId ProfileId = new("0123456789abcdef01234567");
    private string tempDirectory = null!;
    private string passwordFile = null!;

    [SetUp]
    public void SetUp()
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), $"spt-password-store-{Guid.NewGuid():N}");
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
    public void SetPassword_PersistsAndCanBeVerifiedAfterReload()
    {
        var passwordStore = new PasswordStoreService(passwordFile);

        Assert.IsTrue(passwordStore.SetPassword(ProfileId, "secret"));

        var reloadedStore = new PasswordStoreService(passwordFile);
        Assert.IsTrue(reloadedStore.Verify(ProfileId, "secret"));
        Assert.IsFalse(reloadedStore.Verify(ProfileId, "wrong"));
    }

    [Test]
    public void GetHash_RecoversLegacyBackupWhenActiveFileIsMissing()
    {
        Directory.CreateDirectory(tempDirectory);
        var expectedHash = PasswordStoreService.Hash("legacy-secret");
        File.WriteAllText($"{passwordFile}.bak-merge", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [ProfileId.ToString()] = expectedHash,
        }));

        var passwordStore = new PasswordStoreService(passwordFile);

        Assert.AreEqual(expectedHash, passwordStore.GetHash(ProfileId));
        Assert.IsTrue(File.Exists(passwordFile));
    }

    [Test]
    public void ImportLegacyHash_ReplacesProvisionalBackupEntryOnce()
    {
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText($"{passwordFile}.bak-merge", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [ProfileId.ToString()] = PasswordStoreService.Hash("old-secret"),
        }));
        var passwordStore = new PasswordStoreService(passwordFile);
        _ = passwordStore.GetHash(ProfileId);

        Assert.IsTrue(passwordStore.ImportLegacyHash(ProfileId, PasswordStoreService.Hash("profile-secret")));

        Assert.IsTrue(passwordStore.Verify(ProfileId, "profile-secret"));
    }

    [Test]
    public void Remove_PersistsCredentialDeletion()
    {
        var passwordStore = new PasswordStoreService(passwordFile);
        Assert.IsTrue(passwordStore.SetPassword(ProfileId, "secret"));

        Assert.IsTrue(passwordStore.Remove(ProfileId));

        Assert.IsNull(new PasswordStoreService(passwordFile).GetHash(ProfileId));
    }

    [Test]
    public void SetPassword_DoesNotOverwriteCorruptedCredentialFile()
    {
        Directory.CreateDirectory(tempDirectory);
        const string corruptedContent = "{not-json";
        File.WriteAllText(passwordFile, corruptedContent);
        var passwordStore = new PasswordStoreService(passwordFile);

        Assert.IsFalse(passwordStore.SetPassword(ProfileId, "secret"));

        Assert.AreEqual(corruptedContent, File.ReadAllText(passwordFile));
    }
}
