using KeelMatrix.LogLeak;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KeelMatrix.LogLeak.Tests;

public sealed partial class LogLeakProbeTests
{
    [Fact]
    public void Detects_formatted_template_and_source_generated_messages()
    {
        const string sentinel = "synthetic-message-7f4b";
        using var probe = new LogLeakProbe().AddSecret("message", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("PaymentClient");

        logger.LogInformation("message template {Value}", sentinel);
        GeneratedLogging.Write(logger, sentinel);

        var result = probe.Verify();

        Assert.Equal(LogLeakVerificationStatus.LeaksDetected, result.Status);
        Assert.Contains(result.Findings, finding => finding.Location == LogLeakLocation.FormattedMessage);
        Assert.Contains(result.Findings, finding => finding.Location == LogLeakLocation.StructuredProperty);
    }

    [Fact]
    public void Detects_direct_string_structured_property()
    {
        const string sentinel = "synthetic-property-2c8d";
        using var probe = new LogLeakProbe().AddSecret("authorization", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("PaymentClient");
        var state = new[]
        {
            new KeyValuePair<string, object?>("Authorization", sentinel),
            new KeyValuePair<string, object?>("{OriginalFormat}", "property event")
        };

        logger.Log(LogLevel.Information, new EventId(42, "PaymentRequest"), state, null, static (_, _) => "property event");

        var finding = Assert.Single(probe.Verify().Findings);
        Assert.Equal(LogLeakLocation.StructuredProperty, finding.Location);
        Assert.Equal("Authorization", finding.PropertyName);
        Assert.Equal("PaymentClient", finding.CategoryName);
        Assert.Equal(42, finding.EventId);
        Assert.Equal("PaymentRequest", finding.EventName);
    }

    [Fact]
    public void Detects_nested_scope_string_state()
    {
        const string sentinel = "synthetic-scope-a1d9";
        using var probe = new LogLeakProbe().AddSecret("scope", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("PaymentClient");

        using (logger.BeginScope("outer scope"))
        using (logger.BeginScope("inner scope " + sentinel))
        {
            logger.LogInformation("scope event");
        }

        var finding = Assert.Single(probe.Verify().Findings);
        Assert.Equal(LogLeakLocation.Scope, finding.Location);
    }

    [Fact]
    public void Detects_exception_representation()
    {
        const string sentinel = "synthetic-exception-c34e";
        using var probe = new LogLeakProbe().AddSecret("exception", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("PaymentClient");

        logger.LogError(new InvalidOperationException("exception details " + sentinel), "exception event");

        var finding = Assert.Single(probe.Verify().Findings);
        Assert.Equal(LogLeakLocation.ExceptionRepresentation, finding.Location);
    }

    [Fact]
    public void Equivalent_event_corpus_without_registered_sentinel_is_clean()
    {
        const string sentinel = "synthetic-absent-8b21";
        using var probe = new LogLeakProbe().AddSecret("secret", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("PaymentClient");
        var state = new[] { new KeyValuePair<string, object?>("Authorization", "ordinary-value") };

        logger.LogInformation("ordinary message");
        logger.Log(LogLevel.Information, new EventId(7, "ordinary"), state, null, static (_, _) => "ordinary event");
        using (logger.BeginScope("ordinary outer"))
        using (logger.BeginScope("ordinary inner"))
        {
            logger.LogInformation("ordinary scope event");
        }

        logger.LogError(new InvalidOperationException("ordinary exception"), "ordinary exception event");

        var result = probe.Verify();

        Assert.Equal(LogLeakVerificationStatus.Clean, result.Status);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Matching_is_ordinal_literal_only()
    {
        const string sentinel = "CaseSensitive-Token-4a9e";
        using var probe = new LogLeakProbe().AddSecret("literal", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("LiteralTests");

        logger.LogInformation("casesensitive-token-4a9e");
        logger.LogInformation("CaseSensitive%2DToken%2D4a9e");
        logger.LogInformation(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sentinel)));
        logger.LogInformation("CaseSensitive-Token-4a9");

        Assert.Equal(LogLeakVerificationStatus.Clean, probe.Verify().Status);
    }

    [Fact]
    public void Diagnostics_and_telemetry_input_never_include_sentinel_text()
    {
        const string sentinel = "synthetic-diagnostic-5b7c";
        var telemetry = new RecordingTelemetry();
        using var probe = new LogLeakProbe(null, telemetry).AddSecret("diagnostic", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("Category-" + sentinel);
        logger.LogInformation("message " + sentinel);

        var output = new StringWriter();
        var error = new StringWriter();
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        LogLeakAssertionException assertion;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            assertion = Assert.Throws<LogLeakAssertionException>(() => probe.AssertNoLeaks());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }

        var diagnosticText = assertion.ToString()
            + Environment.NewLine
            + string.Join(Environment.NewLine, assertion.Findings.Select(static finding => finding.ToString()))
            + Environment.NewLine
            + output
            + Environment.NewLine
            + error
            + Environment.NewLine
            + telemetry;

        Assert.DoesNotContain(sentinel, diagnosticText, StringComparison.Ordinal);
        Assert.Null(assertion.Findings[0].CategoryName);
        Assert.Equal(1, telemetry.ActivationCalls);
        Assert.Equal(1, telemetry.HeartbeatCalls);
    }

    [Fact]
    public void Event_overflow_is_explicit_and_never_clean()
    {
        const string sentinel = "synthetic-overflow-91c2";
        var options = new LogLeakOptions(maximumCapturedEvents: 1);
        using var probe = new LogLeakProbe(options).AddSecret("overflow", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("OverflowTests");

        logger.LogInformation("first event");
        logger.LogInformation("second event");

        var result = probe.Verify();

        Assert.Equal(LogLeakVerificationStatus.Inconclusive, result.Status);
        Assert.Equal(1, result.CapturedEventCount);
        var exception = Assert.Throws<LogLeakInconclusiveException>(() => probe.AssertNoLeaks());
        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_overflow_is_explicit_and_never_clean()
    {
        const string sentinel = "synthetic-payload-61de";
        var options = new LogLeakOptions(maximumPayloadCharacters: 32);
        using var probe = new LogLeakProbe(options).AddSecret("payload", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("OverflowTests");

        logger.LogInformation(new string('x', 33));

        Assert.Equal(LogLeakVerificationStatus.Inconclusive, probe.Verify().Status);
    }

    [Fact]
    public void Disposal_clears_state_and_makes_operations_fail_deterministically()
    {
        var probe = new LogLeakProbe().AddSecret("dispose", "synthetic-dispose-77a2");

        probe.Dispose();
        probe.Dispose();

        Assert.Throws<LogLeakDisposedException>(() => probe.Verify());
        Assert.Throws<LogLeakDisposedException>(() => probe.AddSecret("later", "synthetic-later-77a2"));
        Assert.Throws<LogLeakDisposedException>(() => probe.Provider.CreateLogger("DisposedTests"));
    }

    [Fact]
    public void Null_empty_and_duplicate_registration_are_deterministic()
    {
        using var probe = new LogLeakProbe();

        Assert.Throws<LogLeakConfigurationException>(() => probe.AddSecret("null-value", null!));
        Assert.Throws<LogLeakConfigurationException>(() => probe.AddSecret("empty-value", string.Empty));
        probe.AddSecret("first", "synthetic-duplicate-b48f");
        probe.AddSecret("second", "synthetic-duplicate-b48f");
        Assert.Throws<LogLeakConfigurationException>(() => probe.AddSecret("first", "another-value-7c11"));

        using var loggerFactory = CreateLoggerFactory(probe);
        loggerFactory.CreateLogger("RegistrationTests").LogInformation("synthetic-duplicate-b48f");

        var labels = probe.Verify().Findings.Select(static finding => finding.SentinelLabel).ToArray();
        Assert.Equal(new[] { "first", "second" }, labels);
    }

    [Fact]
    public void Telemetry_failure_does_not_change_verification_outcome()
    {
        const string sentinel = "synthetic-telemetry-2f1a";
        var telemetry = new ThrowingTelemetry();
        using var probe = new LogLeakProbe(null, telemetry).AddSecret("telemetry", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        loggerFactory.CreateLogger("TelemetryTests").LogInformation("safe event");

        var result = probe.Verify();

        Assert.Equal(LogLeakVerificationStatus.Clean, result.Status);
        Assert.Equal(1, telemetry.ActivationCalls);
        Assert.Equal(1, telemetry.HeartbeatCalls);
    }

    private static ILoggerFactory CreateLoggerFactory(LogLeakProbe probe)
        => LoggerFactory.Create(builder => builder.AddProvider(probe.Provider));

    private static partial class GeneratedLogging
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Warning, Message = "source generated value {Value}")]
        public static partial void Write(ILogger logger, string value);
    }

    private sealed class RecordingTelemetry : LogLeakProbe.ILogLeakTelemetry
    {
        public int ActivationCalls { get; private set; }

        public int HeartbeatCalls { get; private set; }

        public void TrackActivation() => ActivationCalls++;

        public void TrackHeartbeat() => HeartbeatCalls++;

        public override string ToString() => "activation=" + ActivationCalls + ";heartbeat=" + HeartbeatCalls;
    }

    private sealed class ThrowingTelemetry : LogLeakProbe.ILogLeakTelemetry
    {
        public int ActivationCalls { get; private set; }

        public int HeartbeatCalls { get; private set; }

        public void TrackActivation()
        {
            ActivationCalls++;
            throw new InvalidOperationException("telemetry unavailable");
        }

        public void TrackHeartbeat()
        {
            HeartbeatCalls++;
            throw new InvalidOperationException("telemetry unavailable");
        }
    }
}
