namespace KeelMatrix.LogLeak;

/// <summary>
/// Identifies the supported logging field in which registered sentinel text was observed.
/// </summary>
public enum LogLeakLocation
{
    /// <summary>Formatted output, including message templates and source-generated messages.</summary>
    FormattedMessage,

    /// <summary>A direct string value in structured logging state.</summary>
    StructuredProperty,

    /// <summary>A direct string value in a logging scope.</summary>
    Scope,

    /// <summary>The string representation of an exception supplied to the logging call.</summary>
    ExceptionRepresentation
}

/// <summary>
/// Describes the outcome of a bounded LogLeak verification.
/// </summary>
public enum LogLeakVerificationStatus
{
    /// <summary>No registered sentinel was found and all bounds were respected.</summary>
    Clean,

    /// <summary>At least one registered sentinel was found in a supported field.</summary>
    LeaksDetected,

    /// <summary>A capture or resource bound prevented a conclusive result.</summary>
    Inconclusive
}

/// <summary>
/// Configures sentinel registration and bounded provider-boundary capture.
/// </summary>
public sealed class LogLeakOptions
{
    /// <summary>
    /// Initializes capture options.
    /// </summary>
    /// <param name="maximumCapturedEvents">Maximum number of events that may be inspected successfully.</param>
    /// <param name="maximumSentinels">Maximum number of labeled sentinel values that may be registered.</param>
    /// <param name="maximumFindings">Maximum number of findings retained across the probe lifetime.</param>
    /// <param name="maximumSentinelCharacters">Maximum UTF-16 characters in one registered sentinel.</param>
    /// <param name="maximumPayloadCharacters">Maximum UTF-16 characters in one inspected text unit.</param>
    /// <param name="maximumInspectionUnitsPerEvent">Maximum formatted, structured-entry, scope, and exception units inspected per event.</param>
    /// <param name="maximumFindingsPerEvent">Maximum findings retained from one event.</param>
    /// <param name="maximumTransientAllocationBytes">Maximum transient allocation observed while processing one event.</param>
    public LogLeakOptions(
        int maximumCapturedEvents = 4096,
        int maximumSentinels = 128,
        int maximumFindings = 4096,
        int maximumSentinelCharacters = 4096,
        int maximumPayloadCharacters = 4096,
        int maximumInspectionUnitsPerEvent = 1024,
        int maximumFindingsPerEvent = 256,
        long maximumTransientAllocationBytes = 1_048_576)
    {
        if (maximumCapturedEvents <= 0
            || maximumSentinels <= 0
            || maximumFindings <= 0
            || maximumSentinelCharacters <= 0
            || maximumPayloadCharacters <= 0
            || maximumInspectionUnitsPerEvent <= 0
            || maximumFindingsPerEvent <= 0
            || maximumTransientAllocationBytes <= 0)
        {
            throw new LogLeakConfigurationException("Capture limits must be greater than zero.");
        }

        MaximumCapturedEvents = maximumCapturedEvents;
        MaximumSentinels = maximumSentinels;
        MaximumFindings = maximumFindings;
        MaximumSentinelCharacters = maximumSentinelCharacters;
        MaximumPayloadCharacters = maximumPayloadCharacters;
        MaximumInspectionUnitsPerEvent = maximumInspectionUnitsPerEvent;
        MaximumFindingsPerEvent = maximumFindingsPerEvent;
        MaximumTransientAllocationBytes = maximumTransientAllocationBytes;
    }

    /// <summary>Gets the maximum number of successfully inspected events.</summary>
    public int MaximumCapturedEvents { get; }

    /// <summary>Gets the maximum number of registered sentinels.</summary>
    public int MaximumSentinels { get; }

    /// <summary>Gets the maximum number of retained findings.</summary>
    public int MaximumFindings { get; }

    /// <summary>Gets the maximum UTF-16 character count for one sentinel.</summary>
    public int MaximumSentinelCharacters { get; }

    /// <summary>Gets the maximum UTF-16 character count for one inspected text unit.</summary>
    public int MaximumPayloadCharacters { get; }

    /// <summary>Gets the maximum inspection-unit count per event.</summary>
    public int MaximumInspectionUnitsPerEvent { get; }

    /// <summary>Gets the maximum finding count per event.</summary>
    public int MaximumFindingsPerEvent { get; }

    /// <summary>Gets the maximum transient allocation measured while processing one event.</summary>
    public long MaximumTransientAllocationBytes { get; }
}

/// <summary>
/// A safe description of one registered-sentinel finding.
/// </summary>
public sealed class LogLeakFinding
{
    internal LogLeakFinding(
        string sentinelLabel,
        LogLeakLocation location,
        string? categoryName,
        int eventId,
        string? eventName,
        string? propertyName)
    {
        SentinelLabel = sentinelLabel;
        Location = location;
        CategoryName = categoryName;
        EventId = eventId;
        EventName = eventName;
        PropertyName = propertyName;
    }

    /// <summary>Gets the safe label supplied when the sentinel was registered.</summary>
    public string SentinelLabel { get; }

    /// <summary>Gets the broad supported logging location.</summary>
    public LogLeakLocation Location { get; }

    /// <summary>Gets the logging category when it is safe to report.</summary>
    public string? CategoryName { get; }

    /// <summary>Gets the numeric event identifier.</summary>
    public int EventId { get; }

    /// <summary>Gets the event name when it is safe to report.</summary>
    public string? EventName { get; }

    /// <summary>Gets the structured property name when it is safe to report.</summary>
    public string? PropertyName { get; }

    /// <summary>Returns a safe diagnostic that never includes the registered sentinel value.</summary>
    public override string ToString()
    {
        var result = "Sentinel label '" + SentinelLabel + "' reached " + Location;
        if (CategoryName is not null)
        {
            result += " in category '" + CategoryName + "'";
        }

        if (EventId != 0)
        {
            result += " (EventId: " + EventId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (EventName is not null)
            {
                result += ", Name: '" + EventName + "'";
            }

            result += ")";
        }

        if (PropertyName is not null)
        {
            result += ". Property: '" + PropertyName + "'";
        }

        return result + ".";
    }
}

/// <summary>
/// Contains the safe outcome of a bounded verification.
/// </summary>
public sealed class LogLeakVerificationResult
{
    internal LogLeakVerificationResult(
        LogLeakVerificationStatus status,
        IReadOnlyList<LogLeakFinding> findings,
        int capturedEventCount,
        string? inconclusiveReason)
    {
        Status = status;
        Findings = findings;
        CapturedEventCount = capturedEventCount;
        InconclusiveReason = inconclusiveReason;
    }

    /// <summary>Gets the verification status.</summary>
    public LogLeakVerificationStatus Status { get; }

    /// <summary>Gets safe findings observed during verification.</summary>
    public IReadOnlyList<LogLeakFinding> Findings { get; }

    /// <summary>Gets the number of events that completed bounded inspection.</summary>
    public int CapturedEventCount { get; }

    /// <summary>Gets the safe explanation when <see cref="Status"/> is <see cref="LogLeakVerificationStatus.Inconclusive"/>.</summary>
    public string? InconclusiveReason { get; }

    /// <summary>Returns a safe summary that contains no captured log content.</summary>
    public override string ToString()
        => "LogLeak verification "
            + Status
            + " ("
            + Findings.Count
            + " finding(s), "
            + CapturedEventCount
            + " captured event(s)"
            + (InconclusiveReason is null ? ")." : ", " + InconclusiveReason + ").");
}
