namespace KeelMatrix.LogLeak;

internal static class LogLeakDiagnostics
{
    public static string SafeFinding(
        string sentinelLabel,
        LogLeakLocation location,
        string? categoryName,
        int? eventId,
        string? eventName,
        string? propertyName,
        IReadOnlyList<string> sentinelValues)
    {
        var detailed = ComposeFinding(sentinelLabel, location, categoryName, eventId, eventName, propertyName);
        if (!ContainsRegisteredSentinel(detailed, sentinelValues))
        {
            return detailed;
        }

        var minimal = ComposeFinding(sentinelLabel, location, categoryName: null, eventId: null, eventName: null, propertyName: null);
        return ContainsRegisteredSentinel(minimal, sentinelValues) ? string.Empty : minimal;
    }

    public static string SafeVerificationResult(
        LogLeakVerificationStatus status,
        int findingCount,
        int capturedEventCount,
        string? inconclusiveReason,
        IReadOnlyList<string> sentinelValues)
    {
        var detailed = "LogLeak verification "
            + status
            + " ("
            + findingCount
            + " finding(s), "
            + capturedEventCount
            + " captured event(s)"
            + (inconclusiveReason is null ? ")." : ", " + inconclusiveReason + ").");

        if (!ContainsRegisteredSentinel(detailed, sentinelValues))
        {
            return detailed;
        }

        var minimal = "LogLeak verification " + status + ".";
        return ContainsRegisteredSentinel(minimal, sentinelValues) ? string.Empty : minimal;
    }

    public static string SafeInconclusiveReason(string reason, IReadOnlyList<string> sentinelValues)
        => ContainsRegisteredSentinel(reason, sentinelValues) ? string.Empty : reason;

    public static string SafeAssertionMessage(
        IReadOnlyList<LogLeakFinding> findings,
        IReadOnlyList<string> sentinelValues)
    {
        var lines = findings.Select(static finding => "- " + finding.ToString());
        var detailed = "Registered sentinel values reached the logging event stream ("
            + findings.Count
            + " finding(s))."
            + Environment.NewLine
            + string.Join(Environment.NewLine, lines);

        if (!ContainsRegisteredSentinel(detailed, sentinelValues))
        {
            return detailed;
        }

        var minimal = "LogLeak verification found registered sentinels.";
        return ContainsRegisteredSentinel(minimal, sentinelValues) ? string.Empty : minimal;
    }

    public static string SafeInconclusiveMessage(string reason, IReadOnlyList<string> sentinelValues)
    {
        var detailed = "LogLeak verification is inconclusive: " + reason;
        if (!ContainsRegisteredSentinel(detailed, sentinelValues))
        {
            return detailed;
        }

        var minimal = "LogLeak verification is inconclusive.";
        return ContainsRegisteredSentinel(minimal, sentinelValues) ? string.Empty : minimal;
    }

    public static string SafeArgumentNullMessage(string? parameterName, IReadOnlyList<string> sentinelValues)
    {
        var detailed = parameterName is null
            ? "Value cannot be null."
            : "Value cannot be null. Parameter: " + parameterName + ".";
        if (!ContainsRegisteredSentinel(detailed, sentinelValues))
        {
            return detailed;
        }

        var minimal = "A required argument was null.";
        return ContainsRegisteredSentinel(minimal, sentinelValues) ? string.Empty : minimal;
    }

    public static string SafeText(string text, IReadOnlyList<string> sentinelValues)
        => ContainsRegisteredSentinel(text, sentinelValues) ? string.Empty : text;

    public static bool ContainsRegisteredSentinel(string text, IReadOnlyList<string> sentinelValues)
    {
        foreach (var sentinelValue in sentinelValues)
        {
            if (IndexOfOrdinal(text, sentinelValue) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string ComposeFinding(
        string sentinelLabel,
        LogLeakLocation location,
        string? categoryName,
        int? eventId,
        string? eventName,
        string? propertyName)
    {
        var result = "Sentinel label '" + sentinelLabel + "' reached " + location;
        if (categoryName is not null)
        {
            result += " in category '" + categoryName + "'";
        }

        if (eventId is int numericEventId)
        {
            result += " (EventId: " + numericEventId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (eventName is not null)
            {
                result += ", Name: '" + eventName + "'";
            }

            result += ")";
        }

        if (propertyName is not null)
        {
            result += ". Property: '" + propertyName + "'";
        }

        return result + ".";
    }

    private static int IndexOfOrdinal(string text, string value)
    {
#if NET8_0_OR_GREATER
        return text.IndexOf(value, StringComparison.Ordinal);
#else
        return text.IndexOf(value, StringComparison.Ordinal);
#endif
    }
}
