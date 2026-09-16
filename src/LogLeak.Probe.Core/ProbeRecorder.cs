using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LogLeak.Probe.Core;

internal enum LeakLocation
{
    FormattedMessage,
    StructuredProperty,
    Scope,
    ExceptionRepresentation
}

internal interface ICountableScopeProvider
{
    int ScopeCount { get; }
}

internal sealed class CaptureOptions
{
    public CaptureOptions(
        int maximumEvents,
        int maximumSentinels = 128,
        int maximumFindings = 4096,
        int maximumSentinelCharacters = 4096,
        int maximumPayloadCharacters = 4096,
        int maximumInspectionUnitsPerEvent = 1024,
        int maximumFindingsPerEvent = 256,
        long maximumTransientAllocationBytes = 1_048_576)
    {
        if (maximumEvents <= 0 || maximumSentinels <= 0 || maximumFindings <= 0 || maximumSentinelCharacters <= 0 || maximumPayloadCharacters <= 0 || maximumInspectionUnitsPerEvent <= 0 || maximumFindingsPerEvent <= 0 || maximumTransientAllocationBytes <= 0)
        {
            throw new ProbeConfigurationException("Capture limits must be greater than zero.");
        }

        MaximumEvents = maximumEvents;
        MaximumSentinels = maximumSentinels;
        MaximumFindings = maximumFindings;
        MaximumSentinelCharacters = maximumSentinelCharacters;
        MaximumPayloadCharacters = maximumPayloadCharacters;
        MaximumInspectionUnitsPerEvent = maximumInspectionUnitsPerEvent;
        MaximumFindingsPerEvent = maximumFindingsPerEvent;
        MaximumTransientAllocationBytes = maximumTransientAllocationBytes;
    }

    public int MaximumEvents { get; }

    public int MaximumSentinels { get; }

    public int MaximumFindings { get; }

    public int MaximumSentinelCharacters { get; }

    public int MaximumPayloadCharacters { get; }

    public int MaximumInspectionUnitsPerEvent { get; }

    public int MaximumFindingsPerEvent { get; }

    public long MaximumTransientAllocationBytes { get; }
}

internal sealed class LeakFinding
{
    public LeakFinding(string label, LeakLocation location)
    {
        Label = label;
        Location = location;
    }

    public string Label { get; }

    public LeakLocation Location { get; }

    public override string ToString() => $"Sentinel label '{Label}' reached {Location}.";
}

internal class ProbeException : Exception
{
    protected ProbeException(string message) : base(message)
    {
    }
}

internal sealed class ProbeConfigurationException : ProbeException
{
    public ProbeConfigurationException(string message) : base(message)
    {
    }
}

internal sealed class ProbeDisposedException : ProbeException
{
    public ProbeDisposedException() : base("The probe has already been disposed.")
    {
    }
}

internal sealed class ProbeOverflowException : ProbeException
{
    public ProbeOverflowException(int maximumEvents) : base($"The capture event budget of {maximumEvents} events was reached; verification is inconclusive.")
    {
    }

    public ProbeOverflowException(string budgetName, long maximum, long observed)
        : base($"The {budgetName} budget of {maximum} was reached at {observed}; verification is inconclusive.")
    {
    }
}

internal sealed class ProbeInspectionBudgetReachedException : Exception
{
}

internal sealed class ProbeCaptureException : ProbeException
{
    public ProbeCaptureException() : base("The provider-boundary capture encountered an unsupported logging failure; verification is inconclusive.")
    {
    }
}

internal sealed class ProbePayloadLimitException : ProbeException
{
    public ProbePayloadLimitException(string unit, int maximumCharacters)
        : base($"The {unit} exceeded the configured payload limit of {maximumCharacters} characters; verification is inconclusive.")
    {
    }
}

internal sealed class LeakAssertionException : ProbeException
{
    public LeakAssertionException(IReadOnlyList<LeakFinding> findings)
        : base(CreateMessage(findings))
    {
        Findings = findings;
    }

    public IReadOnlyList<LeakFinding> Findings { get; }

    private static string CreateMessage(IReadOnlyList<LeakFinding> findings)
    {
        var lines = findings.Select(finding => $"- {finding}");
        return $"Registered sentinel values reached the logging event stream ({findings.Count} finding(s)).{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }
}

internal sealed class BoundaryProbe : ILoggerProvider, ISupportExternalScope
{
    private readonly object _gate = new();
    private readonly CaptureOptions _options;
    private readonly List<RegisteredSentinel> _sentinels = new();
    private readonly List<LeakFinding> _findings = new();
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private long _matchingTicks;
    private int _capturedEventCount;
    private bool _disposed;
    private bool _overflowed;
    private bool _aggregateBudgetExceeded;
    private string? _aggregateBudgetName;
    private long _aggregateBudgetLimit;
    private long _aggregateBudgetObserved;
    private bool _payloadLimitExceeded;
    private bool _captureFailed;
    private int _inspectionUnitsThisEvent;
    private int _findingsThisEvent;
    private int _lastEventInspectionUnits;
    private int _lastEventFindings;

    public BoundaryProbe(CaptureOptions options)
    {
        _options = options;
    }

    public int CapturedEventCount
    {
        get
        {
            lock (_gate)
            {
                return _capturedEventCount;
            }
        }
    }

    public int RegisteredSentinelCount
    {
        get
        {
            lock (_gate)
            {
                return _sentinels.Count;
            }
        }
    }

    public bool Overflowed
    {
        get
        {
            lock (_gate)
            {
                return _overflowed;
            }
        }
    }

    public bool PayloadLimitExceeded
    {
        get
        {
            lock (_gate)
            {
                return _payloadLimitExceeded;
            }
        }
    }

    public int MaximumInspectionUnitsPerEvent => _options.MaximumInspectionUnitsPerEvent;

    public int MaximumFindingsPerEvent => _options.MaximumFindingsPerEvent;

    public long MaximumTransientAllocationBytes => _options.MaximumTransientAllocationBytes;

    public bool AggregateBudgetExceeded
    {
        get
        {
            lock (_gate)
            {
                return _aggregateBudgetExceeded;
            }
        }
    }

    public string? AggregateBudgetName
    {
        get
        {
            lock (_gate)
            {
                return _aggregateBudgetName;
            }
        }
    }

    public int LastEventInspectionUnits
    {
        get
        {
            lock (_gate)
            {
                return _lastEventInspectionUnits;
            }
        }
    }

    public int LastEventFindings
    {
        get
        {
            lock (_gate)
            {
                return _lastEventFindings;
            }
        }
    }

    public TimeSpan MatchingElapsed
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromSeconds(_matchingTicks / (double)Stopwatch.Frequency);
            }
        }
    }

    public bool HasFinding(string label, LeakLocation location)
    {
        ArgumentNullException.ThrowIfNull(label);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _findings.Any(finding => finding.Label == label && finding.Location == location);
        }
    }

    public void AddSentinel(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 64 || label.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            throw new ProbeConfigurationException("Sentinel labels must be short safe identifiers.");
        }

        if (string.IsNullOrEmpty(value))
        {
            throw new ProbeConfigurationException("Sentinel values must be non-empty.");
        }

        if (value.Length > _options.MaximumSentinelCharacters)
        {
            throw new ProbeConfigurationException("The sentinel value exceeds the configured registration limit.");
        }

        if (value.Contains(label, StringComparison.Ordinal) || label.Contains(value, StringComparison.Ordinal))
        {
            throw new ProbeConfigurationException("A sentinel label must not overlap its sentinel value.");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_sentinels.Count >= _options.MaximumSentinels)
            {
                throw new ProbeConfigurationException("The sentinel registration limit was reached.");
            }

            _sentinels.Add(new RegisteredSentinel(label, value));
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);
        return new BoundaryLogger(this, categoryName);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeProvider);
        lock (_gate)
        {
            ThrowIfDisposed();
            _scopeProvider = scopeProvider;
        }
    }

    public IReadOnlyList<LeakFinding> FindLeaks()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfInconclusive();
            return _findings.ToArray();
        }
    }

    public void AssertNoLeaks()
    {
        var findings = FindLeaks();
        if (findings.Count > 0)
        {
            throw new LeakAssertionException(findings);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _findings.Clear();
            _sentinels.Clear();
            _capturedEventCount = 0;
            _matchingTicks = 0;
            _overflowed = false;
            _aggregateBudgetExceeded = false;
            _aggregateBudgetName = null;
            _aggregateBudgetLimit = 0;
            _aggregateBudgetObserved = 0;
            _payloadLimitExceeded = false;
            _captureFailed = false;
            _inspectionUnitsThisEvent = 0;
            _findingsThisEvent = 0;
            _lastEventInspectionUnits = 0;
            _lastEventFindings = 0;
            _disposed = true;
        }
    }

    internal void Record<TState>(string categoryName, LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            if (_disposed || _overflowed || _payloadLimitExceeded || _captureFailed)
            {
                return;
            }

            if (_capturedEventCount >= _options.MaximumEvents)
            {
                _overflowed = true;
                return;
            }

            _inspectionUnitsThisEvent = 0;
            _findingsThisEvent = 0;
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                var formatted = formatter(state, exception);
                EnsurePayloadWithinLimit(formatted, "formatted message");
                EnsureTransientAllocationWithinLimit(allocationStart);
                var exceptionRepresentation = exception is null ? null : GetExceptionRepresentation(exception);
                EnsurePayloadWithinLimit(exceptionRepresentation, "exception representation");
                EnsureTransientAllocationWithinLimit(allocationStart);
                var started = Stopwatch.GetTimestamp();
                Inspect(formatted, state, _scopeProvider, exceptionRepresentation);
                _matchingTicks += Stopwatch.GetTimestamp() - started;
                EnsureTransientAllocationWithinLimit(allocationStart);

                _capturedEventCount++;
            }
            catch (ProbeInspectionBudgetReachedException)
            {
            }
            catch (ProbePayloadLimitException)
            {
                _payloadLimitExceeded = true;
            }
            catch
            {
                _captureFailed = true;
            }
            finally
            {
                _lastEventInspectionUnits = _inspectionUnitsThisEvent;
                _lastEventFindings = _findingsThisEvent;
            }
        }
    }

    private void Inspect<TState>(string? formatted, TState state, IExternalScopeProvider scopeProvider, string? exceptionRepresentation)
    {
        InspectText(formatted, LeakLocation.FormattedMessage, "formatted message");

        if (state is IEnumerable<KeyValuePair<string, object?>> entries)
        {
            EnsureCountableExtentWithinBudget(entries, "structured-state entry");
            using var enumerator = entries.GetEnumerator();
            while (true)
            {
                if (_inspectionUnitsThisEvent >= _options.MaximumInspectionUnitsPerEvent)
                {
                    ExceedAggregateBudget("per-event inspection-unit", _options.MaximumInspectionUnitsPerEvent, _inspectionUnitsThisEvent + 1);
                }

                if (!enumerator.MoveNext())
                {
                    break;
                }

                var entry = enumerator.Current;
                ConsumeInspectionUnit("structured-state entry");
                if (entry.Key is "{OriginalFormat}")
                {
                    continue;
                }

                if (entry.Value is string text)
                {
                    InspectText(text, LeakLocation.StructuredProperty, "structured property", countUnit: false);
                }
            }
        }

        if (scopeProvider is ICountableScopeProvider countableScopeProvider)
        {
            EnsureExtentWithinBudget(countableScopeProvider.ScopeCount, "scope");
        }

        scopeProvider.ForEachScope(static (scope, owner) => owner.InspectScope(scope), this);

        InspectText(exceptionRepresentation, LeakLocation.ExceptionRepresentation, "exception representation");
    }

    private void InspectScope(object? scope)
    {
        ConsumeInspectionUnit("scope");
        if (scope is string text)
        {
            InspectText(text, LeakLocation.Scope, "scope", countUnit: false);
        }
    }

    private void InspectText(string? text, LeakLocation location, string unit, bool countUnit = true)
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

        foreach (var sentinel in _sentinels)
        {
            if (ContainsLiteral(text, sentinel.Value))
            {
                if (_findingsThisEvent >= _options.MaximumFindingsPerEvent)
                {
                    ExceedAggregateBudget("per-event finding", _options.MaximumFindingsPerEvent, _findingsThisEvent + 1);
                }

                if (_findings.Count >= _options.MaximumFindings)
                {
                    ExceedAggregateBudget("capture finding", _options.MaximumFindings, _findings.Count + 1);
                }

                _findings.Add(new LeakFinding(sentinel.Label, location));
                _findingsThisEvent++;
            }
        }
    }

    private void ConsumeInspectionUnit(string unit)
    {
        if (_inspectionUnitsThisEvent >= _options.MaximumInspectionUnitsPerEvent)
        {
            ExceedAggregateBudget("per-event inspection-unit", _options.MaximumInspectionUnitsPerEvent, _inspectionUnitsThisEvent + 1);
        }

        _inspectionUnitsThisEvent++;
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
            ExceedAggregateBudget($"{unit} extent", 0, extent);
        }

        var observed = (long)_inspectionUnitsThisEvent + extent;
        if (observed > _options.MaximumInspectionUnitsPerEvent)
        {
            ExceedAggregateBudget($"per-event inspection-unit ({unit} extent)", _options.MaximumInspectionUnitsPerEvent, observed);
        }
    }

    private void EnsureTransientAllocationWithinLimit(long allocationStart)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        if (allocated > _options.MaximumTransientAllocationBytes)
        {
            ExceedAggregateBudget("per-event transient-allocation", _options.MaximumTransientAllocationBytes, allocated);
        }
    }

    private void ExceedAggregateBudget(string budgetName, long maximum, long observed)
    {
        if (!_aggregateBudgetExceeded)
        {
            _aggregateBudgetExceeded = true;
            _aggregateBudgetName = budgetName;
            _aggregateBudgetLimit = maximum;
            _aggregateBudgetObserved = observed;
        }

        throw new ProbeInspectionBudgetReachedException();
    }

    private void EnsurePayloadWithinLimit(string? text, string unit)
    {
        if (text is not null && text.Length > _options.MaximumPayloadCharacters)
        {
            throw new ProbePayloadLimitException(unit, _options.MaximumPayloadCharacters);
        }
    }

    private void ThrowIfInconclusive()
    {
        if (_overflowed)
        {
            throw new ProbeOverflowException(_options.MaximumEvents);
        }

        if (_aggregateBudgetExceeded)
        {
            throw new ProbeOverflowException(_aggregateBudgetName!, _aggregateBudgetLimit, _aggregateBudgetObserved);
        }

        if (_payloadLimitExceeded)
        {
            throw new ProbePayloadLimitException("inspected text unit", _options.MaximumPayloadCharacters);
        }

        if (_captureFailed)
        {
            throw new ProbeCaptureException();
        }
    }

    private static bool ContainsLiteral(string? text, string literal) => text is not null && text.Contains(literal, StringComparison.Ordinal);

    private static string GetExceptionRepresentation(Exception exception)
    {
        try
        {
            return exception.ToString();
        }
        catch
        {
            throw new ProbeCaptureException();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ProbeDisposedException();
        }
    }

    private sealed record RegisteredSentinel(string Label, string Value);

    private sealed class BoundaryLogger : ILogger
    {
        private readonly BoundaryProbe _owner;
        private readonly string _categoryName;

        public BoundaryLogger(BoundaryProbe owner, string categoryName)
        {
            _owner = owner;
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _owner._scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                _owner.Record(_categoryName, logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
