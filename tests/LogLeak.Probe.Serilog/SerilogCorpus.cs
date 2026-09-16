using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace LogLeak.Probe.Serilog;

internal static class SerilogCorpus
{
    public static ILoggerProvider CreateProvider()
    {
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .CreateLogger();

        return new SerilogLoggerProvider(serilog, dispose: true);
    }

    public static void Route(Microsoft.Extensions.Logging.ILogger logger, string sentinel) => logger.LogInformation("Serilog provider route {Value}", sentinel);

    public static bool DirectSinkObserved(string sentinel)
    {
        var sink = new SentinelSink(sentinel);
        using var logger = new LoggerConfiguration()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("direct sink field {Value}", sentinel);
        return sink.Observed;
    }

    private sealed class SentinelSink : ILogEventSink
    {
        private readonly string _sentinel;

        public SentinelSink(string sentinel)
        {
            _sentinel = sentinel;
        }

        public bool Observed { get; private set; }

        public void Emit(LogEvent logEvent)
        {
            Observed = logEvent.Properties.Values.Any(value => value.ToString().Contains(_sentinel, StringComparison.Ordinal));
        }
    }
}
