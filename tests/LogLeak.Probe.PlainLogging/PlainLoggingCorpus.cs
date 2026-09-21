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

    public static void StringDictionaryScopes(ILogger logger, string sentinel)
    {
        using var dictionaryScope = logger.BeginScope(new Dictionary<string, string>
        {
            ["Authorization"] = sentinel
        });
        IReadOnlyDictionary<string, string> readOnlyDictionary = new Dictionary<string, string>
        {
            ["Classification"] = sentinel
        };
        using var readOnlyDictionaryScope = logger.BeginScope(readOnlyDictionary);
        logger.LogInformation("string-valued dictionary scopes event");
    }

    public static void NonStringDictionaryScope(ILogger logger, string sentinel)
    {
        using var scope = logger.BeginScope(new Dictionary<string, OpaqueClassifiedValue>
        {
            ["Authorization"] = new OpaqueClassifiedValue(sentinel)
        });
        logger.LogInformation("non-string dictionary scope event");
    }

    public static void Exception(ILogger logger, string sentinel)
    {
        var exception = new InvalidOperationException($"exception representation {sentinel}");
        logger.LogError(exception, "exception event");
    }

    public static RedactionEvidence Redacted(ILogger logger, string sentinel, bool redactionEnabled)
    {
        var classified = Classify(sentinel);
        var formattedValue = redactionEnabled ? Redact(classified, "formatted") : classified.RawValue;
        var structuredValue = redactionEnabled ? Redact(classified, "structured") : classified.RawValue;
        logger.LogInformation("redacted value {Value}", formattedValue);
        var state = new[]
        {
            new KeyValuePair<string, object?>("ClassifiedValue", structuredValue),
            new KeyValuePair<string, object?>("OpaqueClassifiedValue", classified)
        };

        logger.Log(LogLevel.Information, new EventId(206, "classification"), state, null, static (_, _) => "classified state event");
        return new RedactionEvidence(classified.IsClassified, formattedValue, structuredValue, redactionEnabled);
    }

    private static ClassifiedValue Classify(string value) => new(value, true);

    private static string Redact(ClassifiedValue value, string field)
        => "<" + field + "-classified-" + value.RawValue.Length + ">";

    internal sealed record RedactionEvidence(bool WasClassified, string FormattedValue, string StructuredValue, bool RedactionEnabled);

    private sealed record ClassifiedValue(string RawValue, bool IsClassified)
    {
        public override string ToString() => "[CLASSIFIED]";
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
