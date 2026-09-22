using System.Collections.Concurrent;
using System.Reflection;
using KeelMatrix.LogLeak;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KeelMatrix.LogLeak.Tests;

public sealed partial class LogLeakProbeTests
{
    [Fact]
    public void Detects_formatted_template_and_source_generated_messages()
    {
        const string sentinel = "synthetic-message-7f4b";
        using var probe = new LogLeakProbe().AddSecret("formatted", sentinel);
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
        using var probe = new LogLeakProbe().AddSecret("context", sentinel);
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
    public void Scope_handle_is_sentinel_safe_and_disposes_the_underlying_scope()
    {
        const string sentinel = "synthetic-scope-handle-1a2b";
        using var probe = new LogLeakProbe().AddSecret("M", sentinel);
        var logger = probe.Provider.CreateLogger("ScopeHandleTests");

        var scope = logger.BeginScope(sentinel);
        Assert.DoesNotContain(sentinel, scope!.ToString(), StringComparison.Ordinal);
        logger.LogInformation("event inside scope");
        scope.Dispose();
        logger.LogInformation("event after scope");

        var findings = probe.Verify().Findings;
        Assert.Single(findings);
        Assert.Equal(LogLeakLocation.Scope, findings[0].Location);
    }

    [Fact]
    public void Provider_boundary_argument_failures_are_sentinel_safe_in_message_and_to_string()
    {
        AssertProviderArgumentFailureIsSafe("categoryName", provider => provider.CreateLogger(null!));
        AssertProviderArgumentFailureIsSafe(
            "formatter",
            provider => provider.CreateLogger("ArgumentTests").Log<string>(LogLevel.Information, default, "safe", null, null!));
        AssertProviderArgumentFailureIsSafe(
            "newScopeProvider",
            provider => ((ISupportExternalScope)provider).SetScopeProvider(null!));
    }

    [Fact]
    public void Provider_boundary_argument_diagnostics_filter_fixed_and_composed_wording()
    {
        const string fixedWording = "Value cannot be null.";
        const string categoryNameDiagnostic = "Value cannot be null. Parameter: categoryName.";
        const string formatterDiagnostic = "Value cannot be null. Parameter: formatter.";
        const string scopeProviderDiagnostic = "Value cannot be null. Parameter: newScopeProvider.";

        AssertProviderArgumentFailureIsSafe(
            fixedWording,
            "marker",
            provider => provider.CreateLogger(null!),
            expectedParameterName: "categoryName");
        AssertProviderArgumentFailureIsSafe(
            categoryNameDiagnostic,
            "marker",
            provider => provider.CreateLogger(null!),
            expectedParameterName: "categoryName");

        AssertProviderArgumentFailureIsSafe(
            fixedWording,
            "marker",
            provider => provider.CreateLogger("ArgumentTests").Log<string>(LogLevel.Information, default, "safe", null, null!),
            expectedParameterName: "formatter");
        AssertProviderArgumentFailureIsSafe(
            formatterDiagnostic,
            "marker",
            provider => provider.CreateLogger("ArgumentTests").Log<string>(LogLevel.Information, default, "safe", null, null!),
            expectedParameterName: "formatter");

        AssertProviderArgumentFailureIsSafe(
            fixedWording,
            "marker",
            provider => ((ISupportExternalScope)provider).SetScopeProvider(null!),
            expectedParameterName: "newScopeProvider");
        AssertProviderArgumentFailureIsSafe(
            scopeProviderDiagnostic,
            "marker",
            provider => ((ISupportExternalScope)provider).SetScopeProvider(null!),
            expectedParameterName: "newScopeProvider");
    }

    [Fact]
    public void Executes_the_documented_source_generated_logging_example()
    {
        KeelMatrix.LogLeak.Documentation.SourceGeneratedLoggingExample.Run();
    }

    [Fact]
    public void Disposed_boundary_failures_are_sentinel_safe_in_message_and_to_string()
    {
        const string sentinel = "synthetic-disposed-boundary-3c4d";
        var probe = new LogLeakProbe().AddSecret("D", sentinel);
        probe.Dispose();

        var exception = Assert.Throws<LogLeakDisposedException>(() => probe.Provider.CreateLogger("DisposedBoundaryTests"));
        Assert.DoesNotContain(sentinel, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_during_formatter_capture_is_inconclusive_without_telemetry_and_outer_finding_survives()
    {
        const string sentinel = "synthetic-during-formatter-5e6f";
        var telemetry = new RecordingTelemetry();
        using var probe = new LogLeakProbe(null, telemetry).AddSecret("F", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("DuringCaptureTests");
        LogLeakVerificationResult? duringCapture = null;

        logger.Log(LogLevel.Information, default, sentinel, null, (state, _) =>
        {
            duringCapture = probe.Verify();
            return state;
        });

        Assert.Equal(LogLeakVerificationStatus.Inconclusive, duringCapture!.Status);
        Assert.Contains("active", duringCapture.InconclusiveReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, telemetry.ActivationCalls);
        Assert.Equal(0, telemetry.HeartbeatCalls);

        var outer = probe.Verify();
        Assert.Equal(LogLeakVerificationStatus.LeaksDetected, outer.Status);
        Assert.Single(outer.Findings);
        Assert.Equal(LogLeakLocation.FormattedMessage, outer.Findings[0].Location);
        Assert.Equal(0, telemetry.ActivationCalls);
        Assert.Equal(0, telemetry.HeartbeatCalls);
    }

    [Fact]
    public void Callback_failure_is_inconclusive_with_safe_callback_guidance()
    {
        const string sentinel = "synthetic-callback-failure-6f7a";
        using var probe = new LogLeakProbe().AddSecret("F", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("CallbackFailureTests");

        logger.Log<string>(
            LogLevel.Information,
            default,
            "safe",
            null,
            static (_, _) => throw new InvalidOperationException("formatter failed"));

        var result = probe.Verify();

        Assert.Equal(LogLeakVerificationStatus.Inconclusive, result.Status);
        Assert.Contains("callback failed", result.InconclusiveReason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sentinel, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_during_exception_representation_capture_is_inconclusive_and_outer_finding_survives()
    {
        const string sentinel = "synthetic-during-exception-7a8b";
        using var probe = new LogLeakProbe().AddSecret("E", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("DuringCaptureTests");
        var exception = new VerificationReenteringException(probe, sentinel);

        logger.LogError(exception, "safe exception event");

        Assert.Equal(LogLeakVerificationStatus.Inconclusive, exception.DuringCapture!.Status);
        var outer = probe.Verify();
        Assert.Equal(LogLeakVerificationStatus.LeaksDetected, outer.Status);
        Assert.Contains(outer.Findings, finding => finding.Location == LogLeakLocation.ExceptionRepresentation);
    }

    [Fact]
    public void Verification_during_state_and_scope_enumeration_is_inconclusive_and_findings_survive()
    {
        const string stateSentinel = "synthetic-during-state-9c0d";
        const string scopeSentinel = "synthetic-during-scope-1e2f";
        using var probe = new LogLeakProbe()
            .AddSecret("T", stateSentinel)
            .AddSecret("U", scopeSentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("DuringCaptureTests");
        var state = new VerificationReenteringEntries(probe, "Authorization", stateSentinel);

        logger.Log(LogLevel.Information, default, state, null, static (_, _) => "safe state event");
        var scope = new VerificationReenteringEntries(probe, "ScopeToken", scopeSentinel);
        using (logger.BeginScope(scope))
        {
            logger.LogInformation("safe scope event");
        }

        Assert.Equal(LogLeakVerificationStatus.Inconclusive, state.DuringCapture!.Status);
        Assert.Equal(LogLeakVerificationStatus.Inconclusive, scope.DuringCapture!.Status);

        var outer = probe.Verify();
        Assert.Equal(LogLeakVerificationStatus.LeaksDetected, outer.Status);
        Assert.Contains(outer.Findings, finding => finding.Location == LogLeakLocation.StructuredProperty);
        Assert.Contains(outer.Findings, finding => finding.Location == LogLeakLocation.Scope);
    }

    [Fact]
    public void Assert_no_leaks_during_capture_cannot_produce_a_clean_outer_result()
    {
        const string sentinel = "synthetic-during-assert-3a4b";
        using var probe = new LogLeakProbe().AddSecret("Q", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("DuringCaptureTests");
        var state = new[] { new KeyValuePair<string, object?>("Authorization", sentinel) };

        logger.Log(LogLevel.Information, default, state, null, (_, _) =>
        {
            probe.AssertNoLeaks();
            return "unreachable";
        });

        var outer = probe.Verify();
        Assert.Equal(LogLeakVerificationStatus.Inconclusive, outer.Status);
        Assert.Contains(outer.Findings, finding => finding.Location == LogLeakLocation.StructuredProperty);
    }

    [Fact]
    public void Detects_templated_and_dictionary_scope_string_values_without_serializing_objects()
    {
        const string sentinel = "synthetic-templated-scope-3e7a";
        using var probe = new LogLeakProbe().AddSecret("marker", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("PaymentClient");

        using (logger.BeginScope("Authorization {Token}", sentinel))
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["Authorization"] = sentinel,
            ["Opaque"] = new OpaqueScope(sentinel)
        }))
        {
            logger.LogInformation("scope event");
        }

        var result = probe.Verify();

        Assert.Equal(LogLeakVerificationStatus.LeaksDetected, result.Status);
        Assert.Equal(2, result.Findings.Count(finding => finding.Location == LogLeakLocation.Scope));
    }

    [Fact]
    public void Detects_string_valued_dictionary_scope_shapes()
    {
        const string dictionarySentinel = "synthetic-string-dictionary-scope-1a2b";
        const string readOnlyDictionarySentinel = "synthetic-readonly-dictionary-scope-3c4d";
        using var probe = new LogLeakProbe()
            .AddSecret("A1", dictionarySentinel)
            .AddSecret("B2", readOnlyDictionarySentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("PaymentClient");
        IReadOnlyDictionary<string, string> readOnlyDictionary = new Dictionary<string, string>
        {
            ["Authorization"] = readOnlyDictionarySentinel
        };

        using (logger.BeginScope(new Dictionary<string, string>
        {
            ["Authorization"] = dictionarySentinel
        }))
        using (logger.BeginScope(readOnlyDictionary))
        {
            logger.LogInformation("dictionary scope event");
        }

        var findings = probe.Verify().Findings.Where(finding => finding.Location == LogLeakLocation.Scope).ToArray();

        Assert.Equal(2, findings.Length);
        Assert.Contains(findings, finding => finding.SentinelLabel == "A1");
        Assert.Contains(findings, finding => finding.SentinelLabel == "B2");
    }

    [Fact]
    public void String_valued_dictionary_scope_without_registered_sentinel_is_clean()
    {
        using var probe = new LogLeakProbe().AddSecret("A1", "synthetic-absent-dictionary-scope-5e6f");
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("PaymentClient");
        IReadOnlyDictionary<string, string> readOnlyDictionary = new Dictionary<string, string>
        {
            ["Authorization"] = "ordinary-readonly-value"
        };

        using (logger.BeginScope(new Dictionary<string, string>
        {
            ["Authorization"] = "ordinary-dictionary-value"
        }))
        using (logger.BeginScope(readOnlyDictionary))
        {
            logger.LogInformation("dictionary scope event");
        }

        Assert.Equal(LogLeakVerificationStatus.Clean, probe.Verify().Status);
    }

    [Fact]
    public void Non_string_dictionary_scope_values_remain_excluded()
    {
        const string sentinel = "synthetic-non-string-dictionary-scope-7a8b";
        using var probe = new LogLeakProbe().AddSecret("A1", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("PaymentClient");

        using (logger.BeginScope(new Dictionary<string, OpaqueScope>
        {
            ["Authorization"] = new OpaqueScope(sentinel)
        }))
        {
            logger.LogInformation("dictionary scope event");
        }

        Assert.Equal(LogLeakVerificationStatus.Clean, probe.Verify().Status);
    }

    [Fact]
    public void Excludes_opaque_scope_objects_instead_of_recursively_serializing_them()
    {
        const string sentinel = "synthetic-opaque-scope-8d2f";
        using var probe = new LogLeakProbe().AddSecret("marker", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("PaymentClient");

        using (logger.BeginScope(new OpaqueScope(sentinel)))
        {
            logger.LogInformation("safe scope event");
        }

        Assert.Equal(LogLeakVerificationStatus.Clean, probe.Verify().Status);
    }

    [Fact]
    public void Scope_opened_before_provider_registration_is_outside_observed_boundary()
    {
        const string sentinel = "synthetic-pre-registration-scope-1a2b";
        using var probe = new LogLeakProbe().AddSecret("boundary", sentinel);
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(NullLoggerProvider.Instance));
        var logger = loggerFactory.CreateLogger("PaymentClient");

        using (logger.BeginScope("scope opened before provider " + sentinel))
        {
            loggerFactory.AddProvider(probe.Provider);
            loggerFactory.CreateLogger("PaymentClient").LogInformation("safe event");
        }

        Assert.Equal(LogLeakVerificationStatus.Clean, probe.Verify().Status);
    }

    [Fact]
    public void Detects_exception_representation()
    {
        const string sentinel = "synthetic-exception-c34e";
        using var probe = new LogLeakProbe().AddSecret("thrown", sentinel);
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
        using var probe = new LogLeakProbe(null, telemetry).AddSecret("finding", sentinel);
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
        Assert.Equal(0, telemetry.ActivationCalls);
        Assert.Equal(0, telemetry.HeartbeatCalls);
    }

    [Fact]
    public void Numeric_event_metadata_is_suppressed_from_findings_and_assertion_output()
    {
        const string sentinel = "718293";
        using var probe = new LogLeakProbe().AddSecret("pin", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("Category-" + sentinel);
        var state = new[]
        {
            new KeyValuePair<string, object?>(sentinel, sentinel),
            new KeyValuePair<string, object?>("{OriginalFormat}", "pin event")
        };

        logger.Log(LogLevel.Warning, new EventId(718293, "event-" + sentinel), state, null, static (_, _) => sentinel);

        var result = probe.Verify();
        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, finding =>
        {
            Assert.Null(finding.EventId);
            Assert.DoesNotContain(sentinel, finding.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, finding.CategoryName ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, finding.EventName ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, finding.PropertyName ?? string.Empty, StringComparison.Ordinal);
        });

        var assertion = Assert.Throws<LogLeakAssertionException>(() => probe.AssertNoLeaks());
        Assert.DoesNotContain(sentinel, assertion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Completed_diagnostics_guard_separator_and_quote_composition()
    {
        const string sentinel = "prefix' (EventId: 42)";
        using var probe = new LogLeakProbe().AddSecret("credential", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("prefix");

        logger.Log(LogLevel.Warning, new EventId(42), sentinel, null, static (state, _) => state);

        var result = probe.Verify();
        var finding = Assert.Single(result.Findings);
        var assertion = Assert.Throws<LogLeakAssertionException>(() => probe.AssertNoLeaks());

        Assert.DoesNotContain(sentinel, finding.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, assertion.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, assertion.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Completed_diagnostics_guard_event_name_property_and_fixed_fallback()
    {
        const string composedSentinel = "category' (EventId: 7, Name: 'event'). Property: 'token";
        using var probe = new LogLeakProbe().AddSecret("metadata", composedSentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("category");
        var state = new[]
        {
            new KeyValuePair<string, object?>("token", composedSentinel)
        };

        logger.Log(LogLevel.Warning, new EventId(7, "event"), state, null, static (_, _) => "safe");

        var result = probe.Verify();
        var finding = Assert.Single(result.Findings);
        var assertion = Assert.Throws<LogLeakAssertionException>(() => probe.AssertNoLeaks());
        Assert.DoesNotContain(composedSentinel, finding.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(composedSentinel, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(composedSentinel, assertion.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(composedSentinel, assertion.ToString(), StringComparison.Ordinal);

        const string fallbackSentinel = "Sentinel label '";
        using var fallbackProbe = new LogLeakProbe().AddSecret("fallback", fallbackSentinel);
        using var fallbackFactory = CreateLoggerFactory(fallbackProbe);
        fallbackFactory.CreateLogger("FallbackTests").LogInformation(fallbackSentinel);
        var fallbackFinding = Assert.Single(fallbackProbe.Verify().Findings);
        Assert.Equal(string.Empty, fallbackFinding.ToString());
        Assert.DoesNotContain(fallbackSentinel, fallbackFinding.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Same_probe_reentrant_logging_is_rejected_and_makes_verification_inconclusive()
    {
        const string sentinel = "prefix' (EventId: 42)";
        using var probe = new LogLeakProbe().AddSecret("credential", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("prefix");

        logger.Log(
            LogLevel.Warning,
            new EventId(42),
            sentinel,
            null,
            (state, _) =>
            {
                logger.Log(LogLevel.Information, new EventId(43), "nested safe event", null, static (nested, _) => nested);
                return state;
            });

        var result = probe.Verify();

        Assert.Equal(LogLeakVerificationStatus.Inconclusive, result.Status);
        Assert.Equal(1, result.CapturedEventCount);
        Assert.Contains("reentrant", result.InconclusiveReason!, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Findings, finding => Assert.DoesNotContain(sentinel, finding.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(sentinel, result.ToString(), StringComparison.Ordinal);

        var exception = Assert.Throws<LogLeakInconclusiveException>(() => probe.AssertNoLeaks());
        Assert.DoesNotContain(sentinel, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
        Assert.All(exception.Findings, finding => Assert.DoesNotContain(sentinel, finding.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void Same_probe_reentrant_logging_does_not_exceed_capture_limit()
    {
        var options = new LogLeakOptions(maximumCapturedEvents: 1);
        using var probe = new LogLeakProbe(options).AddSecret("C", "synthetic-reentrant-capture-1a2b");
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("ReentrantCaptureTests");

        logger.Log(
            LogLevel.Information,
            new EventId(1),
            "outer safe event",
            null,
            (state, _) =>
            {
                logger.Log(LogLevel.Information, new EventId(2), "nested safe event", null, static (nested, _) => nested);
                return state;
            });

        var result = probe.Verify();

        Assert.Equal(1, result.CapturedEventCount);
        Assert.Equal(LogLeakVerificationStatus.Inconclusive, result.Status);
        Assert.Contains("reentrant", result.InconclusiveReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Same_probe_reentrant_logging_cannot_bypass_per_event_inspection_unit_limit()
    {
        var options = new LogLeakOptions(maximumInspectionUnitsPerEvent: 1);
        using var probe = new LogLeakProbe(options).AddSecret("U", "synthetic-reentrant-unit-3c4d");
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("ReentrantBudgetTests");
        var state = new[]
        {
            new KeyValuePair<string, object?>("First", "safe-first"),
            new KeyValuePair<string, object?>("Second", "safe-second")
        };

        logger.Log(
            LogLevel.Information,
            new EventId(3),
            state,
            null,
            (_, _) =>
            {
                logger.LogInformation("nested safe event");
                return "safe formatted event";
            });

        var result = probe.Verify();

        Assert.Equal(LogLeakVerificationStatus.Inconclusive, result.Status);
        Assert.Equal(0, result.CapturedEventCount);
        Assert.Contains("reentrant", result.InconclusiveReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Same_probe_reentrant_logging_cannot_bypass_per_event_finding_limit()
    {
        var options = new LogLeakOptions(maximumFindingsPerEvent: 1);
        const string sentinel = "synthetic-reentrant-finding-5e6f";
        using var probe = new LogLeakProbe(options).AddSecret("F", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("ReentrantBudgetTests");
        var state = new[]
        {
            new KeyValuePair<string, object?>("First", sentinel),
            new KeyValuePair<string, object?>("Second", sentinel)
        };

        logger.Log(
            LogLevel.Information,
            new EventId(4),
            state,
            null,
            (_, _) =>
            {
                logger.LogInformation("nested safe event");
                return "safe formatted event";
            });

        var result = probe.Verify();

        Assert.Equal(LogLeakVerificationStatus.Inconclusive, result.Status);
        Assert.Equal(0, result.CapturedEventCount);
        Assert.Contains("reentrant", result.InconclusiveReason!, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Findings, finding => Assert.DoesNotContain(sentinel, finding.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void Every_probe_owned_registration_failure_sanitizes_fixed_diagnostics()
    {
        const string invalidLabelMessage = "Sentinel labels must be short safe identifiers.";
        AssertRegistrationDiagnosticCollision(invalidLabelMessage, probe => probe.AddSecret(string.Empty, "safe-empty-label-1a2b"));
        AssertRegistrationDiagnosticCollision(invalidLabelMessage, probe => probe.AddSecret("   ", "safe-whitespace-label-3c4d"));
        AssertRegistrationDiagnosticCollision(invalidLabelMessage, probe => probe.AddSecret(new string('x', 65), "safe-long-label-5e6f"));
        AssertRegistrationDiagnosticCollision(invalidLabelMessage, probe => probe.AddSecret("unsafe label", "safe-illegal-label-7a8b"));

        const string nullValueMessage = "Sentinel values must be non-empty.";
        AssertRegistrationDiagnosticCollision(nullValueMessage, probe => probe.AddSecret("null-value", null!));
        AssertRegistrationDiagnosticCollision(nullValueMessage, probe => probe.AddSecret("empty-value", string.Empty));

        const string oversizedValueMessage = "The sentinel value exceeds the configured registration limit.";
        AssertRegistrationDiagnosticCollision(oversizedValueMessage, probe => probe.AddSecret("oversized", new string('x', 4_097)));

        const string duplicateLabelMessage = "Sentinel labels must be unique.";
        AssertRegistrationDiagnosticCollision(duplicateLabelMessage, probe => probe.AddSecret("existing", "safe-duplicate-value-9c0d"));

        const string overlapMessage = "Sentinel labels and values must not overlap.";
        AssertRegistrationDiagnosticCollision(overlapMessage, probe => probe.AddSecret("new", "existing-safe-overlap-value-1e2f"));

        const string countMessage = "The sentinel registration limit was reached.";
        using var countProbe = new LogLeakProbe(new LogLeakOptions(maximumSentinels: 1)).AddSecret("existing", countMessage);
        var countException = Assert.Throws<LogLeakConfigurationException>(() => countProbe.AddSecret("second", "safe-count-value-3a4b"));
        Assert.DoesNotContain(countMessage, countException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(countMessage, countException.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Escaped_diagnostic_objects_retain_only_precomputed_safe_text()
    {
        const string sentinel = "synthetic-retained-object-6b7c";
        LogLeakFinding finding;
        LogLeakVerificationResult result;
        LogLeakAssertionException assertion;

        using (var probe = new LogLeakProbe().AddSecret("R", sentinel))
        using (var loggerFactory = CreateLoggerFactory(probe))
        {
            loggerFactory.CreateLogger("RetentionTests").LogInformation(sentinel);
            result = probe.Verify();
            finding = Assert.Single(result.Findings);
            assertion = Assert.Throws<LogLeakAssertionException>(() => probe.AssertNoLeaks());
            probe.Dispose();
        }

        var outputs = new[]
        {
            finding.ToString(),
            result.ToString(),
            assertion.Message,
            assertion.ToString()
        };
        Assert.All(outputs, output => Assert.DoesNotContain(sentinel, output, StringComparison.Ordinal));
        Assert.All(assertion.Findings, escapedFinding => Assert.DoesNotContain(sentinel, escapedFinding.ToString(), StringComparison.Ordinal));

        foreach (var escaped in new object[] { finding, result, assertion })
        {
            for (var type = escaped.GetType(); type is not null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    Assert.DoesNotContain("sentinelValues", field.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.NotEqual(typeof(string[]), field.FieldType);
                    var value = field.GetValue(escaped);
                    if (value is string text)
                    {
                        Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
                    }
                    else if (value is IEnumerable<string> texts)
                    {
                        Assert.DoesNotContain(texts, text => text.Contains(sentinel, StringComparison.Ordinal));
                    }
                }
            }
        }
    }

    [Fact]
    public void Detects_string_valued_dictionary_structured_state()
    {
        const string sentinel = "synthetic-string-dictionary-state-8d9e";
        using var probe = new LogLeakProbe().AddSecret("S", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("StructuredStateTests");

        logger.Log(
            LogLevel.Information,
            new EventId(5),
            new Dictionary<string, string> { ["Authorization"] = sentinel },
            null,
            static (_, _) => "safe formatted state");

        var finding = Assert.Single(probe.Verify().Findings);
        Assert.Equal(LogLeakLocation.StructuredProperty, finding.Location);
        Assert.Equal("Authorization", finding.PropertyName);
    }

    [Fact]
    public void String_valued_dictionary_structured_state_without_registered_sentinel_is_clean()
    {
        using var probe = new LogLeakProbe().AddSecret("S", "synthetic-absent-string-dictionary-state-a1b2");
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("StructuredStateTests");

        logger.Log(
            LogLevel.Information,
            new EventId(6),
            new Dictionary<string, string> { ["Authorization"] = "ordinary-state-value" },
            null,
            static (_, _) => "safe formatted state");

        Assert.Equal(LogLeakVerificationStatus.Clean, probe.Verify().Status);
    }

    [Fact]
    public void Excluded_non_string_structured_state_remains_clean_and_dual_state_is_inspected_once()
    {
        const string sentinel = "synthetic-opaque-state-b3c4";
        using var probe = new LogLeakProbe()
            .AddSecret("O", sentinel)
            .AddSecret("D", "synthetic-dual-state-d5e6");
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("StructuredStateTests");

        logger.Log(
            LogLevel.Information,
            new EventId(7),
            new Dictionary<string, OpaqueScope> { ["Opaque"] = new OpaqueScope(sentinel) },
            null,
            static (_, _) => "safe opaque state");
        logger.Log(
            LogLevel.Information,
            new EventId(8),
            new DualStructuredState("synthetic-dual-state-d5e6"),
            null,
            static (_, _) => "safe dual state");

        var findings = probe.Verify().Findings;

        Assert.DoesNotContain(findings, finding => finding.SentinelLabel == "O");
        Assert.Single(findings.Where(finding => finding.SentinelLabel == "D"));
    }

    [Fact]
    public void Completed_diagnostics_guard_numeric_limit_and_configuration_composition()
    {
        const string numericSentinel = "42";
        using var probe = new LogLeakProbe(new LogLeakOptions(maximumCapturedEvents: 42))
            .AddSecret("numeric-limit", numericSentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("LimitTests");
        for (var index = 0; index <= 42; index++)
        {
            logger.LogInformation("safe event");
        }

        var result = probe.Verify();
        var inconclusive = Assert.Throws<LogLeakInconclusiveException>(() => probe.AssertNoLeaks());

        Assert.DoesNotContain(numericSentinel, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(numericSentinel, result.InconclusiveReason ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(numericSentinel, inconclusive.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(numericSentinel, inconclusive.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(numericSentinel, inconclusive.ToString(), StringComparison.Ordinal);

        const string configurationSentinel = "Sentinel labels must be unique.";
        using var configurationProbe = new LogLeakProbe().AddSecret("configuration", configurationSentinel);
        var configurationFailure = Assert.Throws<LogLeakConfigurationException>(() =>
            configurationProbe.AddSecret("configuration", "safe-second-value-9c0d"));

        Assert.DoesNotContain(configurationSentinel, configurationFailure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(configurationSentinel, configurationFailure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_verification_requests_activation_and_heartbeat()
    {
        var telemetry = new RecordingTelemetry();
        using var probe = new LogLeakProbe(null, telemetry).AddSecret("run", "synthetic-clean-activation-4d5e");
        using var loggerFactory = CreateLoggerFactory(probe);
        loggerFactory.CreateLogger("TelemetryTests").LogInformation("safe event");

        Assert.Equal(LogLeakVerificationStatus.Clean, probe.Verify().Status);
        Assert.Equal(1, telemetry.ActivationCalls);
        Assert.Equal(1, telemetry.HeartbeatCalls);
    }

    [Fact]
    public void Inconclusive_capture_does_not_request_telemetry()
    {
        var telemetry = new RecordingTelemetry();
        var options = new LogLeakOptions(maximumCapturedEvents: 1);
        using var probe = new LogLeakProbe(options, telemetry).AddSecret("capture", "synthetic-inconclusive-6f7a");
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("TelemetryTests");
        logger.LogInformation("first event");
        logger.LogInformation("second event");

        Assert.Equal(LogLeakVerificationStatus.Inconclusive, probe.Verify().Status);
        Assert.Equal(0, telemetry.ActivationCalls);
        Assert.Equal(0, telemetry.HeartbeatCalls);
        Assert.Throws<LogLeakInconclusiveException>(() => probe.AssertNoLeaks());
        Assert.Equal(0, telemetry.ActivationCalls);
    }

    [Fact]
    public void Event_overflow_is_explicit_and_never_clean()
    {
        const string sentinel = "synthetic-overflow-91c2";
        var options = new LogLeakOptions(maximumCapturedEvents: 1);
        using var probe = new LogLeakProbe(options).AddSecret("capture", sentinel);
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
        using var probe = new LogLeakProbe(options).AddSecret("unit", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("OverflowTests");

        logger.LogInformation(new string('x', 33));

        Assert.Equal(LogLeakVerificationStatus.Inconclusive, probe.Verify().Status);
    }

    [Fact]
    public void Disposal_clears_state_and_makes_operations_fail_deterministically()
    {
        var probe = new LogLeakProbe().AddSecret("lifecycle", "synthetic-dispose-77a2");

        probe.Dispose();
        probe.Dispose();

        Assert.Throws<LogLeakDisposedException>(() => probe.Verify());
        Assert.Throws<LogLeakDisposedException>(() => probe.AddSecret("later", "synthetic-later-77a2"));
        Assert.Throws<LogLeakDisposedException>(() => probe.Provider.CreateLogger("DisposedTests"));
    }

    [Fact]
    public void Registration_freezes_when_capture_or_verification_begins()
    {
        using var capturedProbe = new LogLeakProbe();
        using var capturedFactory = CreateLoggerFactory(capturedProbe);
        capturedFactory.CreateLogger("LifecycleTests").LogInformation("future-sentinel-value-1a2b");

        var captureException = Assert.Throws<LogLeakConfigurationException>(() =>
            capturedProbe.AddSecret("future", "future-sentinel-value-1a2b"));
        Assert.Contains("closed", captureException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LogLeakVerificationStatus.Clean, capturedProbe.Verify().Status);

        using var verifiedProbe = new LogLeakProbe();
        Assert.Equal(LogLeakVerificationStatus.Clean, verifiedProbe.Verify().Status);
        var verificationException = Assert.Throws<LogLeakConfigurationException>(() =>
            verifiedProbe.AddSecret("later", "synthetic-later-registration-2b3c"));
        Assert.Contains("closed", verificationException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Retained_findings_remain_stable_when_a_later_registration_is_rejected()
    {
        const string firstSentinel = "synthetic-first-retained-4c5d";
        const string laterSentinel = "synthetic-later-retained-6e7f";
        using var probe = new LogLeakProbe().AddSecret("primary", firstSentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        loggerFactory.CreateLogger("Category-" + laterSentinel).LogInformation(firstSentinel);

        var firstResult = probe.Verify();
        var finding = Assert.Single(firstResult.Findings);
        Assert.Equal("Category-" + laterSentinel, finding.CategoryName);
        Assert.Throws<LogLeakConfigurationException>(() => probe.AddSecret("secondary", laterSentinel));

        var secondResult = probe.Verify();
        Assert.Equal(finding.CategoryName, secondResult.Findings[0].CategoryName);
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
        Assert.Equal(2, labels.Length);
        Assert.Equal("first", labels[0]);
        Assert.Equal("second", labels[1]);
    }

    [Fact]
    public void Registration_rejects_label_value_collisions_in_either_order_without_echoing_values()
    {
        const string firstValue = "synthetic-first-collision-3c4d";
        const string secondValue = "synthetic-second-collision-5e6f";
        const string safeInitialValue = "safe-initial-value-7a8b";
        using var valueFirst = new LogLeakProbe().AddSecret("alpha", firstValue);
        var valueFirstException = Assert.Throws<LogLeakConfigurationException>(() => valueFirst.AddSecret(firstValue, secondValue));

        using var labelFirst = new LogLeakProbe().AddSecret("second-" + secondValue, safeInitialValue);
        var labelFirstException = Assert.Throws<LogLeakConfigurationException>(() => labelFirst.AddSecret("first", secondValue));

        Assert.DoesNotContain(firstValue, valueFirstException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secondValue, valueFirstException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(firstValue, labelFirstException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secondValue, labelFirstException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(safeInitialValue, labelFirstException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registration_rejects_reverse_containment_in_either_registration_order()
    {
        using var newValueContainsExistingLabel = new LogLeakProbe().AddSecret("existing-label", "synthetic-first-value-2a3b");
        var newValueException = Assert.Throws<LogLeakConfigurationException>(() =>
            newValueContainsExistingLabel.AddSecret("new-label", "existing-label-suffix"));

        using var existingValueContainsNewLabel = new LogLeakProbe().AddSecret("first-label", "prefix-new-label-suffix");
        var existingValueException = Assert.Throws<LogLeakConfigurationException>(() =>
            existingValueContainsNewLabel.AddSecret("new-label", "synthetic-second-value-4c5d"));

        Assert.DoesNotContain("existing-label", newValueException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("existing-label-suffix", newValueException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("first-label", existingValueException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("prefix-new-label-suffix", existingValueException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registration_rejects_self_and_multiple_registered_value_collisions_without_echoing_labels()
    {
        const string firstValue = "synthetic-first-value-9c0d";
        const string secondValue = "synthetic-second-value-1e2f";
        using var probe = new LogLeakProbe().AddSecret("alpha", firstValue);
        probe.AddSecret("beta", secondValue);

        var selfException = Assert.Throws<LogLeakConfigurationException>(() => probe.AddSecret("self-" + firstValue, "safe-self-value-3a4b"));
        var equalException = Assert.Throws<LogLeakConfigurationException>(() => probe.AddSecret("equal-label", "equal-label"));
        var multipleException = Assert.Throws<LogLeakConfigurationException>(() => probe.AddSecret(firstValue + "-" + secondValue, "safe-multiple-value-5c6d"));

        Assert.DoesNotContain(firstValue, selfException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secondValue, selfException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("equal-label", equalException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(firstValue, multipleException.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secondValue, multipleException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_registration_does_not_poison_later_valid_registration_or_reopen_collision()
    {
        const string registeredValue = "synthetic-mid-sequence-7d8e";
        using var probe = new LogLeakProbe().AddSecret("registered", registeredValue);

        var collision = Assert.Throws<LogLeakConfigurationException>(() => probe.AddSecret("label-" + registeredValue, "safe-rejected-value-9f0a"));
        Assert.DoesNotContain(registeredValue, collision.Message, StringComparison.Ordinal);

        probe.AddSecret("survivor", "synthetic-valid-after-rejection-1b2c");
        var laterCollision = Assert.Throws<LogLeakConfigurationException>(() => probe.AddSecret("later-" + registeredValue, "safe-later-value-3d4e"));
        Assert.DoesNotContain(registeredValue, laterCollision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Telemetry_failure_does_not_change_verification_outcome()
    {
        const string sentinel = "synthetic-telemetry-2f1a";
        var telemetry = new ThrowingTelemetry();
        using var probe = new LogLeakProbe(null, telemetry).AddSecret("run", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        loggerFactory.CreateLogger("TelemetryTests").LogInformation("safe event");

        var result = probe.Verify();

        Assert.Equal(LogLeakVerificationStatus.Clean, result.Status);
        Assert.Equal(1, telemetry.ActivationCalls);
        Assert.Equal(1, telemetry.HeartbeatCalls);
    }

    [Fact]
    public void Detected_leaks_do_not_request_activation_telemetry()
    {
        const string sentinel = "synthetic-detected-telemetry-3a4b";
        var telemetry = new RecordingTelemetry();
        using var probe = new LogLeakProbe(null, telemetry).AddSecret("run", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        loggerFactory.CreateLogger("TelemetryTests").LogInformation(sentinel);

        var result = probe.Verify();

        Assert.Equal(LogLeakVerificationStatus.LeaksDetected, result.Status);
        Assert.Equal(0, telemetry.ActivationCalls);
        Assert.Equal(0, telemetry.HeartbeatCalls);
        Assert.Throws<LogLeakAssertionException>(() => probe.AssertNoLeaks());
        Assert.Equal(0, telemetry.ActivationCalls);
    }

    [Fact]
    public async Task Concurrent_registration_and_capture_freeze_at_one_atomic_boundary()
    {
        const string sentinel = "synthetic-interleaved-registration-5b6c";
        using var probe = new LogLeakProbe();
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("ConcurrencyLifecycleTests");
        using var start = new Barrier(2);
        Exception? registrationException = null;
        Exception? captureException = null;

        var registration = Task.Run(() =>
        {
            try
            {
                start.SignalAndWait();
                probe.AddSecret("interleaved", sentinel);
            }
            catch (Exception exception)
            {
                registrationException = exception;
            }
        });
        var capture = Task.Run(() =>
        {
            try
            {
                start.SignalAndWait();
                logger.LogInformation(sentinel);
            }
            catch (Exception exception)
            {
                captureException = exception;
            }
        });

        await Task.WhenAll(registration, capture);

        Assert.True(registrationException is null || registrationException is LogLeakConfigurationException);
        Assert.Null(captureException);
        Assert.Throws<LogLeakConfigurationException>(() => probe.AddSecret("after", "synthetic-after-interleave-7d8e"));
        Assert.Contains(
            probe.Verify().Status,
            new[] { LogLeakVerificationStatus.Clean, LogLeakVerificationStatus.LeaksDetected });
    }

    [Fact]
    public async Task Concurrent_capture_detects_planted_sentinel_without_exceptions_or_unbounded_findings()
    {
        const int taskCount = 12;
        const int eventsPerTask = 4;
        const string sentinel = "synthetic-concurrent-leak-6f2a";
        var options = new LogLeakOptions(
            maximumCapturedEvents: taskCount * eventsPerTask,
            maximumFindings: taskCount * eventsPerTask,
            maximumFindingsPerEvent: 2);
        using var probe = new LogLeakProbe(options).AddSecret("batch", sentinel);
        using var loggerFactory = CreateLoggerFactory(probe);
        var logger = loggerFactory.CreateLogger("ConcurrencyTests");
        var errors = new ConcurrentQueue<Exception>();

        var tasks = Enumerable.Range(0, taskCount)
            .Select(taskNumber => Task.Run(() =>
            {
                try
                {
                    for (var eventNumber = 0; eventNumber < eventsPerTask; eventNumber++)
                    {
                        logger.LogInformation("concurrent event {TaskNumber} {EventNumber}: " + sentinel, taskNumber, eventNumber);
                    }
                }
                catch (Exception exception)
                {
                    errors.Enqueue(exception);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Empty(errors);
        var result = probe.Verify();
        Assert.Equal(LogLeakVerificationStatus.LeaksDetected, result.Status);
        Assert.InRange(result.Findings.Count, 1, options.MaximumFindings);
        Assert.Equal(taskCount * eventsPerTask, result.CapturedEventCount);
        Assert.All(result.Findings, finding => Assert.Equal(LogLeakLocation.FormattedMessage, finding.Location));

        probe.Dispose();
        Assert.Throws<LogLeakDisposedException>(() => probe.Verify());
    }

    [Fact]
    public async Task Unrelated_thread_allocations_and_following_gc_do_not_change_verification_result()
    {
        using var cleanProbe = new LogLeakProbe().AddSecret("A1", "synthetic-unrelated-thread-clean-a1b2");
        using var cleanFactory = CreateLoggerFactory(cleanProbe);
        cleanFactory.CreateLogger("ResourceStabilityTests").LogInformation("safe event");
        var cleanBefore = cleanProbe.Verify();

        await Task.Run(() =>
        {
            for (var index = 0; index < 256; index++)
            {
                var allocation = new byte[16 * 1024];
                allocation[0] = (byte)index;
                GC.KeepAlive(allocation);
            }
        });
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var cleanAfter = cleanProbe.Verify();
        Assert.Equal(cleanBefore.Status, cleanAfter.Status);
        Assert.Equal(cleanBefore.CapturedEventCount, cleanAfter.CapturedEventCount);
        Assert.Equal(cleanBefore.Findings.Count, cleanAfter.Findings.Count);

        const string leakSentinel = "synthetic-unrelated-thread-leak-c3d4";
        using var leakProbe = new LogLeakProbe().AddSecret("B2", leakSentinel);
        using var leakFactory = CreateLoggerFactory(leakProbe);
        leakFactory.CreateLogger("ResourceStabilityTests").LogInformation(leakSentinel);
        var leakBefore = leakProbe.Verify();

        await Task.Run(() =>
        {
            for (var index = 0; index < 256; index++)
            {
                var allocation = new byte[16 * 1024];
                allocation[0] = (byte)index;
                GC.KeepAlive(allocation);
            }
        });
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var leakAfter = leakProbe.Verify();
        Assert.Equal(leakBefore.Status, leakAfter.Status);
        Assert.Equal(leakBefore.CapturedEventCount, leakAfter.CapturedEventCount);
        Assert.Equal(leakBefore.Findings.Count, leakAfter.Findings.Count);
    }

    private static ILoggerFactory CreateLoggerFactory(LogLeakProbe probe)
        => LoggerFactory.Create(builder => builder.AddProvider(probe.Provider));

    private static void AssertRegistrationDiagnosticCollision(string registeredValue, Action<LogLeakProbe> action)
    {
        using var probe = new LogLeakProbe().AddSecret("existing", registeredValue);
        var exception = Assert.Throws<LogLeakConfigurationException>(() => action(probe));

        Assert.DoesNotContain(registeredValue, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(registeredValue, exception.ToString(), StringComparison.Ordinal);
    }

    private static void AssertProviderArgumentFailureIsSafe(string sentinel, Action<ILoggerProvider> action)
    {
        using var probe = new LogLeakProbe().AddSecret("marker", sentinel);
        var exception = Assert.ThrowsAny<ArgumentNullException>(() => action(probe.Provider));

        Assert.DoesNotContain(sentinel, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
    }

    private static void AssertProviderArgumentFailureIsSafe(
        string sentinel,
        string label,
        Action<ILoggerProvider> action,
        string expectedParameterName)
    {
        using var probe = new LogLeakProbe().AddSecret(label, sentinel);
        var exception = Assert.ThrowsAny<ArgumentNullException>(() => action(probe.Provider));

        Assert.Equal(expectedParameterName, exception.ParamName);
        Assert.DoesNotContain(sentinel, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
    }

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

    private sealed class OpaqueScope
    {
        private readonly string value;

        public OpaqueScope(string value)
        {
            this.value = value;
        }

        public override string ToString() => value;
    }

    private sealed class VerificationReenteringException : Exception
    {
        private readonly string representation;

        public VerificationReenteringException(LogLeakProbe probe, string representation)
            : base("safe exception")
        {
            this.representation = representation;
            DuringCapture = null;
            this.probe = probe;
        }

        private readonly LogLeakProbe probe;

        public LogLeakVerificationResult? DuringCapture { get; private set; }

        public override string ToString()
        {
            DuringCapture = probe.Verify();
            return representation;
        }
    }

    private sealed class VerificationReenteringEntries : IEnumerable<KeyValuePair<string, object?>>
    {
        private readonly LogLeakProbe probe;
        private readonly string key;
        private readonly string value;

        public VerificationReenteringEntries(LogLeakProbe probe, string key, string value)
        {
            this.probe = probe;
            this.key = key;
            this.value = value;
        }

        public LogLeakVerificationResult? DuringCapture { get; private set; }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            DuringCapture = probe.Verify();
            yield return new KeyValuePair<string, object?>(key, value);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    private sealed class DualStructuredState : IEnumerable<KeyValuePair<string, object?>>, IEnumerable<KeyValuePair<string, string>>
    {
        private readonly string value;

        public DualStructuredState(string value)
        {
            this.value = value;
        }

        IEnumerator<KeyValuePair<string, object?>> IEnumerable<KeyValuePair<string, object?>>.GetEnumerator()
        {
            yield return new KeyValuePair<string, object?>("ObjectValue", value);
        }

        IEnumerator<KeyValuePair<string, string>> IEnumerable<KeyValuePair<string, string>>.GetEnumerator()
        {
            yield return new KeyValuePair<string, string>("StringValue", value);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => ((IEnumerable<KeyValuePair<string, object?>>)this).GetEnumerator();
    }
}
