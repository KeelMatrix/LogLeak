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

internal sealed class CaptureOptions
{
    public CaptureOptions(int maximumEvents, int maximumSentinels = 128, int maximumFindings = 4096, int maximumSentinelCharacters = 4096)
    {
        if (maximumEvents <= 0 || maximumSentinels <= 0 || maximumFindings <= 0 || maximumSentinelCharacters <= 0)
        {
            throw new ProbeConfigurationException("Capture limits must be greater than zero.");
        }

        MaximumEvents = maximumEvents;
        MaximumSentinels = maximumSentinels;
        MaximumFindings = maximumFindings;
        MaximumSentinelCharacters = maximumSentinelCharacters;
    }

    public int MaximumEvents { get; }

    public int MaximumSentinels { get; }

    public int MaximumFindings { get; }

    public int MaximumSentinelCharacters { get; }
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
    public ProbeOverflowException(int maximumEvents) : base($"The capture limit of {maximumEvents} events was reached; verification is inconclusive.")
    {
    }
}

internal sealed class ProbeCaptureException : ProbeException
{
    public ProbeCaptureException() : base("The provider-boundary capture encountered an unsupported logging failure; verification is inconclusive.")
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
    private bool _captureFailed;

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
            _disposed = true;
        }
    }

    internal void Record<TState>(string categoryName, LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            if (_disposed || _overflowed || _captureFailed)
            {
                return;
            }

            if (_capturedEventCount >= _options.MaximumEvents)
            {
                _overflowed = true;
                return;
            }

            try
            {
                var formatted = formatter(state, exception);
                var structuredValues = ExtractStructuredValues(state);
                var scopes = CaptureScopes(_scopeProvider);
                var exceptionRepresentation = exception is null ? null : GetExceptionRepresentation(exception);
                var started = Stopwatch.GetTimestamp();
                var eventFindings = Inspect(formatted, structuredValues, scopes, exceptionRepresentation);
                _matchingTicks += Stopwatch.GetTimestamp() - started;

                if (eventFindings.Count > _options.MaximumFindings - _findings.Count)
                {
                    _overflowed = true;
                    return;
                }

                _capturedEventCount++;
                _findings.AddRange(eventFindings);
            }
            catch
            {
                _captureFailed = true;
            }
        }
    }

    private List<LeakFinding> Inspect(string? formatted, IReadOnlyList<string> structuredValues, IReadOnlyList<string> scopes, string? exceptionRepresentation)
    {
        var findings = new List<LeakFinding>();
        foreach (var sentinel in _sentinels)
        {
            if (ContainsLiteral(formatted, sentinel.Value))
            {
                findings.Add(new LeakFinding(sentinel.Label, LeakLocation.FormattedMessage));
            }

            if (structuredValues.Any(value => ContainsLiteral(value, sentinel.Value)))
            {
                findings.Add(new LeakFinding(sentinel.Label, LeakLocation.StructuredProperty));
            }

            if (scopes.Any(value => ContainsLiteral(value, sentinel.Value)))
            {
                findings.Add(new LeakFinding(sentinel.Label, LeakLocation.Scope));
            }

            if (ContainsLiteral(exceptionRepresentation, sentinel.Value))
            {
                findings.Add(new LeakFinding(sentinel.Label, LeakLocation.ExceptionRepresentation));
            }
        }

        return findings;
    }

    private void ThrowIfInconclusive()
    {
        if (_overflowed)
        {
            throw new ProbeOverflowException(_options.MaximumEvents);
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

    private static IReadOnlyList<string> ExtractStructuredValues<TState>(TState state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> entries)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Value is string text)
            {
                values.Add(text);
            }
        }

        return values;
    }

    private static List<string> CaptureScopes(IExternalScopeProvider scopeProvider)
    {
        var scopes = new List<string>();
        scopeProvider.ForEachScope(static (scope, destination) =>
        {
            if (scope is string text)
            {
                destination.Add(text);
            }
        }, scopes);
        return scopes;
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
