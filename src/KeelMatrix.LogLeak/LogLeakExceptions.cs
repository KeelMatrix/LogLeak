namespace KeelMatrix.LogLeak;

/// <summary>
/// Represents invalid sentinel or capture-limit configuration.
/// </summary>
public sealed class LogLeakConfigurationException : Exception
{
    internal LogLeakConfigurationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Represents a verification that could not safely reach a conclusive result.
/// </summary>
public sealed class LogLeakInconclusiveException : Exception
{
    internal LogLeakInconclusiveException(
        string reason,
        IReadOnlyList<LogLeakFinding> findings)
        : base(CreateMessage(reason))
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

    private static string CreateMessage(string reason)
        => "LogLeak verification is inconclusive: " + reason;
}

/// <summary>
/// Represents a completed verification that found registered sentinel text in a supported logging field.
/// </summary>
public sealed class LogLeakAssertionException : Exception
{
    internal LogLeakAssertionException(IReadOnlyList<LogLeakFinding> findings)
        : base(CreateMessage(findings))
    {
        Findings = findings;
    }

    /// <summary>
    /// Gets safe findings for the completed verification.
    /// </summary>
    public IReadOnlyList<LogLeakFinding> Findings { get; }

    private static string CreateMessage(IReadOnlyList<LogLeakFinding> findings)
    {
        var lines = findings.Select(static finding => "- " + finding.ToString());
        return "Registered sentinel values reached the logging event stream ("
            + findings.Count
            + " finding(s))."
            + Environment.NewLine
            + string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Represents an operation attempted after a probe was disposed.
/// </summary>
public sealed class LogLeakDisposedException : Exception
{
    internal LogLeakDisposedException()
        : base("The LogLeak probe has already been disposed.")
    {
    }
}
