using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
[NonParallelizable]
public class BattlePassTrackServiceTests
{
    private BattlePassTrackService _trackService;
    private DatabaseService _databaseService;

    [OneTimeSetUp]
    public void Initialize()
    {
        _trackService = DI.GetInstance().GetService<BattlePassTrackService>();
        _databaseService = DI.GetInstance().GetService<DatabaseService>();
    }

    [Test]
    public void KillMatches_ResolvesSelectedCaliberFromWeaponTemplate()
    {
        var weapon = _databaseService
            .GetItems()
            .First(pair => !string.IsNullOrWhiteSpace(pair.Value.Properties?.Caliber));
        var caliber = weapon.Value.Properties.Caliber!;
        var task = new BpTaskTemplate { Target = "Any", WeaponCalibers = [caliber] };
        var kill = new BpKillEvent { Weapon = weapon.Key.ToString() };

        Assert.That(_trackService.KillMatches(task, kill), Is.True);

        task.WeaponCalibers = ["CaliberThatDoesNotExist"];
        Assert.That(_trackService.KillMatches(task, kill), Is.False);
    }

    [Test]
    public void KillMatches_UnavailableEquipmentFiltersAreNoOp()
    {
        // 敌我装备无法从战绩判定，KillMatches 不消费（靠 Validate 在创建时拦截），存在时不影响匹配。
        var task = new BpTaskTemplate
        {
            Target = "Any",
            EnemyEquipment = ["legacy-enemy-equipment"],
            PlayerEquipment = ["legacy-player-equipment"],
        };

        Assert.That(_trackService.KillMatches(task, new BpKillEvent()), Is.True);
    }

    [Test]
    public void KillMatches_WeaponModsRequireAllModsPresentFromClientReport()
    {
        // 配件 inclusive：须装齐全部指定配件才计数（仿"试驾"）。
        var task = new BpTaskTemplate { Target = "Any", WeaponMods = ["mod-a", "mod-b"] };

        // 装齐 → 命中（多带无关配件不影响）
        Assert.That(
            _trackService.KillMatches(task, new BpKillEvent { WeaponMods = ["mod-a", "mod-b", "mod-c"] }),
            Is.True
        );

        // 缺一 → 不命中
        Assert.That(_trackService.KillMatches(task, new BpKillEvent { WeaponMods = ["mod-a"] }), Is.False);

        // 无配件信息（如普通击杀 / 服务端权威链路）→ 不命中
        Assert.That(_trackService.KillMatches(task, new BpKillEvent()), Is.False);
    }

    [Test]
    public void SupplementalSpecificWeaponTask_ClientSnapshotsAdvanceWithoutAuthoritativeDoubleCount()
    {
        const string weaponTpl = "weapon-a";
        WithTaskStore(
            new BpTaskTemplate
            {
                Id = "specific_weapon_kills",
                Scope = "daily",
                ConditionType = "Kills",
                Target = "Any",
                Count = 2,
                Xp = 100,
                Weapons = [weaponTpl],
            },
            (progress, season) =>
            {
                _trackService.ApplyRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "client-raid",
                    Kills = [new BpKillEvent { Weapon = weaponTpl }],
                });

                Assert.Multiple(() =>
                {
                    Assert.That(progress.ActiveTasks[0].Progress, Is.EqualTo(1));
                    Assert.That(progress.ActiveTasks[0].CreditedXp, Is.False);
                });

                _trackService.ApplyAuthoritativeRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "server-raid",
                    ExitStatus = "Survived",
                    Kills =
                    [
                        new BpKillEvent { Weapon = weaponTpl },
                        new BpKillEvent { Weapon = weaponTpl },
                    ],
                });

                Assert.That(progress.ActiveTasks[0].Progress, Is.EqualTo(1), "权威战后快照不应重复消费客户端武器任务");

                var final = _trackService.ApplyRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "client-raid",
                    ExitStatus = "Survived",
                    Kills =
                    [
                        new BpKillEvent { Weapon = weaponTpl },
                        new BpKillEvent { Weapon = weaponTpl },
                    ],
                });

                Assert.Multiple(() =>
                {
                    Assert.That(progress.ActiveTasks[0].Progress, Is.EqualTo(2));
                    Assert.That(progress.ActiveTasks[0].CreditedXp, Is.True);
                    Assert.That(progress.Xp, Is.EqualTo(100));
                    Assert.That(final.Credited.Single(credit => credit.Done).TaskId, Is.EqualTo("specific_weapon_kills"));
                });
            });
    }

    [TestCase("Killed", 0)]
    [TestCase("MissingInAction", 2)]
    [TestCase("Left", 2)]
    [TestCase("Runner", 2)]
    [TestCase("Transit", 2)]
    public void OneLife_KillTargetReachedWithoutSurvived_OnlyDeathClearsProgress(string exitStatus, int expectedProgress)
    {
        WithTaskStore(
            new BpTaskTemplate { Id = "one_life_kills", Scope = "daily", ConditionType = "Kills", Count = 2, Xp = 100, OneLife = true },
            (progress, season) =>
            {
                var result = _trackService.ApplyAuthoritativeRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "raid-1",
                    ExitStatus = exitStatus,
                    Kills = [new BpKillEvent(), new BpKillEvent()],
                });

                Assert.Multiple(() =>
                {
                    Assert.That(progress.ActiveTasks[0].CreditedXp, Is.False);
                    Assert.That(progress.ActiveTasks[0].Progress, Is.EqualTo(expectedProgress));
                    Assert.That(progress.Xp, Is.Zero);
                    Assert.That(result.Credited.Any(credit => credit.Done), Is.False);
                });
            });
    }

    [Test]
    public void OneLife_KillTargetReachedAndSurvived_Completes()
    {
        WithTaskStore(
            new BpTaskTemplate { Id = "one_life_kills", Scope = "daily", ConditionType = "Kills", Count = 2, Xp = 100, OneLife = true },
            (progress, season) =>
            {
                var result = _trackService.ApplyAuthoritativeRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "raid-1",
                    ExitStatus = "Survived",
                    Kills = [new BpKillEvent(), new BpKillEvent()],
                });

                Assert.Multiple(() =>
                {
                    Assert.That(progress.ActiveTasks[0].CreditedXp, Is.True);
                    Assert.That(progress.ActiveTasks[0].Progress, Is.EqualTo(2));
                    Assert.That(progress.Xp, Is.EqualTo(100));
                    Assert.That(result.Credited.Single(credit => credit.Done).TaskId, Is.EqualTo("one_life_kills"));
                });
            });
    }

    [Test]
    public void OneLife_ProgressAccumulatesAcrossSurvivedRaids()
    {
        WithTaskStore(
            new BpTaskTemplate { Id = "one_life_kills", Scope = "daily", ConditionType = "Kills", Count = 3, Xp = 100, OneLife = true },
            (progress, season) =>
            {
                var first = _trackService.ApplyAuthoritativeRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "raid-1",
                    ExitStatus = "Survived",
                    Kills = [new BpKillEvent()],
                });

                Assert.Multiple(() =>
                {
                    Assert.That(progress.ActiveTasks[0].Progress, Is.EqualTo(1));
                    Assert.That(progress.ActiveTasks[0].CreditedXp, Is.False);
                    Assert.That(first.Credited.Any(credit => credit.Done), Is.False);
                });

                var second = _trackService.ApplyAuthoritativeRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "raid-2",
                    ExitStatus = "Survived",
                    Kills = [new BpKillEvent(), new BpKillEvent()],
                });

                Assert.Multiple(() =>
                {
                    Assert.That(progress.ActiveTasks[0].Progress, Is.EqualTo(3));
                    Assert.That(progress.ActiveTasks[0].CreditedXp, Is.True);
                    Assert.That(progress.Xp, Is.EqualTo(100));
                    Assert.That(second.Credited.Single(credit => credit.Done).TaskId, Is.EqualTo("one_life_kills"));
                });
            });
    }

    [Test]
    public void OneLife_SupplementalProgress_WaitsForAuthoritativeSurvivedResult()
    {
        WithTaskStore(
            new BpTaskTemplate { Id = "one_life_zone", Scope = "daily", ConditionType = "VisitZone", Count = 1, Xp = 75, OneLife = true, ZoneId = "zone-a" },
            (progress, season) =>
            {
                var live = _trackService.ApplyRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "raid-1",
                    VisitedZones = ["zone-a"],
                });

                Assert.Multiple(() =>
                {
                    Assert.That(progress.ActiveTasks[0].Progress, Is.EqualTo(1));
                    Assert.That(progress.ActiveTasks[0].CreditedXp, Is.False);
                    Assert.That(live.Credited.Any(credit => credit.Done), Is.False);
                });

                var ended = _trackService.ApplyAuthoritativeRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "raid-1",
                    ExitStatus = "Survived",
                });

                Assert.Multiple(() =>
                {
                    Assert.That(progress.ActiveTasks[0].CreditedXp, Is.True);
                    Assert.That(progress.Xp, Is.EqualTo(75));
                    Assert.That(ended.Credited.Any(credit => credit.Done), Is.True);
                });
            });
    }

    [TestCase("Killed", 0)]
    [TestCase("MissingInAction", 1)]
    [TestCase("Left", 1)]
    [TestCase("Runner", 1)]
    [TestCase("Transit", 1)]
    public void OneLife_SupplementalWeaponProgress_OnlyDeathClearsProgress(string exitStatus, int expectedProgress)
    {
        const string weaponTpl = "weapon-a";
        WithTaskStore(
            new BpTaskTemplate
            {
                Id = "one_life_specific_weapon",
                Scope = "daily",
                ConditionType = "Kills",
                Target = "Any",
                Count = 1,
                Xp = 100,
                OneLife = true,
                Weapons = [weaponTpl],
            },
            (progress, season) =>
            {
                _trackService.ApplyRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "client-raid",
                    Kills = [new BpKillEvent { Weapon = weaponTpl }],
                });

                Assert.Multiple(() =>
                {
                    Assert.That(progress.ActiveTasks[0].Progress, Is.EqualTo(1));
                    Assert.That(progress.ActiveTasks[0].CreditedXp, Is.False);
                    Assert.That(progress.Xp, Is.Zero);
                });

                _trackService.ApplyAuthoritativeRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "server-raid",
                    ExitStatus = exitStatus,
                });

                Assert.That(progress.ActiveTasks[0].Progress, Is.EqualTo(expectedProgress));

                var final = _trackService.ApplyRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "client-raid",
                    ExitStatus = exitStatus,
                    Kills = [new BpKillEvent { Weapon = weaponTpl }],
                });

                Assert.Multiple(() =>
                {
                    Assert.That(progress.ActiveTasks[0].Progress, Is.EqualTo(expectedProgress));
                    Assert.That(progress.ActiveTasks[0].CreditedXp, Is.False);
                    Assert.That(progress.Xp, Is.Zero);
                    Assert.That(final.Credited.Any(credit => credit.Done), Is.False);
                    Assert.That(progress.PendingSupplementalRaidId, Is.Null);
                });
            });
    }

    [Test]
    public void LegacySingleRaid_KilledAfterTargetReached_RetainsLegacyCompletionBehavior()
    {
        WithTaskStore(
            new BpTaskTemplate { Id = "legacy_single", Scope = "daily", ConditionType = "Kills", Count = 1, Xp = 25, SingleRaid = true },
            (progress, season) =>
            {
                _trackService.ApplyAuthoritativeRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "raid-1",
                    ExitStatus = "Killed",
                    Kills = [new BpKillEvent()],
                });

                Assert.That(progress.ActiveTasks[0].CreditedXp, Is.True);
            });
    }

    [Test]
    public void SupplementalWeaponModTask_ClientFinalAfterAuthoritativeEnd_CompletesFromPendingBaseline()
    {
        WithTaskStore(
            new BpTaskTemplate
            {
                Id = "test_drive_like",
                Scope = "daily",
                ConditionType = "Kills",
                Count = 2,
                Xp = 100,
                WeaponMods = ["mod-a"],
            },
            (progress, season) =>
            {
                _trackService.ApplyRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "client-raid",
                    Kills = [new BpKillEvent { WeaponMods = ["mod-a"] }],
                });

                _trackService.ApplyAuthoritativeRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "server-raid",
                    ExitStatus = "Survived",
                });

                var final = _trackService.ApplyRaidTrack("profile", progress, season, new RaidTrackPayload
                {
                    RaidId = "client-raid",
                    ExitStatus = "Survived",
                    Kills =
                    [
                        new BpKillEvent { WeaponMods = ["mod-a"] },
                        new BpKillEvent { WeaponMods = ["mod-a"] },
                    ],
                });

                Assert.Multiple(() =>
                {
                    Assert.That(progress.ActiveTasks[0].CreditedXp, Is.True);
                    Assert.That(progress.ActiveTasks[0].Progress, Is.EqualTo(2));
                    Assert.That(progress.Xp, Is.EqualTo(100));
                    Assert.That(final.Duplicate, Is.False);
                    Assert.That(progress.ProcessedRaidIds, Does.Contain("client-raid"));
                    Assert.That(progress.ProcessedRaidIds, Does.Contain("server-raid"));
                    Assert.That(progress.PendingSupplementalRaidId, Is.Null);
                });
            });
    }

    [Test]
    public void TaskRules_RejectOneLifeForNonRaidHandover()
    {
        var task = new BpTaskTemplate { Id = "handover", ConditionType = "HandoverItem", OneLife = true };
        Assert.That(BattlePassTaskRules.Validate(task), Does.Contain("一命完成仅支持"));
    }

    private static void WithTaskStore(BpTaskTemplate task, Action<BpProgress, BpSeason> assertion)
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "spt-bp-one-life-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Directory.SetCurrentDirectory(tempDirectory);
            BattlePassStore.SaveTasks([task]);
            BattlePassStore.SaveGenTasks([]);
            var progress = new BpProgress
            {
                SeasonId = "season",
                ActiveTasks = [new BpActiveTask { TaskId = task.Id, Scope = task.Scope }],
            };
            var season = new BpSeason { SeasonId = "season", StartUtc = 1, EndUtc = long.MaxValue };
            assertion(progress, season);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
