using System.Collections.Concurrent;
using SPTarkov.DI.Annotations;

namespace SPTarkov.Server.Core.Utils.Logger;

[Injectable(InjectionType.Singleton)]
public class SptLoggerQueueManager(IEnumerable<ILogHandler> logHandlers)
{
    private readonly Dictionary<string, List<BaseSptLoggerReference>> _resolvedMessageLoggerTypes = new();
    private readonly Lock _resolvedMessageLoggerTypesLock = new();
    private Thread? _loggerTask;
    private readonly Lock LoggerTaskLock = new();
    private readonly CancellationTokenSource _loggerCancellationTokens = new();
    private readonly BlockingCollection<SptLogMessage> _messageQueue = new();
    private Dictionary<LoggerType, ILogHandler>? _logHandlers;
    private SptLoggerConfiguration _config;

    public void Initialize(SptLoggerConfiguration config)
    {
        _config = config;

        _logHandlers ??= logHandlers.ToDictionary(lh => lh.LoggerType, lh => lh);

        lock (LoggerTaskLock)
        {
            if (_loggerTask == null)
            {
                _loggerTask = new Thread(LoggerWorkerThread) { IsBackground = true };
                _loggerTask.Start();
            }
        }
    }

    private void LoggerWorkerThread()
    {
        while (!_loggerCancellationTokens.IsCancellationRequested)
        {
            try
            {
                foreach (var message in _messageQueue.GetConsumingEnumerable(_loggerCancellationTokens.Token))
                {
                    LogMessage(message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Logger queue caught exception: {ex}");
            }
        }
    }

    private void LogMessage(SptLogMessage message)
    {
        List<BaseSptLoggerReference> messageLoggers;
        lock (_resolvedMessageLoggerTypesLock)
        {
            if (!_resolvedMessageLoggerTypes.TryGetValue(message.Logger, out messageLoggers))
            {
                messageLoggers = _config
                    .Loggers.Where(logger =>
                    {
                        var excludeFilters = logger.Filters?.Where(filter => filter.Type == SptLoggerFilterType.Exclude);
                        var includeFilters = logger.Filters?.Where(filter => filter.Type == SptLoggerFilterType.Include);
                        var passed = true;
                        if (excludeFilters?.Any() ?? false)
                        {
                            passed = !excludeFilters.Any(filter => filter.Match(message));
                        }

                        if (includeFilters?.Any() ?? false)
                        {
                            passed = includeFilters.Any(filter => filter.Match(message));
                        }

                        return passed;
                    })
                    .ToList();
                _resolvedMessageLoggerTypes.Add(message.Logger, messageLoggers);
            }
        }

        if (messageLoggers.Count != 0)
        {
            messageLoggers.ForEach(logger =>
            {
                if (logger.LogLevel.CanLog(message.LogLevel) && (_logHandlers?.TryGetValue(logger.Type, out var handler) ?? false))
                {
                    handler.Log(message, logger);
                }
            });
        }
    }

    public void EnqueueMessage(SptLogMessage message)
    {
        _messageQueue.TryAdd(message);
    }

    public void DumpAndStop()
    {
        _loggerCancellationTokens.Cancel();
        while (_loggerTask.IsAlive)
        {
            // waiting for logger to finish avoiding the application to close
            Thread.Sleep(100);
        }
    }
}
