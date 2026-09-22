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
    private string[]? sentinelValuesThisEvent;
    private int capturedEventCount;
    private bool registrationFrozen;
    private bool disposed;
    private bool eventOverflowed;
    private bool aggregateBudgetExceeded;
    private string? aggregateBudgetName;
    private long aggregateBudgetLimit;
    private long aggregateBudgetObserved;
    private bool payloadLimitExceeded;
    private bool captureFailed;
    private bool reentrantCaptureDetected;
    private bool verificationDuringCaptureDetected;
    private bool eventInProgress;
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
    /// <remarks>
    /// Register this provider before opening scopes that the probe should observe. Scopes opened earlier are outside the
    /// provider's observed boundary and may not be captured. Disposing the provider also disposes this probe and clears retained state.
    /// </remarks>
    public ILoggerProvider Provider => provider;

    /// <summary>
    /// Registers one labeled test-only sentinel value.
    /// </summary>
    /// <param name="label">A short safe identifier used in findings.</param>
    /// <param name="value">The exact non-empty value to match in supported logging fields.</param>
    /// <returns>This probe, to support concise setup.</returns>
    /// <remarks>
    /// Labels are limited to 64 characters and may contain letters, digits, hyphens, underscores, periods, and colons.
    /// No registered label may textually contain a registered value, and no registered value may textually contain a
    /// registered label. These checks use exact ordinal comparison (case-sensitive, with no normalization) and run at
    /// every registration in either registration order. Registration closes when capture or verification begins; later
    /// additions are rejected so retained findings cannot be reinterpreted under a different sentinel set.
    /// </remarks>
    /// <exception cref="LogLeakConfigurationException">Thrown when the label or value is invalid.</exception>
    /// <exception cref="LogLeakDisposedException">Thrown when the probe has been disposed.</exception>
    public LogLeakProbe AddSecret(string label, string value)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var sentinelValues = GetSentinelValuesSnapshot();
            ValidateLabel(label, sentinelValues);
            ValidateValue(value, sentinelValues);

            if (value.Length > options.MaximumSentinelCharacters)
            {
                throw new LogLeakConfigurationException("The sentinel value exceeds the configured registration limit.", sentinelValues);
            }

            if (registrationFrozen)
            {
                throw new LogLeakConfigurationException("Sentinel registration is closed once capture or verification begins.", sentinelValues);
            }

            if (sentinels.Count >= options.MaximumSentinels)
            {
                throw new LogLeakConfigurationException("The sentinel registration limit was reached.", sentinelValues);
            }

            if (sentinels.Any(existing => string.Equals(existing.Label, label, StringComparison.Ordinal)))
            {
                throw new LogLeakConfigurationException("Sentinel labels must be unique.", sentinelValues);
            }

            if (ContainsOrdinal(label, value)
                || ContainsOrdinal(value, label)
                || sentinels.Any(existing =>
                    ContainsOrdinal(label, existing.Value)
                    || ContainsOrdinal(existing.Value, label)
                    || ContainsOrdinal(existing.Label, value)
                    || ContainsOrdinal(value, existing.Label)))
            {
                throw new LogLeakConfigurationException("Sentinel labels and values must not overlap.", sentinelValues);
            }

            sentinels.Add(new RegisteredSentinel(label, value));
        }

        return this;
    }

    /// <summary>
    /// Performs a bounded verification and returns a safe, explicit result.
    /// </summary>
    /// <returns>A clean, leak-detected, or inconclusive result with safe findings only.</returns>
    /// <remarks>
    /// The first capture or verification freezes sentinel registration. Only a clean verification requests best-effort
    /// activation and heartbeat telemetry; detected leaks and inconclusive captures do not.
    /// </remarks>
    /// <exception cref="LogLeakDisposedException">Thrown when the probe has been disposed.</exception>
    public LogLeakVerificationResult Verify()
    {
        LogLeakVerificationResult result;
        lock (gate)
        {
            ThrowIfDisposed();
            registrationFrozen = true;
            var status = GetStatus();
            var sentinelValues = GetSentinelValuesSnapshot();
            var verificationDuringCapture = eventInProgress;
            result = new LogLeakVerificationResult(
                status,
                findings.ToArray(),
                capturedEventCount,
                status == LogLeakVerificationStatus.Inconclusive ? GetInconclusiveReason(sentinelValues) : null,
                sentinelValues,
                verificationDuringCapture);
        }

        if (result.Status == LogLeakVerificationStatus.Clean)
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
            throw new LogLeakInconclusiveException(
                result.InconclusiveReason!,
                result.Findings,
                result.InconclusiveDiagnostic,
                result.OccurredDuringCapture);
        }

        if (result.Findings.Count > 0)
        {
            throw new LogLeakAssertionException(result.Findings, result.AssertionDiagnostic);
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
            sentinelValuesThisEvent = null;
            capturedEventCount = 0;
            registrationFrozen = false;
            eventOverflowed = false;
            aggregateBudgetExceeded = false;
            aggregateBudgetName = null;
            aggregateBudgetLimit = 0;
            aggregateBudgetObserved = 0;
            payloadLimitExceeded = false;
            captureFailed = false;
            reentrantCaptureDetected = false;
            verificationDuringCaptureDetected = false;
            eventInProgress = false;
            inspectionUnitsThisEvent = 0;
            findingsThisEvent = 0;
            scopeProvider = new LoggerExternalScopeProvider();
            disposed = true;
        }
    }

    private static void ValidateLabel(string label, IReadOnlyList<string> sentinelValues)
    {
        if (string.IsNullOrWhiteSpace(label)
            || label.Length > 64
            || label.Any(character => !(char.IsLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':')))
        {
            throw new LogLeakConfigurationException("Sentinel labels must be short safe identifiers.", sentinelValues);
        }
    }

    private static void ValidateValue(string value, IReadOnlyList<string> sentinelValues)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new LogLeakConfigurationException("Sentinel values must be non-empty.", sentinelValues);
        }
    }

    private CaptureLogger CreateLogger(string categoryName)
    {
        lock (gate)
        {
            ThrowIfNull(categoryName, nameof(categoryName), GetSentinelValuesSnapshot());
            ThrowIfDisposed();
            return new CaptureLogger(this, categoryName);
        }
    }

    private SentinelSafeScopeHandle BeginScope<TState>(TState state)
        where TState : notnull
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return new SentinelSafeScopeHandle(scopeProvider.Push(state));
        }
    }

    private void SetScopeProvider(IExternalScopeProvider newScopeProvider)
    {
        lock (gate)
        {
            ThrowIfNull(newScopeProvider, nameof(newScopeProvider), GetSentinelValuesSnapshot());
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

            if (eventInProgress)
            {
                reentrantCaptureDetected = true;
                return;
            }

            registrationFrozen = true;

            if (capturedEventCount >= options.MaximumCapturedEvents)
            {
                eventOverflowed = true;
                return;
            }

            inspectionUnitsThisEvent = 0;
            findingsThisEvent = 0;
            categoryNameThisEvent = categoryName;
            eventIdThisEvent = eventId;
            sentinelValuesThisEvent = GetSentinelValuesSnapshot();
            eventInProgress = true;
            var verificationInterruptedCapture = false;
            try
            {
                string? formatted = null;
                string? exceptionRepresentation = null;
                try
                {
                    formatted = formatter(state, exception);
                }
                catch (LogLeakInconclusiveException inconclusive) when (inconclusive.OccurredDuringCapture)
                {
                    verificationInterruptedCapture = true;
                }

                EnsurePayloadWithinLimit(formatted, "formatted message");

                if (exception is not null)
                {
                    try
                    {
                        exceptionRepresentation = GetExceptionRepresentation(exception);
                    }
                    catch (LogLeakInconclusiveException inconclusive) when (inconclusive.OccurredDuringCapture)
                    {
                        verificationInterruptedCapture = true;
                    }
                }

                EnsurePayloadWithinLimit(exceptionRepresentation, "exception representation");

                try
                {
                    Inspect(categoryName, eventId, formatted, state, scopeProvider, exceptionRepresentation);
                }
                catch (LogLeakInconclusiveException inconclusive) when (inconclusive.OccurredDuringCapture)
                {
                    verificationInterruptedCapture = true;
                }

                if (verificationInterruptedCapture)
                {
                    verificationDuringCaptureDetected = true;
                }
                else
                {
                    capturedEventCount++;
                }
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
                eventInProgress = false;
                categoryNameThisEvent = null;
                eventIdThisEvent = default;
                sentinelValuesThisEvent = null;
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
            InspectStringValuedEntries(entries, LogLeakLocation.StructuredProperty, categoryName, eventId, "structured-state entry", "structured property");
        }
        else if (state is IEnumerable<KeyValuePair<string, string>> stringEntries)
        {
            InspectStringValuedEntries(stringEntries, LogLeakLocation.StructuredProperty, categoryName, eventId, "structured-state entry", "structured property");
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
        else if (scope is IEnumerable<KeyValuePair<string, object?>> entries)
        {
            InspectStringValuedEntries(entries, LogLeakLocation.Scope, categoryNameThisEvent, eventIdThisEvent, "scope entry", "scope value");
        }
        else if (scope is IEnumerable<KeyValuePair<string, string>> stringEntries)
        {
            InspectStringValuedEntries(stringEntries, LogLeakLocation.Scope, categoryNameThisEvent, eventIdThisEvent, "scope entry", "scope value");
        }
    }

    private void InspectStringValuedEntries<TValue>(
        IEnumerable<KeyValuePair<string, TValue>> entries,
        LogLeakLocation location,
        string? categoryName,
        EventId eventId,
        string extentUnit,
        string textUnit)
    {
        EnsureCountableExtentWithinBudget(entries, extentUnit);
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
            ConsumeInspectionUnit(extentUnit);
            if (string.Equals(entry.Key, "{OriginalFormat}", StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Value is string text)
            {
                InspectText(
                    text,
                    location,
                    categoryName,
                    eventId,
                    location == LogLeakLocation.StructuredProperty ? entry.Key : null,
                    textUnit,
                    countUnit: false);
            }
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
                    GetSafeEventId(eventId.Id),
                    GetSafeMetadata(eventId.Name),
                    location == LogLeakLocation.StructuredProperty ? GetSafeMetadata(propertyName) : null,
                    sentinelValuesThisEvent ?? Array.Empty<string>()));
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

    private int? GetSafeEventId(int eventId)
    {
        var rendered = eventId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return sentinels.Any(sentinel => ContainsOrdinal(rendered, sentinel.Value))
            ? null
            : eventId;
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
        if (eventInProgress
            || verificationDuringCaptureDetected
            || reentrantCaptureDetected
            || eventOverflowed
            || aggregateBudgetExceeded
            || payloadLimitExceeded
            || captureFailed)
        {
            return LogLeakVerificationStatus.Inconclusive;
        }

        return findings.Count == 0
            ? LogLeakVerificationStatus.Clean
            : LogLeakVerificationStatus.LeaksDetected;
    }

    private string GetInconclusiveReason(IReadOnlyList<string> sentinelValues)
    {
        string reason;
        if (eventInProgress)
        {
            reason = "verification was requested while provider-boundary capture was active";
        }
        else if (verificationDuringCaptureDetected)
        {
            reason = "verification was requested during provider-boundary capture and the callback did not complete";
        }
        else if (reentrantCaptureDetected)
        {
            reason = "the provider-boundary capture detected reentrant logging through the same probe";
        }
        else if (eventOverflowed)
        {
            reason = "the capture event budget of " + options.MaximumCapturedEvents.ToString(System.Globalization.CultureInfo.InvariantCulture) + " events was reached";
        }
        else if (aggregateBudgetExceeded)
        {
            reason = "the " + aggregateBudgetName + " budget of " + aggregateBudgetLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " was reached at " + aggregateBudgetObserved.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (payloadLimitExceeded)
        {
            reason = "an inspected text unit exceeded the configured payload limit of "
                + options.MaximumPayloadCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " characters";
        }
        else
        {
            reason = "a formatter, exception representation, structured-state, or scope callback failed during provider-boundary capture";
        }

        return LogLeakDiagnostics.SafeInconclusiveReason(reason, sentinelValues);
    }

    private static string GetExceptionRepresentation(Exception exception)
    {
        try
        {
            return exception.ToString();
        }
        catch (LogLeakInconclusiveException inconclusive) when (inconclusive.OccurredDuringCapture)
        {
            throw;
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

    private string[] GetSentinelValuesSnapshot()
        => sentinels.Select(static sentinel => sentinel.Value).ToArray();

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
        => ThrowIfNull(value, parameterName, Array.Empty<string>());

    private static void ThrowIfNull<T>(T? value, string parameterName, IReadOnlyList<string> sentinelValues)
        where T : class
    {
        if (value is null)
        {
            var safeParameterName = LogLeakDiagnostics.ContainsRegisteredSentinel(parameterName, sentinelValues)
                ? null
                : parameterName;
            throw new SentinelSafeArgumentNullException(safeParameterName, sentinelValues);
        }
    }

    private void ThrowIfNullArgument<T>(T? value, string parameterName)
        where T : class
    {
        lock (gate)
        {
            ThrowIfNull(value, parameterName, GetSentinelValuesSnapshot());
        }
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

    private sealed class SentinelSafeArgumentNullException : ArgumentNullException
    {
        private readonly string diagnostic;

        public SentinelSafeArgumentNullException(string? parameterName, IReadOnlyList<string> sentinelValues)
            : base(parameterName)
        {
            diagnostic = LogLeakDiagnostics.SafeArgumentNullMessage(parameterName, sentinelValues);
        }

        public override string Message => diagnostic;

        public override string ToString() => diagnostic;
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

    private sealed class SentinelSafeScopeHandle : IDisposable
    {
        private IDisposable? inner;

        public SentinelSafeScopeHandle(IDisposable inner)
        {
            this.inner = inner;
        }

        public void Dispose()
            => Interlocked.Exchange(ref inner, null)?.Dispose();

        public override string ToString() => string.Empty;
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
                owner.ThrowIfNullArgument(formatter, nameof(formatter));
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
