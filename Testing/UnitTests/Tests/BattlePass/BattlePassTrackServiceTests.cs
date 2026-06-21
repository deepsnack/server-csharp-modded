using NUnit.Framework;
using SPTarkov.Server.Core.BattlePass;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.BattlePass;

[TestFixture]
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
    public void KillMatches_LegacyUnavailableEquipmentFiltersDoNotMakeTaskImpossible()
    {
        var task = new BpTaskTemplate
        {
            Target = "Any",
            EnemyEquipment = ["legacy-enemy-equipment"],
            PlayerEquipment = ["legacy-player-equipment"],
            WeaponMods = ["legacy-weapon-mod"],
        };

        Assert.That(_trackService.KillMatches(task, new BpKillEvent()), Is.True);
    }
}
