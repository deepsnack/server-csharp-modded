using System.Text.Json;
using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.BattlePass.Administration;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
[NonParallelizable]
public class TitleChangeHandlerTests
{
    [Test]
    public void UpsertTitle_WritesCatalogThroughHandler()
    {
        WithTempStore(() =>
        {
            var handler = new TitleChangeHandler();
            var input = JsonSerializer.SerializeToElement(new
            {
                id = "veteran",
                name = "老兵",
                type = "text",
                text = "VETERAN",
                color = "#E8B923",
            });

            var normalized = handler.Normalize("title.upsert", input);
            Assert.That(handler.Validate("title.upsert", normalized), Is.Null);

            handler.ApplyAndActivate("title.upsert", normalized, expectedBaseRevision: null, changeId: null);

            var saved = BattlePassStore.GetTitleCatalog().Single(title => title.Id == "veteran");
            Assert.That(saved.Name, Is.EqualTo("老兵"));
        });
    }

    [Test]
    public void GrantTitle_RestoreSnapshot_RevertsPlayerTitles()
    {
        WithTempStore(() =>
        {
            BattlePassStore.SaveTitleCatalog([new BpTitle { Id = "veteran", Name = "老兵" }]);
            var handler = new TitleChangeHandler();
            var profileId = "profile-" + Guid.NewGuid().ToString("N");
            var targetKey = $"titles:player:{profileId}";
            var before = handler.GetCurrentSnapshot(targetKey);
            var input = JsonSerializer.SerializeToElement(new { profileId, titleId = "veteran" });
            var normalized = handler.Normalize("title.grant", input);

            var resultRevision = handler.ApplyAndActivate("title.grant", normalized, expectedBaseRevision: null, changeId: null);
            Assert.That(BattlePassStore.GetPlayerTitles(profileId).Owned, Does.Contain("veteran"));

            handler.RestoreSnapshot(targetKey, before, resultRevision, auditId: null);

            Assert.That(BattlePassStore.GetPlayerTitles(profileId).Owned, Does.Not.Contain("veteran"));
        });
    }

    private static void WithTempStore(Action action)
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "spt-bp-title-handler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Directory.SetCurrentDirectory(tempDirectory);
            action();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
