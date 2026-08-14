using System.Globalization;
using NUnit.Framework;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Services;

namespace UnitTests.Tests.Generators;

[TestFixture]
public class LocationLootGeneratorTests
{
    private LocationLootGenerator _generator;
    private DatabaseService _databaseService;

    [OneTimeSetUp]
    public void Initialize()
    {
        _generator = DI.GetInstance().GetService<LocationLootGenerator>();
        _databaseService = DI.GetInstance().GetService<DatabaseService>();
    }

    [Test]
    public void RepeatedRaidGeneration_DoesNotMutateDatabaseLooseLootPool()
    {
        const string locationId = "factory4_day";
        var location = _databaseService.GetLocation(locationId);
        Assert.That(location, Is.Not.Null, $"Test location {locationId} is missing");

        var source = location!.LooseLoot.Value;
        var baseline = Snapshot(source);
        Assert.That(baseline, Is.Not.Empty);

        for (var raid = 1; raid <= 5; raid++)
        {
            _generator.GenerateLocationLoot(locationId);
            Assert.That(
                Snapshot(source),
                Is.EqualTo(baseline),
                $"Database loose-loot pool changed after simulated raid {raid}");
        }
    }

    private static string Snapshot(LooseLoot loot)
    {
        var points = (loot.Spawnpoints ?? [])
            .Select((point, index) => SnapshotPoint("random", point, index))
            .Concat((loot.SpawnpointsForced ?? []).Select((point, index) => SnapshotPoint("forced", point, index)));

        return string.Join(
            "\n",
            new[]
            {
                loot.SpawnpointCount?.Mean.ToString("R", CultureInfo.InvariantCulture) ?? "null",
                loot.SpawnpointCount?.Std.ToString("R", CultureInfo.InvariantCulture) ?? "null",
            }.Concat(points));
    }

    private static string SnapshotPoint(string bucket, Spawnpoint point, int index)
    {
        var template = point.Template;
        var items = template?.Items is null
            ? ""
            : string.Join(
                ",",
                template.Items.Select(item =>
                    $"{item.Id}:{item.Template}:{item.ParentId}:{item.SlotId}:{item.ComposedKey}"));
        var distribution = point.ItemDistribution is null
            ? ""
            : string.Join(
                ",",
                point.ItemDistribution.Select(item =>
                    $"{item.ComposedKey?.Key}:{item.RelativeProbability?.ToString("R", CultureInfo.InvariantCulture)}"));

        return string.Join(
            "|",
            bucket,
            index,
            point.LocationId,
            point.Probability?.ToString("R", CultureInfo.InvariantCulture),
            template?.Id,
            template?.Root,
            items,
            distribution);
    }
}
