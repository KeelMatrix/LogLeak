using Microsoft.Extensions.Logging;
using Serilog;
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
}
