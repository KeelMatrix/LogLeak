namespace KeelMatrix.LogLeak;

/// <summary>
/// Represents invalid sentinel or capture-limit configuration.
/// </summary>
public sealed class LogLeakConfigurationException : Exception
{
    internal LogLeakConfigurationException(string message, IReadOnlyList<string>? sentinelValues = null)
        : base(LogLeakDiagnostics.SafeText(message, sentinelValues ?? Array.Empty<string>()))
    {
    }

    /// <summary>Returns the safe configuration diagnostic.</summary>
    public override string ToString() => Message;
}

/// <summary>
/// Represents a verification that could not safely reach a conclusive result.
/// </summary>
public sealed class LogLeakInconclusiveException : Exception
{
    internal LogLeakInconclusiveException(
        string reason,
        IReadOnlyList<LogLeakFinding> findings,
        string diagnostic)
        : base(diagnostic)
    {
        Reason = reason;
        Findings = findings;
    }

    /// <summary>
    /// Gets the safe explanation for why verification was inconclusive.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// Gets safe findings observed before the inconclusive condition.
    /// </summary>
    public IReadOnlyList<LogLeakFinding> Findings { get; }

    /// <summary>Returns the safe inconclusive-verification diagnostic.</summary>
    public override string ToString() => Message;
}

/// <summary>
/// Represents a completed verification that found registered sentinel text in a supported logging field.
/// </summary>
public sealed class LogLeakAssertionException : Exception
{
    internal LogLeakAssertionException(IReadOnlyList<LogLeakFinding> findings, string diagnostic)
        : base(diagnostic)
    {
        Findings = findings;
    }

    /// <summary>
    /// Gets safe findings for the completed verification.
    /// </summary>
    public IReadOnlyList<LogLeakFinding> Findings { get; }

    /// <summary>Returns the safe leak-verification diagnostic.</summary>
    public override string ToString() => Message;
}

/// <summary>
/// Represents an operation attempted after a probe was disposed.
/// </summary>
public sealed class LogLeakDisposedException : Exception
{
    internal LogLeakDisposedException()
        : base(string.Empty)
    {
    }

    /// <summary>Returns an empty diagnostic so disposal errors cannot echo registered values.</summary>
    public override string ToString() => string.Empty;
}
