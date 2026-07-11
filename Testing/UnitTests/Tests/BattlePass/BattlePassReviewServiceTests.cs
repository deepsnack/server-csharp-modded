using System.Text.Json;
using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass.Administration;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
[NonParallelizable]
public class BattlePassReviewServiceTests
{
    private BattlePassReviewService _reviewService;
    private BattlePassChangeStore _changeStore;

    [OneTimeSetUp]
    public void Initialize()
    {
        _reviewService = DI.GetInstance().GetService<BattlePassReviewService>();
        _changeStore = DI.GetInstance().GetService<BattlePassChangeStore>();
    }

    [Test]
    public void ItemOverrideDelete_SubmitAndApprove_PreservesObjectPayload()
    {
        WithTemporaryStore(() =>
        {
            var handler = new StrictItemDeleteHandler();
            handler.Add("override-1");
            _reviewService.RegisterHandler(handler);

            using var input = JsonDocument.Parse("""{ "id": "override-1" }""");
            var submitted = _reviewService.Submit(
                Collaborator("items.submit"),
                handler.Module,
                "item.override.delete",
                input.RootElement);

            Assert.That(submitted.ok, Is.True, submitted.result);
            var stored = _changeStore.GetChange(submitted.result);
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.ProposedPayload, Is.TypeOf<JsonElement>());
            var payload = (JsonElement) stored.ProposedPayload!;
            Assert.Multiple(() =>
            {
                Assert.That(payload.ValueKind, Is.EqualTo(JsonValueKind.Object));
                Assert.That(payload.GetProperty("id").GetString(), Is.EqualTo("override-1"));
            });

            var approved = _reviewService.Approve(Admin(), submitted.result, expectedVersion: stored.Version);
            Assert.Multiple(() =>
            {
                Assert.That(approved.Ok, Is.True, approved.Message);
                Assert.That(approved.Status, Is.EqualTo("applied"));
                Assert.That(handler.Contains("override-1"), Is.False);
            });
        });
    }

    [Test]
    public void LegacyNullDeletePayload_IsRebuiltAndKnownFailureReturnsToPending()
    {
        WithTemporaryStore(() =>
        {
            _changeStore.SaveChange(new BpChangeRequest
            {
                SchemaVersion = 1,
                Version = 0,
                Id = "legacy-null-delete",
                Module = "items",
                CommandType = "item.override.delete",
                Operation = "delete",
                TargetId = "override-legacy",
                TargetDisplayName = "override-legacy",
                TargetKey = "items:override:override-legacy",
                Status = "failed",
                FailureCode = "InvalidOperationException",
                FailureMessage = "The requested operation requires an element of type 'Object', but the target element has type 'Null'.",
            });

            _ = _changeStore.GetChange("legacy-null-delete");
            var migrated = _changeStore.GetChange("legacy-null-delete");

            Assert.That(migrated, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(migrated!.SchemaVersion, Is.EqualTo(2));
                Assert.That(migrated.Version, Is.EqualTo(2));
                Assert.That(migrated.Status, Is.EqualTo("pending"));
                Assert.That(migrated.FailureMessage, Is.Null);
                Assert.That(migrated.History.Any(item => item.Type == "recover"), Is.True);
            });

            var payload = (JsonElement) migrated!.ProposedPayload!;
            Assert.That(payload.GetProperty("id").GetString(), Is.EqualTo("override-legacy"));
        });
    }

    [Test]
    public void ApprovedAudit_RollbackRestoresBeforeSnapshotAndCannotRepeat()
    {
        WithTemporaryStore(() =>
        {
            var handler = new StrictItemDeleteHandler();
            handler.Add("override-rollback");
            _reviewService.RegisterHandler(handler);

            using var input = JsonDocument.Parse("""{ "id": "override-rollback" }""");
            var submitted = _reviewService.Submit(
                Collaborator("items.submit"),
                handler.Module,
                "item.override.delete",
                input.RootElement);
            var approved = _reviewService.Approve(Admin(), submitted.result);
            Assert.That(approved.Ok, Is.True, approved.Message);
            Assert.That(handler.Contains("override-rollback"), Is.False);

            var audit = _changeStore.QueryAudit(eventType: "approve").Single(item => item.ChangeId == submitted.result);
            Assert.Multiple(() =>
            {
                Assert.That(audit.Reversible, Is.True);
                Assert.That(audit.RolledBack, Is.False);
                Assert.That(audit.BeforePayload, Is.Not.Null);
            });

            var rolledBack = _reviewService.RollbackAudit(Admin(), audit.Id, "自动化回归");
            Assert.Multiple(() =>
            {
                Assert.That(rolledBack.Ok, Is.True, rolledBack.Message);
                Assert.That(handler.Contains("override-rollback"), Is.True);
                Assert.That(_changeStore.GetAudit(audit.Id)!.RolledBack, Is.True);
                Assert.That(_changeStore.QueryAudit(eventType: "rollback").Any(item => item.RollbackOfAuditId == audit.Id), Is.True);
            });

            var duplicate = _reviewService.RollbackAudit(Admin(), audit.Id);
            Assert.That(duplicate.Ok, Is.False);
        });
    }

    [Test]
    public void ApprovedAudit_RollbackRejectsLaterTargetMutation()
    {
        WithTemporaryStore(() =>
        {
            var handler = new StrictItemDeleteHandler();
            handler.Add("override-conflict");
            _reviewService.RegisterHandler(handler);

            using var input = JsonDocument.Parse("""{ "id": "override-conflict" }""");
            var submitted = _reviewService.Submit(Collaborator("items.submit"), handler.Module, "item.override.delete", input.RootElement);
            Assert.That(_reviewService.Approve(Admin(), submitted.result).Ok, Is.True);
            var audit = _changeStore.QueryAudit(eventType: "approve").Single(item => item.ChangeId == submitted.result);

            handler.Add("override-conflict"); // 模拟批准后由其它操作再次修改同一目标
            var result = _reviewService.RollbackAudit(Admin(), audit.Id);

            Assert.Multiple(() =>
            {
                Assert.That(result.Ok, Is.False);
                Assert.That(result.Message, Does.Contain("冲突"));
                Assert.That(_changeStore.GetAudit(audit.Id)!.RolledBack, Is.False);
            });
        });
    }

    private static BattlePassAdminPrincipal Collaborator(params string[] capabilities) => new()
    {
        ActorType = "collaborator",
        ActorId = "profile-1",
        DisplayName = "协管测试",
        Capabilities = new HashSet<string>(capabilities, StringComparer.OrdinalIgnoreCase),
    };

    private static BattlePassAdminPrincipal Admin() => new()
    {
        ActorType = "admin",
        ActorId = "admin",
        DisplayName = "管理员测试",
    };

    private static void WithTemporaryStore(Action assertion)
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "spt-bp-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Directory.SetCurrentDirectory(tempDirectory);
            assertion();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Mirrors the real item deletion handler's strict object input contract without running ItemControlSync
    /// against the process-wide item database during this persistence-chain regression test.
    /// </summary>
    private sealed class StrictItemDeleteHandler : IBattlePassChangeHandler
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

        public string Module => "items-delete-review-regression";
        public IReadOnlyList<string> CommandTypes { get; } = ["item.override.delete"];
        public string RequiredCapability => "items.submit";

        public void Add(string id) => _ids.Add(id);
        public bool Contains(string id) => _ids.Contains(id);

        public object Normalize(string commandType, JsonElement input)
        {
            var id = input.TryGetProperty("id", out var property) ? property.GetString()?.Trim() : null;
            return id ?? "";
        }

        public string? Validate(string commandType, object normalizedInput) =>
            string.IsNullOrWhiteSpace((string) normalizedInput) ? "缺少 id" : null;

        public string Describe(string commandType, object normalizedInput, object? currentState) =>
            $"撤销获取途径编辑 id={normalizedInput}";

        public string GetTargetKey(string commandType, object normalizedInput) => $"items:override:{normalizedInput}";
        public string GetTargetDisplayName(string commandType, object normalizedInput) => (string) normalizedInput;
        public object? GetCurrentSnapshot(string targetKey) => _ids.Contains(TargetId(targetKey)) ? TargetId(targetKey) : null;
        public string GetRevision(object? snapshot) => snapshot is null ? "" : $"present:{snapshot}";

        public string ApplyAndActivate(string commandType, object normalizedInput, string? expectedBaseRevision, string? changeId)
        {
            _ids.Remove((string) normalizedInput);
            return "";
        }

        public string RestoreSnapshot(string targetKey, object? beforeSnapshot, string expectedCurrentRevision, string? auditId)
        {
            var currentRevision = GetRevision(GetCurrentSnapshot(targetKey));
            if (!string.Equals(currentRevision, expectedCurrentRevision, StringComparison.Ordinal))
                throw new ChangeConflictException($"expected={expectedCurrentRevision}, actual={currentRevision}");

            var id = TargetId(targetKey);
            if (beforeSnapshot is null)
            {
                _ids.Remove(id);
            }
            else
            {
                var restoredId = beforeSnapshot is JsonElement element ? element.GetString() : beforeSnapshot.ToString();
                _ids.Add(restoredId ?? id);
            }

            return GetRevision(GetCurrentSnapshot(targetKey));
        }

        private static string TargetId(string targetKey) => targetKey.Split(':', 3)[2];
    }
}
