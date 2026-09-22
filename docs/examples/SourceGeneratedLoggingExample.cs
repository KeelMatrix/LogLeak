using KeelMatrix.LogLeak;
using Microsoft.Extensions.Logging;

namespace KeelMatrix.LogLeak.Documentation;

internal static partial class SourceGeneratedLoggingExample
{
    internal static void Run()
    {
        const string sentinel = "synthetic-logger-value";
        using var probe = new LogLeakProbe().AddSecret("token", sentinel);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(probe.Provider));

        GeneratedLogging.RequestCompleted(factory.CreateLogger("PaymentClient"));
        probe.AssertNoLeaks();
    }

    private static partial class GeneratedLogging
    {
        [LoggerMessage(EventId = 42, Level = LogLevel.Information, Message = "request completed")]
        internal static partial void RequestCompleted(ILogger logger);
    }
}
