using NUnit.Framework;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
using LogLevelAlias = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

namespace UnitTests.Tests.Services;

[TestFixture]
public class SaveServerLazyHeaderScanTests
{
    private sealed class TestLogger : ISptLogger<SaveServer>
    {
        public string? LastWarning;

        public void LogWithColor(string data, LogTextColor? textColor = null, LogBackgroundColor? backgroundColor = null, Exception? ex = null) { }

        public void Success(string data, Exception? ex = null) { }

        public void Error(string data, Exception? ex = null) => throw new InvalidOperationException("log error: " + data, ex);

        public void Warning(string data, Exception? ex = null)
        {
            LastWarning = data + (ex is null ? "" : " | " + ex);
            throw new InvalidOperationException("ScanProfileHeader warning: " + data + " | " + ex);
        }

        public void Info(string data, Exception? ex = null) { }

        public void Debug(string data, Exception? ex = null) { }

        public void Critical(string data, Exception? ex = null) { }

        public void Log(LogLevelAlias level, string data, LogTextColor? textColor = null, LogBackgroundColor? backgroundColor = null, Exception? ex = null) { }

        public bool IsLogEnabled(LogLevelAlias level) => true;

        public void DumpAndStop() { }
    }

    private sealed class TestableSaveServer(JsonUtil jsonUtil, TestLogger logger) : SaveServer(
        null!,
        null!,
        jsonUtil,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        null!,
        logger,
        null!
    )
    {
        public bool ScanProfileHeaderForTest(string filePath) => ScanProfileHeader(filePath);
    }

    private static JsonUtil CreateJsonUtil()
    {
        return new JsonUtil([new SptJsonConverterRegistrator()]);
    }

    private const string EmptyItemsProfileId = "6a329a8f2724a732f46056ec";
    private const string NonEmptyItemsProfileId = "6a329a8f2724a732f46057e7";

    private static string EmptyItemsJson() =>
        """
        {
          "info": { "id": "6a329a8f2724a732f46056ec", "aid": 1013430, "username": "headless_6a329a8f2724a732f46056eb", "wipe": false, "edition": "Edge of Darkness" },
          "characters": {
            "pmc": {
              "_id": "6a329a8f2724a732f46056ec",
              "aid": 1013430,
              "Stats": { "Eft": { "OverallCounters": { "Items": [] } } }
            }
          }
        }
        """;

    private static string NonEmptyItemsJson() =>
        """
        {
          "info": { "id": "6a329a8f2724a732f46057e7", "aid": 1013431, "username": "player1", "wipe": false, "edition": "Edge of Darkness" },
          "characters": {
            "pmc": {
              "_id": "6a329a8f2724a732f46057e7",
              "aid": 1013431,
              "Stats": { "Eft": { "OverallCounters": { "Items": [ { "Key": ["Kills"], "Value": 12.0 } ] } } }
            }
          }
        }
        """;

    [Test]
    public void ScanProfileHeader_EmptyOverallCountersItems_DoesNotThrowAndIndexesProfile()
    {
        // 生产事故复现（2026-08-01）：OverallCounters.Items 为空数组 [] 时，误用
        // Deserialize<OverallCounters>(数组文本) 抛 "could not be converted to OverallCounters"，
        // 存档头扫描失败 → 懒加载索引缺失 → 玩家进游戏报 no profile found。
        var path = Path.Combine(Path.GetTempPath(), "lazy-header-empty-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, EmptyItemsJson());

        try
        {
            var service = new TestableSaveServer(CreateJsonUtil(), new TestLogger());
            Assert.That(service.ScanProfileHeaderForTest(path), Is.True);
            Assert.That(service.GetLazyHeaders().ContainsKey(new MongoId(EmptyItemsProfileId)), Is.True);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ScanProfileHeader_NonEmptyOverallCountersItems_ExtractsStatsSummary()
    {
        var path = Path.Combine(Path.GetTempPath(), "lazy-header-items-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, NonEmptyItemsJson());

        try
        {
            var service = new TestableSaveServer(CreateJsonUtil(), new TestLogger());
            Assert.That(service.ScanProfileHeaderForTest(path), Is.True);
            var header = service.GetLazyHeaders()[new MongoId(NonEmptyItemsProfileId)];
            Assert.That(header.StatsSummary, Is.Not.Null);
            Assert.That(header.StatsSummary!.Kills, Is.EqualTo(12.0));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
