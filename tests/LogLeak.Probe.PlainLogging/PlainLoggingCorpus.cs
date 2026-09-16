using Microsoft.Extensions.Logging;

namespace LogLeak.Probe.PlainLogging;

internal static class PlainLoggingCorpus
{
    public static void Interpolated(ILogger logger, string sentinel) => logger.LogInformation($"interpolated value {sentinel}");

    public static void MessageTemplate(ILogger logger, string sentinel) => logger.LogInformation("message template {Value}", sentinel);

    public static void StructuredState(ILogger logger, string sentinel)
    {
        var state = new[]
        {
            new KeyValuePair<string, object?>("ClassifiedValue", sentinel)
        };

        logger.Log(LogLevel.Information, new EventId(201, "structured"), state, null, static (_, _) => "structured state event");
    }

    public static void NestedScopes(ILogger logger, string sentinel)
    {
        using var outer = logger.BeginScope("outer scope");
        using var inner = logger.BeginScope($"inner scope {sentinel}");
        logger.LogInformation("nested scope event");
    }

    public static void Exception(ILogger logger, string sentinel)
    {
        var exception = new InvalidOperationException($"exception representation {sentinel}");
        logger.LogError(exception, "exception event");
    }

    public static void Redacted(ILogger logger, string sentinel)
    {
        var classified = new OpaqueClassifiedValue(sentinel);
        var redacted = "[REDACTED]";
        logger.LogInformation("redacted value {Value}", redacted);
        var state = new[]
        {
            new KeyValuePair<string, object?>("OpaqueClassifiedValue", classified)
        };

        logger.Log(LogLevel.Information, new EventId(206, "classification"), state, null, static (_, _) => "classified state event");
    }

    internal sealed class OpaqueClassifiedValue
    {
        public OpaqueClassifiedValue(string value)
        {
            Value = value;
        }

        public string Value { get; }

        public override string ToString() => "[CLASSIFIED]";
    }
}

internal static partial class GeneratedLoggingCorpus
{
    [LoggerMessage(EventId = 202, Level = LogLevel.Warning, Message = "source generated value {Value}")]
    internal static partial void SourceGenerated(ILogger logger, string value);
}
