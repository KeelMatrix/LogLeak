using KeelMatrix.Telemetry;
using Microsoft.Extensions.Logging;

namespace KeelMatrix.LogLeak;

/// <summary>
/// Captures supported Microsoft.Extensions.Logging provider-boundary fields and verifies them against explicitly registered sentinels.
/// </summary>
public sealed class LogLeakProbe : IDisposable
{
    private readonly object gate = new();
    private readonly LogLeakOptions options;
    private readonly List<RegisteredSentinel> sentinels = new();
    private readonly List<LogLeakFinding> findings = new();
    private readonly ILogLeakTelemetry telemetry;
    private readonly CaptureLoggerProvider provider;
    private IExternalScopeProvider scopeProvider = new LoggerExternalScopeProvider();
    private int capturedEventCount;
    private bool disposed;
    private bool eventOverflowed;
    private bool aggregateBudgetExceeded;
    private string? aggregateBudgetName;
    private long aggregateBudgetLimit;
    private long aggregateBudgetObserved;
    private bool payloadLimitExceeded;
    private bool captureFailed;
    private int inspectionUnitsThisEvent;
    private int findingsThisEvent;
    private string? categoryNameThisEvent;
    private EventId eventIdThisEvent;

    /// <summary>
    /// Initializes a probe with bounded capture and the shared best-effort telemetry client.
    /// </summary>
    /// <param name="options">Capture limits, or <see langword="null"/> for the conservative defaults.</param>
    public LogLeakProbe(LogLeakOptions? options = null)
        : this(options, new SharedTelemetryClient())
    {
    }

    internal LogLeakProbe(LogLeakOptions? options, ILogLeakTelemetry telemetry)
    {
        this.options = options ?? new LogLeakOptions();
        ThrowIfNull(telemetry, nameof(telemetry));
        this.telemetry = telemetry;
        provider = new CaptureLoggerProvider(this);
    }

    /// <summary>
    /// Gets the provider to add through normal <see cref="ILoggingBuilder"/> registration.
    /// </summary>
    /// <remarks>Disposing the provider also disposes this probe and clears retained state.</remarks>
    public ILoggerProvider Provider => provider;

    /// <summary>
    /// Registers one labeled test-only sentinel value.
    /// </summary>
    /// <param name="label">A short safe identifier used in findings.</param>
    /// <param name="value">The exact non-empty value to match in supported logging fields.</param>
    /// <returns>This probe, to support concise setup.</returns>
    /// <exception cref="LogLeakConfigurationException">Thrown when the label or value is invalid.</exception>
    /// <exception cref="LogLeakDisposedException">Thrown when the probe has been disposed.</exception>
    public LogLeakProbe AddSecret(string label, string value)
    {
        ValidateLabel(label);
        ValidateValue(value);

        if (value.Length > options.MaximumSentinelCharacters)
        {
            throw new LogLeakConfigurationException("The sentinel value exceeds the configured registration limit.");
        }

        if (ContainsOrdinal(label, value))
        {
            throw new LogLeakConfigurationException("A sentinel label must not contain its sentinel value.");
        }

        lock (gate)
        {
            ThrowIfDisposed();
            if (sentinels.Count >= options.MaximumSentinels)
            {
                throw new LogLeakConfigurationException("The sentinel registration limit was reached.");
            }

            if (sentinels.Any(existing => string.Equals(existing.Label, label, StringComparison.Ordinal)))
            {
                throw new LogLeakConfigurationException("Sentinel labels must be unique.");
            }

            sentinels.Add(new RegisteredSentinel(label, value));
        }

        return this;
    }

    /// <summary>
    /// Performs a bounded verification and returns a safe, explicit result.
    /// </summary>
    /// <returns>A clean, leak-detected, or inconclusive result with safe findings only.</returns>
    /// <exception cref="LogLeakDisposedException">Thrown when the probe has been disposed.</exception>
    public LogLeakVerificationResult Verify()
    {
        LogLeakVerificationResult result;
        lock (gate)
        {
            ThrowIfDisposed();
            var status = GetStatus();
            result = new LogLeakVerificationResult(
                status,
                findings.ToArray(),
                capturedEventCount,
                status == LogLeakVerificationStatus.Inconclusive ? GetInconclusiveReason() : null);
        }

        if (result.Status != LogLeakVerificationStatus.Inconclusive)
        {
            TrackTelemetryBestEffort();
        }

        return result;
    }

    /// <summary>
    /// Verifies that no registered sentinel reached a supported logging field.
    /// </summary>
    /// <exception cref="LogLeakAssertionException">Thrown when a sentinel was found.</exception>
    /// <exception cref="LogLeakInconclusiveException">Thrown when bounded capture could not prove a clean result.</exception>
    /// <exception cref="LogLeakDisposedException">Thrown when the probe has been disposed.</exception>
    public void AssertNoLeaks()
    {
        var result = Verify();
        if (result.Status == LogLeakVerificationStatus.Inconclusive)
        {
            throw new LogLeakInconclusiveException(result.InconclusiveReason!, result.Findings);
        }

        if (result.Findings.Count > 0)
        {
            throw new LogLeakAssertionException(result.Findings);
        }
    }

    /// <summary>
    /// Clears registered sentinels, findings, and capture state.
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            findings.Clear();
            sentinels.Clear();
            capturedEventCount = 0;
            eventOverflowed = false;
            aggregateBudgetExceeded = false;
            aggregateBudgetName = null;
            aggregateBudgetLimit = 0;
            aggregateBudgetObserved = 0;
            payloadLimitExceeded = false;
            captureFailed = false;
            inspectionUnitsThisEvent = 0;
            findingsThisEvent = 0;
            scopeProvider = new LoggerExternalScopeProvider();
            disposed = true;
        }
    }

    private static void ValidateLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)
            || label.Length > 64
            || label.Any(character => !(char.IsLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':')))
        {
            throw new LogLeakConfigurationException("Sentinel labels must be short safe identifiers.");
        }
    }

    private static void ValidateValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new LogLeakConfigurationException("Sentinel values must be non-empty.");
        }
    }

    private CaptureLogger CreateLogger(string categoryName)
    {
        ThrowIfNull(categoryName, nameof(categoryName));

        lock (gate)
        {
            ThrowIfDisposed();
            return new CaptureLogger(this, categoryName);
        }
    }

    private IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return scopeProvider.Push(state);
        }
    }

    private void SetScopeProvider(IExternalScopeProvider newScopeProvider)
    {
        ThrowIfNull(newScopeProvider, nameof(newScopeProvider));

        lock (gate)
        {
            ThrowIfDisposed();
            scopeProvider = newScopeProvider;
        }
    }

    private bool IsEnabled(LogLevel logLevel)
    {
        lock (gate)
        {
            return !disposed && logLevel != LogLevel.None;
        }
    }

    private void Record<TState>(
        string categoryName,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (gate)
        {
            if (disposed || eventOverflowed || aggregateBudgetExceeded || payloadLimitExceeded || captureFailed)
            {
                return;
            }

            if (capturedEventCount >= options.MaximumCapturedEvents)
            {
                eventOverflowed = true;
                return;
            }

            inspectionUnitsThisEvent = 0;
            findingsThisEvent = 0;
            categoryNameThisEvent = categoryName;
            eventIdThisEvent = eventId;
            var allocationStart = GetAllocatedBytes();
            try
            {
                var formatted = formatter(state, exception);
                EnsurePayloadWithinLimit(formatted, "formatted message");
                EnsureTransientAllocationWithinLimit(allocationStart);

                var exceptionRepresentation = exception is null ? null : GetExceptionRepresentation(exception);
                EnsurePayloadWithinLimit(exceptionRepresentation, "exception representation");
                EnsureTransientAllocationWithinLimit(allocationStart);

                Inspect(categoryName, eventId, formatted, state, scopeProvider, exceptionRepresentation);
                EnsureTransientAllocationWithinLimit(allocationStart);
                capturedEventCount++;
            }
            catch (InspectionBudgetReachedException)
            {
            }
            catch (PayloadLimitException)
            {
                payloadLimitExceeded = true;
            }
            catch (Exception)
            {
                captureFailed = true;
            }
            finally
            {
                categoryNameThisEvent = null;
                eventIdThisEvent = default;
            }
        }
    }

    private void Inspect<TState>(
        string categoryName,
        EventId eventId,
        string? formatted,
        TState state,
        IExternalScopeProvider currentScopeProvider,
        string? exceptionRepresentation)
    {
        InspectText(formatted, LogLeakLocation.FormattedMessage, categoryName, eventId, propertyName: null, unit: "formatted message");

        if (state is IEnumerable<KeyValuePair<string, object?>> entries)
        {
            EnsureCountableExtentWithinBudget(entries, "structured-state entry");
            using var enumerator = entries.GetEnumerator();
            while (true)
            {
                if (inspectionUnitsThisEvent >= options.MaximumInspectionUnitsPerEvent)
                {
                    ExceedAggregateBudget("per-event inspection-unit", options.MaximumInspectionUnitsPerEvent, inspectionUnitsThisEvent + 1);
                }

                if (!enumerator.MoveNext())
                {
                    break;
                }

                var entry = enumerator.Current;
                ConsumeInspectionUnit("structured-state entry");
                if (string.Equals(entry.Key, "{OriginalFormat}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (entry.Value is string text)
                {
                    InspectText(text, LogLeakLocation.StructuredProperty, categoryName, eventId, entry.Key, "structured property", countUnit: false);
                }
            }
        }

        currentScopeProvider.ForEachScope(static (scope, callbackState) => callbackState.InspectScope(scope), this);
        InspectText(exceptionRepresentation, LogLeakLocation.ExceptionRepresentation, categoryName, eventId, propertyName: null, unit: "exception representation");
    }

    private void InspectScope(object? scope)
    {
        ConsumeInspectionUnit("scope");
        if (scope is string text)
        {
            InspectText(text, LogLeakLocation.Scope, categoryNameThisEvent, eventIdThisEvent, propertyName: null, unit: "scope", countUnit: false);
        }
    }

    private void InspectText(
        string? text,
        LogLeakLocation location,
        string? categoryName,
        EventId eventId,
        string? propertyName,
        string unit,
        bool countUnit = true)
    {
        if (text is null)
        {
            return;
        }

        if (countUnit)
        {
            ConsumeInspectionUnit(unit);
        }

        EnsurePayloadWithinLimit(text, unit);
        foreach (var sentinel in sentinels)
        {
            if (ContainsOrdinal(text, sentinel.Value))
            {
                if (findingsThisEvent >= options.MaximumFindingsPerEvent)
                {
                    ExceedAggregateBudget("per-event finding", options.MaximumFindingsPerEvent, findingsThisEvent + 1);
                }

                if (findings.Count >= options.MaximumFindings)
                {
                    ExceedAggregateBudget("capture finding", options.MaximumFindings, findings.Count + 1);
                }

                findings.Add(new LogLeakFinding(
                    sentinel.Label,
                    location,
                    GetSafeMetadata(categoryName),
                    eventId.Id,
                    GetSafeMetadata(eventId.Name),
                    location == LogLeakLocation.StructuredProperty ? GetSafeMetadata(propertyName) : null));
                findingsThisEvent++;
            }
        }
    }

    private string? GetSafeMetadata(string? metadata)
    {
        if (metadata is null
            || metadata.Length == 0
            || metadata.Length > 256
            || metadata.Any(char.IsControl))
        {
            return null;
        }

        return sentinels.Any(sentinel => ContainsOrdinal(metadata, sentinel.Value))
            ? null
            : metadata;
    }

    private void ConsumeInspectionUnit(string unit)
    {
        if (inspectionUnitsThisEvent >= options.MaximumInspectionUnitsPerEvent)
        {
            ExceedAggregateBudget("per-event inspection-unit", options.MaximumInspectionUnitsPerEvent, inspectionUnitsThisEvent + 1);
        }

        inspectionUnitsThisEvent++;
    }

    private void EnsureCountableExtentWithinBudget<T>(IEnumerable<T> entries, string unit)
    {
        if (entries is ICollection<T> collection)
        {
            EnsureExtentWithinBudget(collection.Count, unit);
        }
        else if (entries is IReadOnlyCollection<T> readOnlyCollection)
        {
            EnsureExtentWithinBudget(readOnlyCollection.Count, unit);
        }
    }

    private void EnsureExtentWithinBudget(int extent, string unit)
    {
        if (extent < 0)
        {
            ExceedAggregateBudget(unit + " extent", 0, extent);
        }

        var observed = (long)inspectionUnitsThisEvent + extent;
        if (observed > options.MaximumInspectionUnitsPerEvent)
        {
            ExceedAggregateBudget("per-event inspection-unit (" + unit + " extent)", options.MaximumInspectionUnitsPerEvent, observed);
        }
    }

    private void EnsureTransientAllocationWithinLimit(long allocationStart)
    {
        if (allocationStart < 0)
        {
            return;
        }

        var allocated = GetAllocatedBytes() - allocationStart;
        if (allocated > options.MaximumTransientAllocationBytes)
        {
            ExceedAggregateBudget("per-event transient-allocation", options.MaximumTransientAllocationBytes, allocated);
        }
    }

    private void EnsurePayloadWithinLimit(string? text, string unit)
    {
        if (text is not null && text.Length > options.MaximumPayloadCharacters)
        {
            throw new PayloadLimitException(unit, options.MaximumPayloadCharacters);
        }
    }

    private void ExceedAggregateBudget(string budgetName, long maximum, long observed)
    {
        if (!aggregateBudgetExceeded)
        {
            aggregateBudgetExceeded = true;
            aggregateBudgetName = budgetName;
            aggregateBudgetLimit = maximum;
            aggregateBudgetObserved = observed;
        }

        throw new InspectionBudgetReachedException();
    }

    private LogLeakVerificationStatus GetStatus()
    {
        if (eventOverflowed || aggregateBudgetExceeded || payloadLimitExceeded || captureFailed)
        {
            return LogLeakVerificationStatus.Inconclusive;
        }

        return findings.Count == 0
            ? LogLeakVerificationStatus.Clean
            : LogLeakVerificationStatus.LeaksDetected;
    }

    private string GetInconclusiveReason()
    {
        if (eventOverflowed)
        {
            return "the capture event budget of " + options.MaximumCapturedEvents.ToString(System.Globalization.CultureInfo.InvariantCulture) + " events was reached";
        }

        if (aggregateBudgetExceeded)
        {
            return "the " + aggregateBudgetName + " budget of " + aggregateBudgetLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " was reached at " + aggregateBudgetObserved.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (payloadLimitExceeded)
        {
            return "an inspected text unit exceeded the configured payload limit of "
                + options.MaximumPayloadCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " characters";
        }

        return "the provider-boundary capture encountered an unsupported logging failure";
    }

    private static string GetExceptionRepresentation(Exception exception)
    {
        try
        {
            return exception.ToString();
        }
        catch (Exception)
        {
            throw new CaptureFailureException();
        }
    }

    private void TrackTelemetryBestEffort()
    {
        try
        {
            telemetry.TrackActivation();
        }
        catch (Exception)
        {
        }

        try
        {
            telemetry.TrackHeartbeat();
        }
        catch (Exception)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new LogLeakDisposedException();
        }
    }

    private static long GetAllocatedBytes()
    {
#if NET8_0_OR_GREATER
        return GC.GetAllocatedBytesForCurrentThread();
#else
        return GC.GetTotalMemory(forceFullCollection: false);
#endif
    }

    private static bool ContainsOrdinal(string text, string value)
    {
#if NET8_0_OR_GREATER
        return text.Contains(value, StringComparison.Ordinal);
#else
        return text.IndexOf(value, StringComparison.Ordinal) >= 0;
#endif
    }

    private static void ThrowIfNull<T>(T? value, string parameterName)
        where T : class
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(value, parameterName);
#else
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }
#endif
    }

    private sealed class RegisteredSentinel
    {
        public RegisteredSentinel(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }

        public string Value { get; }
    }

    private sealed class CaptureLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly LogLeakProbe owner;

        public CaptureLoggerProvider(LogLeakProbe owner)
        {
            this.owner = owner;
        }

        public ILogger CreateLogger(string categoryName) => owner.CreateLogger(categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => owner.SetScopeProvider(scopeProvider);

        public void Dispose() => owner.Dispose();
    }

    private sealed class CaptureLogger : ILogger
    {
        private readonly LogLeakProbe owner;
        private readonly string categoryName;

        public CaptureLogger(LogLeakProbe owner, string categoryName)
        {
            this.owner = owner;
            this.categoryName = categoryName;
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => owner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter is null)
            {
            ThrowIfNull(formatter, nameof(formatter));
            }

            if (IsEnabled(logLevel))
            {
                owner.Record(categoryName, logLevel, eventId, state, exception, formatter!);
            }
        }
    }

    private sealed class InspectionBudgetReachedException : Exception
    {
    }

    private sealed class PayloadLimitException : Exception
    {
        public PayloadLimitException(string unit, int maximumCharacters)
        {
            Unit = unit;
            MaximumCharacters = maximumCharacters;
        }

        public string Unit { get; }

        public int MaximumCharacters { get; }
    }

    private sealed class CaptureFailureException : Exception
    {
    }

    internal interface ILogLeakTelemetry
    {
        void TrackActivation();

        void TrackHeartbeat();
    }

    private sealed class SharedTelemetryClient : ILogLeakTelemetry
    {
        private readonly Client client = new("LogLeak", typeof(LogLeakProbe));

        public void TrackActivation() => client.TrackActivation();

        public void TrackHeartbeat() => client.TrackHeartbeat();
    }
}
