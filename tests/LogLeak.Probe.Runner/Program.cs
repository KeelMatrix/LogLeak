using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using LogLeak.Probe.AspNetApp;
using LogLeak.Probe.Core;
using LogLeak.Probe.PlainLogging;
using LogLeak.Probe.Serilog;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogLeak.Probe.Runner;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.WriteLine("LogLeak feasibility probe");
            Console.WriteLine("Supported fields: formatted message; string structured-property values; string scope values; exception representation.");
            Console.WriteLine("Matching: exact ordinal literal containment only; no decoding, normalization, hashing, encoding permutations, or object serialization.");
            Console.WriteLine();
            PrintFieldContract();

            switch (args.FirstOrDefault())
            {
                case "--coverage":
                    RunCoverageCorpus();
                    break;
                case "--diagnostics":
                    RunDiagnosticExfiltrationCorpus();
                    break;
                case "--overflow":
                    RunOverflowCorpus();
                    break;
                case "--performance":
                    RunPerformanceCorpus();
                    break;
                case null:
                    RunCoverageCorpus();
                    RunDiagnosticExfiltrationCorpus();
                    RunOverflowCorpus();
                    RunPerformanceCorpus();
                    PrintGoGate();
                    break;
                default:
                    throw new InvalidOperationException("Unknown probe mode.");
            }

            Console.WriteLine();
            Console.WriteLine("RESULT: PASS - all bounded probe corpora completed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("RESULT: FAIL - the probe corpus did not complete.");
            Console.Error.WriteLine($"Failure type: {exception.GetType().FullName}");
            return 1;
        }
    }

    private static void RunCoverageCorpus()
    {
        Console.WriteLine("COVERAGE CORPUS");
        RunExpectedFinding("ordinary interpolated logging", LeakLocation.FormattedMessage, PlainLoggingCorpus.Interpolated);
        RunExpectedFinding("message-template logging", LeakLocation.FormattedMessage, PlainLoggingCorpus.MessageTemplate);
        RunExpectedFinding("message-template structured property", LeakLocation.StructuredProperty, PlainLoggingCorpus.MessageTemplate);
        RunExpectedFinding("source-generated LoggerMessage", LeakLocation.FormattedMessage, (logger, sentinel) => GeneratedLoggingCorpus.SourceGenerated(logger, sentinel));
        RunExpectedFinding("source-generated structured property", LeakLocation.StructuredProperty, (logger, sentinel) => GeneratedLoggingCorpus.SourceGenerated(logger, sentinel));
        RunExpectedFinding("direct structured state value", LeakLocation.StructuredProperty, PlainLoggingCorpus.StructuredState);
        RunExpectedFinding("nested scope value", LeakLocation.Scope, PlainLoggingCorpus.NestedScopes);
        RunExpectedFinding("exception representation", LeakLocation.ExceptionRepresentation, PlainLoggingCorpus.Exception);
        RunRegistrationCorpus();
        RunRedactionAndClassification();
        RunAbsentSentinelCorpus();
        RunAspNetCoreIntegration();
        RunSerilogIntegration();

        Console.WriteLine("Unsupported fixture recorded: arbitrary structured objects are not serialized or inspected; this avoids hidden object-graph behavior.");
        Console.WriteLine("Unsupported fixture recorded: arbitrary downstream sink fields are outside the Microsoft.Extensions.Logging provider boundary.");
        Console.WriteLine();
    }

    private static void PrintFieldContract()
    {
        Console.WriteLine("SUPPORTED FIELD CONTRACT");
        Console.WriteLine("- formatted message: formatter output inspected with ordinal literal containment.");
        Console.WriteLine("- structured property: direct string values in the framework's enumerable key/value state inspected; values are never serialized.");
        Console.WriteLine("- scope: direct string scope states inspected; structured or arbitrary scope objects are excluded.");
        Console.WriteLine("- exception representation: exception.ToString() inspected with ordinal literal containment; exception payload is never emitted.");
        Console.WriteLine("EXCLUDED FIELD CONTRACT");
        Console.WriteLine("- non-string state/scope objects: excluded because recursive serialization or ToString would be unsafe and unreliable.");
        Console.WriteLine("- category, level, EventId, templates, and property names: metadata is captured only for provider-boundary context and is not a supported leak field.");
        Console.WriteLine("- downstream Serilog or other sink fields: excluded because they are beyond the Microsoft.Extensions.Logging provider boundary.");
        Console.WriteLine();
    }

    private static void RunAbsentSentinelCorpus()
    {
        const string label = "absent-sentinel";
        var sentinel = NewSentinel(label);
        var unrelatedValue = "fixture-not-registered-" + Guid.NewGuid().ToString("N");
        using (var probe = NewProbe(64))
        {
            probe.AddSentinel(label, sentinel);
            using var factory = CreateFactory(probe);
            var logger = factory.CreateLogger("LogLeak.Probe.Absent");
            PlainLoggingCorpus.Interpolated(logger, unrelatedValue);
            PlainLoggingCorpus.MessageTemplate(logger, unrelatedValue);
            PlainLoggingCorpus.StructuredState(logger, unrelatedValue);
            PlainLoggingCorpus.NestedScopes(logger, unrelatedValue);
            GeneratedLoggingCorpus.SourceGenerated(logger, unrelatedValue);
            PlainLoggingCorpus.Exception(logger, unrelatedValue);
            PlainLoggingCorpus.Redacted(logger, unrelatedValue);
            probe.AssertNoLeaks();
        }

        using (var probe = NewProbe(64))
        {
            probe.AddSentinel(label, sentinel);
            using var factory = new WebApplicationFactory<LogLeak.Probe.AspNetApp.Program>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(AppContext.BaseDirectory);
                builder.ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddProvider(probe);
                });
            });
            using var client = factory.CreateClient();
            var response = client.GetAsync("/probe/" + unrelatedValue).GetAwaiter().GetResult();
            Require(response.IsSuccessStatusCode, "The absent-sentinel ASP.NET Core corpus did not respond successfully.");
            probe.AssertNoLeaks();
        }

        using (var probe = NewProbe(64))
        {
            probe.AddSentinel(label, sentinel);
            using var serilogProvider = SerilogCorpus.CreateProvider();
            using var factory = LoggerFactory.Create(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(serilogProvider);
                builder.AddProvider(probe);
            });
            SerilogCorpus.Route(factory.CreateLogger("LogLeak.Probe.Absent.Serilog"), unrelatedValue);
            probe.AssertNoLeaks();
        }

        Console.WriteLine("PASS absent-sentinel corpus: equivalent plain, ASP.NET Core, and Serilog events produced zero findings.");
    }

    private static void RunExpectedFinding(string name, LeakLocation expectedLocation, Action<ILogger, string> emit)
    {
        var label = "coverage-" + name.Replace(' ', '-');
        var sentinel = NewSentinel(label);
        using var probe = NewProbe(32);
        probe.AddSentinel(label, sentinel);
        using var factory = CreateFactory(probe);
        var logger = factory.CreateLogger("LogLeak.Probe.Coverage");
        emit(logger, sentinel);

        var findings = probe.FindLeaks();
        Require(findings.Any(finding => finding.Label == label && finding.Location == expectedLocation), "A planted supported-field sentinel was not detected.");
        RequireSafeFindingDiagnostics(findings);
        Console.WriteLine($"PASS {name}: detected label '{label}' at {expectedLocation}.");
    }

    private static void RunRedactionAndClassification()
    {
        const string label = "coverage-redaction";
        var sentinel = NewSentinel(label);
        using var probe = NewProbe(32);
        probe.AddSentinel(label, sentinel);
        using var factory = CreateFactory(probe);
        PlainLoggingCorpus.Redacted(factory.CreateLogger("LogLeak.Probe.Redaction"), sentinel);
        probe.AssertNoLeaks();
        Console.WriteLine("PASS redaction/classification: redacted text is absent and opaque objects are conservatively unsupported.");
    }

    private static void RunRegistrationCorpus()
    {
        using var invalidProbe = NewProbe(8);
        var emptyValueFailure = CaptureConfigurationFailure(() => invalidProbe.AddSentinel("empty", string.Empty));
        var nullValueFailure = CaptureConfigurationFailure(() => invalidProbe.AddSentinel("null", null!));
        Require(emptyValueFailure is ProbeConfigurationException && nullValueFailure is ProbeConfigurationException, "Empty and null sentinel registration was not rejected.");

        var sentinel = NewSentinel("duplicate");
        using var probe = NewProbe(8);
        probe.AddSentinel("first-duplicate", sentinel);
        probe.AddSentinel("second-duplicate", sentinel);
        using var factory = CreateFactory(probe);
        PlainLoggingCorpus.MessageTemplate(factory.CreateLogger("LogLeak.Probe.Duplicates"), sentinel);
        var findings = probe.FindLeaks();
        Require(findings.Any(finding => finding.Label == "first-duplicate") && findings.Any(finding => finding.Label == "second-duplicate"), "Duplicate sentinel values did not produce deterministic findings for both labels.");
        Require(findings.First(finding => finding.Label == "first-duplicate").Label == "first-duplicate" && findings.First(finding => finding.Label == "second-duplicate").Label == "second-duplicate", "Duplicate sentinel findings were not stable by registration order.");
        Require(!emptyValueFailure.ToString().Contains(sentinel, StringComparison.Ordinal) && !nullValueFailure.ToString().Contains(sentinel, StringComparison.Ordinal), "Registration diagnostics contained a sentinel.");
        Console.WriteLine("PASS registration: empty/null values rejected; duplicate literal values produce stable findings for both safe labels.");
    }

    private static void RunAspNetCoreIntegration()
    {
        const string label = "coverage-aspnet";
        var sentinel = NewSentinel(label);
        using var probe = NewProbe(128);
        probe.AddSentinel(label, sentinel);
        using var factory = new WebApplicationFactory<LogLeak.Probe.AspNetApp.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(AppContext.BaseDirectory);
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(probe);
            });
        });

        using var client = factory.CreateClient();
        var response = client.GetAsync("/probe/" + sentinel).GetAwaiter().GetResult();
        Require(response.IsSuccessStatusCode, "The ASP.NET Core integration endpoint did not respond successfully.");
        var findings = probe.FindLeaks();
        Require(findings.Any(finding => finding.Label == label), "The ASP.NET Core logging path did not reach the probe.");
        RequireSafeFindingDiagnostics(findings);
        Console.WriteLine("PASS ASP.NET Core WebApplicationFactory integration: provider-boundary event observed.");
    }

    private static void RunSerilogIntegration()
    {
        const string label = "coverage-serilog";
        var sentinel = NewSentinel(label);
        using var probe = NewProbe(64);
        probe.AddSentinel(label, sentinel);
        using var serilogProvider = SerilogCorpus.CreateProvider();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(serilogProvider);
            builder.AddProvider(probe);
        });

        SerilogCorpus.Route(factory.CreateLogger("LogLeak.Probe.Serilog"), sentinel);
        var findings = probe.FindLeaks();
        Require(findings.Any(finding => finding.Label == label), "The Serilog Microsoft logging provider path did not reach the probe.");
        RequireSafeFindingDiagnostics(findings);
        Console.WriteLine("PASS Serilog provider integration: Microsoft logging routed through Serilog and observed at the probe boundary.");
    }

    private static void RunDiagnosticExfiltrationCorpus()
    {
        Console.WriteLine("DIAGNOSTIC-EXFILTRATION CORPUS");
        var locations = new[]
        {
            ("formatted-message", LeakLocation.FormattedMessage, (Action<ILogger, string>)PlainLoggingCorpus.Interpolated),
            ("structured-property", LeakLocation.StructuredProperty, (Action<ILogger, string>)PlainLoggingCorpus.StructuredState),
            ("scope", LeakLocation.Scope, (Action<ILogger, string>)PlainLoggingCorpus.NestedScopes),
            ("exception-representation", LeakLocation.ExceptionRepresentation, (Action<ILogger, string>)PlainLoggingCorpus.Exception)
        };

        foreach (var (name, expectedLocation, emit) in locations)
        {
            var label = "diagnostic-" + name;
            var sentinel = NewSentinel(label);
            using var probe = NewProbe(32);
            probe.AddSentinel(label, sentinel);
            using var factory = CreateFactory(probe);
            emit(factory.CreateLogger("LogLeak.Probe.Diagnostics"), sentinel);
            var findings = probe.FindLeaks();
            Require(findings.Any(finding => finding.Label == label && finding.Location == expectedLocation), "The diagnostic corpus did not create the expected supported-field finding.");

            var thrown = CaptureAssertion(probe);
            var console = CaptureConsole(() =>
            {
                Console.WriteLine(thrown.ToString());
                Console.Error.WriteLine(thrown.ToString());
            });
            var testJson = JsonSerializer.Serialize(new SafeTestResult("failed", findings.Select(finding => new SafeFinding(finding.Label, finding.Location.ToString())).ToArray()));
            var testXml = new XDocument(new XElement("test-result", new XAttribute("status", "failed"), findings.Select(finding => new XElement("finding", new XAttribute("label", finding.Label), new XAttribute("location", finding.Location))))).ToString(SaveOptions.DisableFormatting);
            var report = JsonSerializer.Serialize(new SafeReport("diagnostic", findings.Count, "no captured log content"));
            var telemetryDouble = new FakeTelemetry();
            telemetryDouble.Send(new SafeTelemetry("verification-complete", "test", findings.Count));
            var telemetry = telemetryDouble.Input;
            var debuggerText = string.Join(Environment.NewLine, findings.Select(finding => finding.ToString()).Append(thrown.ToString()));

            var artifactPath = Path.Combine(Path.GetTempPath(), "logleak-probe-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(artifactPath, report, Encoding.UTF8);
                var artifactBytes = File.ReadAllBytes(artifactPath);
                var sources = new Dictionary<string, object?>
                {
                    ["thrown assertion"] = thrown.ToString(),
                    ["stdout"] = console.Stdout,
                    ["stderr"] = console.Stderr,
                    ["test-result-json"] = testJson,
                    ["test-result-xml"] = testXml,
                    ["probe-report"] = report,
                    ["telemetry-input"] = telemetry,
                    ["debugger-and-tostring"] = debuggerText,
                    ["report-artifact"] = artifactBytes
                };
                DiagnosticSafetyAudit.AssertAbsent(sentinel, sources);
            }
            finally
            {
                if (File.Exists(artifactPath))
                {
                    File.Delete(artifactPath);
                }
            }

            Console.WriteLine($"PASS {name}: label/location diagnostics, stdout/stderr, JSON/XML, report artifact, telemetry, and ToString are sentinel-free.");
        }

        Console.WriteLine();
    }

    private static void RunOverflowCorpus()
    {
        Console.WriteLine("BOUNDED CAPTURE CORPUS");
        const int limit = 128;
        var sentinel = NewSentinel("overflow");
        using var probe = NewProbe(limit);
        probe.AddSentinel("overflow", sentinel);
        using var factory = CreateFactory(probe);
        var logger = factory.CreateLogger("LogLeak.Probe.Overflow");
        for (var index = 0; index <= limit; index++)
        {
            logger.LogInformation("overflow event {Index}", index);
        }

        Require(probe.Overflowed, "The configured capture limit did not enter the overflow state.");
        var first = CaptureProbeFailure(probe);
        logger.LogInformation("post-overflow event");
        var second = CaptureProbeFailure(probe);
        Require(first is ProbeOverflowException && second is ProbeOverflowException, "Overflow did not remain an explicit conservative failure after more logging.");
        var beforeDispose = probe.CapturedEventCount;
        var registeredBeforeDispose = probe.RegisteredSentinelCount;
        probe.Dispose();
        Require(beforeDispose == limit && registeredBeforeDispose == 1 && probe.CapturedEventCount == 0 && probe.RegisteredSentinelCount == 0, "Dispose did not clear bounded capture state.");
        var findingLimitSentinel = NewSentinel("finding-limit");
        using var findingLimitProbe = NewProbe(8, maximumFindings: 1);
        findingLimitProbe.AddSentinel("finding-limit", findingLimitSentinel);
        using var findingLimitFactory = CreateFactory(findingLimitProbe);
        PlainLoggingCorpus.MessageTemplate(findingLimitFactory.CreateLogger("LogLeak.Probe.FindingLimit"), findingLimitSentinel);
        var findingLimitFailure = CaptureProbeFailure(findingLimitProbe);
        Require(findingLimitFailure is ProbeOverflowException && findingLimitProbe.Overflowed, "The finding-resource limit did not fail conservatively.");
        Console.WriteLine($"PASS overflow: event-limit={limit}, finding-limit=1, retained-before-dispose={beforeDispose}, post-overflow verification remains inconclusive, disposed-state=cleared.");
        Console.WriteLine();
    }

    private static void RunPerformanceCorpus()
    {
        Console.WriteLine("PERFORMANCE/MEMORY CORPUS");
        const int eventCount = 100_000;
        var sentinel = NewSentinel("benchmark-absent");
        using var probe = NewProbe(eventCount);
        probe.AddSentinel("benchmark-absent", sentinel);
        using var factory = CreateFactory(probe);
        var logger = factory.CreateLogger("LogLeak.Probe.Benchmark");
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = baseline;
        var emitTimer = Stopwatch.StartNew();
        for (var index = 0; index < eventCount; index++)
        {
            logger.LogInformation("representative event {Sequence}", index);
            if ((index & 1023) == 0)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
            }
        }

        emitTimer.Stop();
        var findings = probe.FindLeaks();
        var matchingElapsed = probe.MatchingElapsed;
        var retained = Math.Max(0, peak - baseline);
        Require(findings.Count == 0, "The absent-sentinel benchmark produced a finding.");
        Require(probe.CapturedEventCount == eventCount, "The benchmark did not retain exactly the configured event limit.");
        Console.WriteLine($"PASS benchmark: events={eventCount}, emit-elapsed-ms={emitTimer.Elapsed.TotalMilliseconds:F2}, matching-elapsed-ms={matchingElapsed.TotalMilliseconds:F2}, peak-captured-memory-estimate-bytes={retained}, configured-limit={eventCount}, overflow-behavior=explicit-conservative, disposal=covered.");
        Console.WriteLine();
    }

    private static void PrintGoGate()
    {
        Console.WriteLine("PHASE 0 GO-GATE RECORD");
        Console.WriteLine("100% recall in every declared supported field: PASS - each four-field fixture reports a label and broad location.");
        Console.WriteLine("Zero deterministic false positives without the sentinel: PASS - redaction corpus and 100,000-event absent corpus produced no findings.");
        Console.WriteLine("Zero sentinel text/bytes in diagnostics, telemetry, reports, and test output: PASS - all four supported leak locations audited in memory and on-disk artifact bytes.");
        Console.WriteLine("Predictable redaction behavior: PASS - redacted text passes; opaque object values are explicitly unsupported and are not serialized.");
        Console.WriteLine("ASP.NET Core setup requires only a few lines: PASS - WebApplicationFactory logging setup is exercised.");
        Console.WriteLine("Memory explicitly bounded: PASS - every probe has a positive event limit and overflow is conservative.");
        Console.WriteLine("Benchmark overhead acceptable: UNDECIDED - measured numbers are reported above; no release threshold is invented before independent review.");
        Console.WriteLine("Recommendation: continue only as a narrowly scoped product design/review decision after independent review of this evidence; do not treat this probe as a shipping implementation.");
    }

    private static BoundaryProbe NewProbe(int maximumEvents, int maximumSentinels = 128, int maximumFindings = 4096) => new(new CaptureOptions(maximumEvents, maximumSentinels, maximumFindings));

    private static ILoggerFactory CreateFactory(BoundaryProbe probe) => LoggerFactory.Create(builder =>
    {
        builder.ClearProviders();
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddProvider(probe);
    });

    private static LeakAssertionException CaptureAssertion(BoundaryProbe probe)
    {
        try
        {
            probe.AssertNoLeaks();
        }
        catch (LeakAssertionException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected a safe leak assertion.");
    }

    private static Exception CaptureProbeFailure(BoundaryProbe probe)
    {
        try
        {
            probe.AssertNoLeaks();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected a conservative probe failure.");
    }

    private static Exception CaptureConfigurationFailure(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected a safe configuration failure.");
    }

    private static (string Stdout, string Stderr) CaptureConsole(Action action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            action();
            return (output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static void RequireSafeFindingDiagnostics(IEnumerable<LeakFinding> findings)
    {
        foreach (var finding in findings)
        {
            Require(!string.IsNullOrWhiteSpace(finding.Label) && !finding.ToString().Contains("System.", StringComparison.Ordinal), "Finding diagnostics contained unexpected raw representation data.");
        }
    }

    private static string NewSentinel(string _) => "probe-" + Guid.NewGuid().ToString("N");

    private static void Require(bool condition, string safeMessage)
    {
        if (!condition)
        {
            throw new InvalidOperationException(safeMessage);
        }
    }

    private sealed record SafeFinding(string Label, string Location);

    private sealed record SafeTestResult(string Status, IReadOnlyList<SafeFinding> Findings);

    private sealed record SafeReport(string Kind, int FindingCount, string ContentPolicy);

    private sealed record SafeTelemetry(string Event, string Environment, int FindingCount);

    private sealed class FakeTelemetry
    {
        public string? Input { get; private set; }

        public void Send(SafeTelemetry payload) => Input = JsonSerializer.Serialize(payload);
    }

    private static class DiagnosticSafetyAudit
    {
        public static void AssertAbsent(string sentinel, IReadOnlyDictionary<string, object?> sources)
        {
            var sentinelBytes = Encoding.UTF8.GetBytes(sentinel);
            foreach (var (name, source) in sources)
            {
                switch (source)
                {
                    case string text when text.Contains(sentinel, StringComparison.Ordinal):
                        throw new InvalidOperationException("A product-owned diagnostic source contained a registered sentinel.");
                    case byte[] bytes when Contains(bytes, sentinelBytes):
                        throw new InvalidOperationException("A product-owned artifact contained a registered sentinel.");
                    case null:
                        throw new InvalidOperationException("A diagnostic source was unexpectedly null.");
                }
            }
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length)
            {
                return false;
            }

            for (var index = 0; index <= haystack.Length - needle.Length; index++)
            {
                if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
