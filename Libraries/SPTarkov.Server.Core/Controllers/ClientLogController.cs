using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Logging;
using SPTarkov.Server.Core.Models.Utils;
using LogLevel = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

namespace SPTarkov.Server.Core.Controllers;

[Injectable]
public class ClientLogController(ISptLogger<ClientLogController> logger)
{
    private readonly ConcurrentDictionary<string, int> _forwardedWarningRepeats = new(StringComparer.Ordinal);
    private int _suppressedForwardedWarnings;

    /// <summary>
    ///     Handle /singleplayer/log
    /// </summary>
    /// <param name="logRequest"></param>
    public void ClientLog(ClientLogRequest logRequest)
    {
        var message = $"[{logRequest.Source}] {logRequest.Message}";

        var color = logRequest.Color ?? LogTextColor.White;
        var backgroundColor = logRequest.BackgroundColor ?? LogBackgroundColor.Default;
        var level = logRequest.Level ?? LogLevel.Info;

        if (ShouldSuppressRepeatedForwardedWarning(logRequest, level))
        {
            return;
        }

        logger.Log(level, message, color, backgroundColor);
    }

    private bool ShouldSuppressRepeatedForwardedWarning(ClientLogRequest logRequest, LogLevel level)
    {
        if (level is not (LogLevel.Warn or LogLevel.Error or LogLevel.Fatal))
        {
            return false;
        }

        var key = $"{logRequest.Source}\u001f{level}\u001f{logRequest.Message}";
        // 首次出现返回 false（放行），重复出现计数并抑制；ConcurrentDictionary 无锁路径，
        // 避免多客户端高频 client/log 在全局锁上串行。
        if (!_forwardedWarningRepeats.TryAdd(key, 1))
        {
            _forwardedWarningRepeats.AddOrUpdate(key, 1, (_, count) => count + 1);
            Interlocked.Increment(ref _suppressedForwardedWarnings);
            if (_suppressedForwardedWarnings % 100 == 0)
            {
                var samples = string.Join(
                    ", ",
                    _forwardedWarningRepeats.OrderByDescending(item => item.Value).Take(10).Select(item => $"{item.Value}x {SampleKey(item.Key)}")
                );
                logger.Warning(
                    $"[ClientLog] repeated forwarded warning summary: suppressed={_suppressedForwardedWarnings}, unique={_forwardedWarningRepeats.Count}, samples=[{samples}]"
                );
            }

            return true;
        }

        return false;
    }

    private static string SampleKey(string key)
    {
        var parts = key.Split('\u001f');
        if (parts.Length < 3)
        {
            return key;
        }

        var message = parts[2].Length > 80 ? $"{parts[2][..80]}..." : parts[2];
        return $"{parts[0]}/{parts[1]}:{message}";
    }
}
