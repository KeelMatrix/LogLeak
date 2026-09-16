using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
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
    private const int MaximumPayloadCharacters = 4_096;
    private const int MaximumInspectionUnitsPerEvent = 1_024;
    private const int MaximumFindingsPerEvent = 256;
    private const int MaximumFindingsTotal = 4_096;
    private const long MaximumTransientAllocationBytes = 1_048_576;

    private static int Main(string[] args)
    {
        try
        {
            Console.WriteLine("LogLeak feasibility probe");
            Console.WriteLine("Supported fields: formatted message; string structured-property values; string scope values; exception representation.");
            Console.WriteLine("Matching: exact ordinal literal containment only; no decoding, normalization, hashing, encoding permutations, or object serialization.");
            Console.WriteLine();
            PrintFieldContract();

            var mode = args.FirstOrDefault();
            BenchmarkEvidence.ValidateProvenance(
                mode == "--verify-benchmark-provenance" ? GetOption(args, "--policy") : null,
                mode == "--verify-benchmark-provenance" ? GetOption(args, "--sample-set") : null);

            switch (mode)
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
                case "--performance-samples":
                    RunPerformanceSampleSet();
                    Console.WriteLine("RESULT: PASS - benchmark sample reproduction completed.");
                    return 0;
                case "--verify-benchmark-provenance":
                    Console.WriteLine("RESULT: PASS - benchmark provenance check completed.");
                    return 0;
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
            Console.Error.WriteLine($"Failure: {exception.Message}");
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
        RunExcludedFieldCorpus();
        RunPayloadBoundCorpus();
        RunAggregateInspectionBudgetCorpus();

        Console.WriteLine("Unsupported fixture recorded: arbitrary structured objects are not serialized or inspected; this avoids hidden object-graph behavior.");
        Console.WriteLine();
    }

    private static void PrintFieldContract()
    {
        Console.WriteLine("SUPPORTED FIELD CONTRACT");
        Console.WriteLine("- formatted message: formatter output inspected with ordinal literal containment.");
        Console.WriteLine("- structured property: direct string values in the framework's enumerable key/value state inspected except the reserved {OriginalFormat} template metadata key; values are never serialized.");
        Console.WriteLine("- scope: direct string scope states inspected; structured or arbitrary scope objects are excluded.");
        Console.WriteLine("- exception representation: exception.ToString() inspected with ordinal literal containment; exception payload is never emitted.");
        Console.WriteLine($"- payload bound: every inspected text unit is limited to {MaximumPayloadCharacters} UTF-16 characters (approximately 8 KiB of UTF-16 character data); this covers ordinary test messages while rejecting pathological payloads, with no truncation; exceeding it is explicitly inconclusive.");
        Console.WriteLine($"- aggregate resource budget: at most {MaximumInspectionUnitsPerEvent} structured-entry/scope/text inspection units and {MaximumFindingsPerEvent} findings per event; transient allocation is guarded against a {MaximumTransientAllocationBytes:N0}-byte per-event budget measured on the provider thread; any exceeded budget is explicit inconclusive.");
        Console.WriteLine($"- retained capture budget: at most the configured event count and {MaximumFindingsPerEvent} findings per event ({MaximumFindingsTotal:N0} total findings by default); captured text is not retained.");
        Console.WriteLine("EXCLUDED FIELD CONTRACT");
        Console.WriteLine("- non-string state/scope objects: excluded because recursive serialization or ToString would be unsafe and unreliable.");
        Console.WriteLine("- category and level metadata: not inspected; level is an enum with no sentinel-bearing text payload.");
        Console.WriteLine("- EventId number/name, raw message template, and property names: not inspected; the reserved {OriginalFormat} key is excluded metadata, not a structured property.");
        Console.WriteLine("- downstream Serilog or other sink fields: excluded because they are beyond the Microsoft.Extensions.Logging provider boundary.");
        Console.WriteLine("- excluded-field fixtures below record the observed zero-finding or unsupported result for every excluded claim.");
        Console.WriteLine("DIAGNOSTIC EVIDENCE SCOPE");
        Console.WriteLine("- probe-owned findings/exceptions and simulated console, test-result, report, artifact, and telemetry-double paths are audited; real test-framework adapters and the shared KeelMatrix.Telemetry contract are deferred.");
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

    private static void RunExcludedFieldCorpus()
    {
        Console.WriteLine("EXCLUDED FIELD CORPUS");

        var categorySentinel = NewSentinel("excluded-category");
        using (var probe = NewProbe(8))
        {
            probe.AddSentinel("excluded-category", categorySentinel);
            using var factory = CreateFactory(probe);
            factory.CreateLogger("Category." + categorySentinel).LogInformation("safe category event");
            probe.AssertNoLeaks();
        }

        Console.WriteLine("PASS excluded category: sentinel placed in logger category was not inspected (findings=0).");

        var levelSentinel = NewSentinel("excluded-level");
        using (var probe = NewProbe(8))
        {
            probe.AddSentinel("excluded-level", levelSentinel);
            using var factory = CreateFactory(probe);
            factory.CreateLogger("LogLeak.Probe.Excluded.Level").Log(LogLevel.Information, new EventId(801, "safe-level"), "safe level event", null, static (state, _) => state);
            probe.AssertNoLeaks();
        }

        Console.WriteLine("PASS excluded level: level is an enum with no sentinel-bearing text payload and was not inspected (findings=0).");

        var eventIdSentinel = NewSentinel("excluded-event-id");
        using (var probe = NewProbe(8))
        {
            probe.AddSentinel("excluded-event-id", eventIdSentinel);
            using var factory = CreateFactory(probe);
            factory.CreateLogger("LogLeak.Probe.Excluded.EventId").Log(LogLevel.Information, new EventId(802, eventIdSentinel), "safe event-id event", null, static (state, _) => state);
            probe.AssertNoLeaks();
        }

        Console.WriteLine("PASS excluded EventId name/number: sentinel placed in EventId metadata was not inspected (findings=0).");

        var templateSentinel = NewSentinel("excluded-template");
        using (var probe = NewProbe(8))
        {
            probe.AddSentinel("excluded-template", templateSentinel);
            using var factory = CreateFactory(probe);
            var state = new[]
            {
                new KeyValuePair<string, object?>("{OriginalFormat}", "raw template " + templateSentinel)
            };
            factory.CreateLogger("LogLeak.Probe.Excluded.Template").Log(LogLevel.Information, new EventId(803, "safe-template"), state, null, static (_, _) => "safe formatted event");
            probe.AssertNoLeaks();
        }

        Console.WriteLine("PASS excluded raw template/{OriginalFormat}: reserved template metadata was not classified as StructuredProperty (findings=0).");

        var propertyNameSentinel = NewSentinel("excluded-property-name");
        using (var probe = NewProbe(8))
        {
            probe.AddSentinel("excluded-property-name", propertyNameSentinel);
            using var factory = CreateFactory(probe);
            var state = new[]
            {
                new KeyValuePair<string, object?>(propertyNameSentinel, "safe property value")
            };
            factory.CreateLogger("LogLeak.Probe.Excluded.PropertyName").Log(LogLevel.Information, new EventId(804, "safe-property"), state, null, static (_, _) => "safe formatted event");
            probe.AssertNoLeaks();
        }

        Console.WriteLine("PASS excluded property name: sentinel placed in a structured key was not inspected (findings=0).");

        var downstreamSentinel = NewSentinel("excluded-downstream");
        using (var probe = NewProbe(8))
        {
            probe.AddSentinel("excluded-downstream", downstreamSentinel);
            Require(SerilogCorpus.DirectSinkObserved(downstreamSentinel), "The direct downstream sink fixture did not observe its planted sentinel.");
            probe.AssertNoLeaks();
        }

        Console.WriteLine("PASS excluded downstream sink field: direct Serilog sink observed its sentinel outside the provider boundary; probe findings=0.");
        Console.WriteLine();
    }

    private static void RunPayloadBoundCorpus()
    {
        Console.WriteLine("BOUNDED PAYLOAD CORPUS");
        RunBoundedDetection("formatted message", LeakLocation.FormattedMessage, (logger, sentinel) =>
        {
            logger.Log(LogLevel.Information, new EventId(901), "safe", null, (_, _) => BoundedText(sentinel));
        });
        RunBoundedDetection("structured property", LeakLocation.StructuredProperty, (logger, sentinel) =>
        {
            var state = new[] { new KeyValuePair<string, object?>("Value", BoundedText(sentinel)) };
            logger.Log(LogLevel.Information, new EventId(902), state, null, static (_, _) => "safe");
        });
        RunBoundedDetection("scope", LeakLocation.Scope, (logger, sentinel) =>
        {
            using var scope = logger.BeginScope(BoundedText(sentinel));
            logger.LogInformation("safe");
        });
        RunBoundedDetection("exception representation", LeakLocation.ExceptionRepresentation, (logger, sentinel) =>
        {
            var exception = new InvalidOperationException(BoundedText(sentinel, reserveCharacters: 128));
            logger.Log(LogLevel.Error, new EventId(903), "safe", exception, static (_, _) => "safe");
        });

        RunPayloadLimitFailure("formatted message", (logger, sentinel) =>
        {
            logger.Log(LogLevel.Information, new EventId(911), "safe", null, (_, _) => BeyondBoundText(sentinel));
        });
        RunPayloadLimitFailure("structured property", (logger, sentinel) =>
        {
            var state = new[] { new KeyValuePair<string, object?>("Value", BeyondBoundText(sentinel)) };
            logger.Log(LogLevel.Information, new EventId(912), state, null, static (_, _) => "safe");
        });
        RunPayloadLimitFailure("scope", (logger, sentinel) =>
        {
            using var scope = logger.BeginScope(BeyondBoundText(sentinel));
            logger.LogInformation("safe");
        });
        RunPayloadLimitFailure("exception representation", (logger, sentinel) =>
        {
            var exception = new InvalidOperationException(BeyondBoundText(sentinel));
            logger.Log(LogLevel.Error, new EventId(913), "safe", exception, static (_, _) => "safe");
        });

        Console.WriteLine($"PASS payload bound: all four supported text units detected a sentinel within {MaximumPayloadCharacters} characters; each sentinel beyond the bound produced explicit inconclusive verification, never a clean result.");
        Console.WriteLine();
    }

    private static void RunBoundedDetection(string name, LeakLocation expectedLocation, Action<ILogger, string> emit)
    {
        var label = "payload-inside-" + name.Replace(' ', '-');
        var sentinel = NewSentinel(label);
        using var probe = NewProbe(8);
        probe.AddSentinel(label, sentinel);
        using var factory = CreateFactory(probe);
        emit(factory.CreateLogger("LogLeak.Probe.PayloadBound"), sentinel);
        var findings = probe.FindLeaks();
        Require(findings.Any(finding => finding.Label == label && finding.Location == expectedLocation), "A sentinel within the payload bound was not detected at the expected location.");
        Require(!probe.PayloadLimitExceeded, "A sentinel within the payload bound incorrectly caused an inconclusive result.");
        Console.WriteLine($"PASS payload within bound {name}: detected at {expectedLocation}.");
    }

    private static void RunPayloadLimitFailure(string name, Action<ILogger, string> emit)
    {
        var label = "payload-beyond-" + name.Replace(' ', '-');
        var sentinel = NewSentinel(label);
        using var probe = NewProbe(8);
        probe.AddSentinel(label, sentinel);
        using var factory = CreateFactory(probe);
        emit(factory.CreateLogger("LogLeak.Probe.PayloadBound"), sentinel);
        var failure = CaptureProbeFailure(probe);
        Require(failure is ProbePayloadLimitException && probe.PayloadLimitExceeded, "An oversized payload did not produce an explicit payload-limit result.");
        Require(!failure.ToString().Contains(sentinel, StringComparison.Ordinal), "The payload-limit diagnostic exposed a registered sentinel.");
        Console.WriteLine($"PASS payload beyond bound {name}: explicit inconclusive result, not a clean result.");
    }

    private static void RunAggregateInspectionBudgetCorpus()
    {
        Console.WriteLine("AGGREGATE INSPECTION BUDGET CORPUS");
        const int entryCount = 10_000;
        var label = "aggregate-state";
        var sentinel = NewSentinel(label);
        var state = new CountingState(entryCount, sentinel);
        using var probe = NewProbe(8, maximumFindingsPerEvent: entryCount);
        probe.AddSentinel(label, sentinel);
        using var factory = CreateFactory(probe);
        var logger = factory.CreateLogger("LogLeak.Probe.Aggregate");
        logger.Log(LogLevel.Information, new EventId(921), state, null, static (_, _) => "safe");

        var failure = CaptureProbeFailure(probe);
        Require(failure is ProbeOverflowException && state.EnumerationCount < entryCount, "The aggregate state fixture was fully enumerated before the conservative overflow result.");
        Require(!failure.ToString().Contains(sentinel, StringComparison.Ordinal), "The aggregate overflow diagnostic exposed a registered sentinel.");
        Require(probe.AggregateBudgetExceeded && probe.AggregateBudgetName!.Contains("inspection-unit", StringComparison.Ordinal), "The large state did not stop on the configured inspection-unit budget.");
        Console.WriteLine($"AGGREGATE_STATE=expected-inconclusive; entries={entryCount}; enumerated={state.EnumerationCount}; inspected-units={probe.LastEventInspectionUnits}; inspection-unit-budget={probe.MaximumInspectionUnitsPerEvent}; finding-budget={probe.MaximumFindingsPerEvent}; transient-allocation-budget={probe.MaximumTransientAllocationBytes}; result={failure.GetType().Name}; diagnostic-safe=True");

        var firstLabel = "aggregate-state-first";
        var firstSentinel = NewSentinel(firstLabel);
        var firstState = new CountingState(entryCount, firstSentinel, sentinelIndex: 0);
        using var firstProbe = NewProbe(8, maximumFindingsPerEvent: entryCount);
        firstProbe.AddSentinel(firstLabel, firstSentinel);
        using var firstFactory = CreateFactory(firstProbe);
        firstFactory.CreateLogger("LogLeak.Probe.Aggregate.First").Log(LogLevel.Information, new EventId(922), firstState, null, static (_, _) => "safe");
        var firstFailure = CaptureProbeFailure(firstProbe);
        Require(firstFailure is ProbeOverflowException && firstState.EnumerationCount < entryCount && firstProbe.HasFinding(firstLabel, LeakLocation.StructuredProperty), "The first inspected structured entry was not detected before aggregate overflow became inconclusive.");
        Console.WriteLine($"AGGREGATE_STATE_FIRST=detected-before-inconclusive; entries={entryCount}; enumerated={firstState.EnumerationCount}; finding-recorded={firstProbe.HasFinding(firstLabel, LeakLocation.StructuredProperty)}; result={firstFailure.GetType().Name}");

        var countableState = new CountableState(entryCount, sentinel);
        using var countableProbe = NewProbe(8, maximumFindingsPerEvent: entryCount);
        countableProbe.AddSentinel(label, sentinel);
        using var countableFactory = CreateFactory(countableProbe);
        countableFactory.CreateLogger("LogLeak.Probe.Aggregate.Countable").Log(LogLevel.Information, new EventId(923), countableState, null, static (_, _) => "safe");
        var countableFailure = CaptureProbeFailure(countableProbe);
        Require(countableFailure is ProbeOverflowException && countableState.EnumerationCount == 0, "The countable structured state was enumerated before the aggregate extent check.");
        Console.WriteLine($"AGGREGATE_STATE_COUNTABLE=expected-inconclusive; extent={countableState.Count}; enumerated={countableState.EnumerationCount}; result={countableFailure.GetType().Name}");

        var findingBudgetLabel = "aggregate-finding";
        var findingBudgetSentinel = NewSentinel(findingBudgetLabel);
        using var findingBudgetProbe = NewProbe(8, maximumFindings: MaximumFindingsTotal, maximumFindingsPerEvent: 1);
        findingBudgetProbe.AddSentinel(findingBudgetLabel, findingBudgetSentinel);
        using var findingBudgetFactory = CreateFactory(findingBudgetProbe);
        PlainLoggingCorpus.MessageTemplate(findingBudgetFactory.CreateLogger("LogLeak.Probe.Aggregate.Finding"), findingBudgetSentinel);
        var findingBudgetFailure = CaptureProbeFailure(findingBudgetProbe);
        Require(findingBudgetFailure is ProbeOverflowException && findingBudgetFailure.ToString().Contains("per-event finding", StringComparison.Ordinal), "The per-event finding budget did not stop inspection with a named inconclusive result.");
        Console.WriteLine($"AGGREGATE_FINDING=expected-inconclusive; inspected-units={findingBudgetProbe.LastEventInspectionUnits}; findings={findingBudgetProbe.LastEventFindings}; finding-budget={findingBudgetProbe.MaximumFindingsPerEvent}; result={findingBudgetFailure.GetType().Name}");

        const int scopeCount = 10_000;
        var scopeLabel = "aggregate-scope";
        var scopeSentinel = NewSentinel(scopeLabel);
        var scopes = new CountingScopeProvider(scopeCount, scopeSentinel, sentinelIndex: 0);
        using var scopeProbe = NewProbe(8, maximumFindingsPerEvent: scopeCount);
        scopeProbe.AddSentinel(scopeLabel, scopeSentinel);
        using var scopeFactory = CreateFactory(scopeProbe);
        scopeProbe.SetScopeProvider(scopes);
        scopeFactory.CreateLogger("LogLeak.Probe.Aggregate.Scope").LogInformation("safe");
        var scopeFailure = CaptureProbeFailure(scopeProbe);
        Require(scopeFailure is ProbeOverflowException && scopes.EnumerationCount < scopeCount && scopeProbe.HasFinding(scopeLabel, LeakLocation.Scope), "The first inspected scope was not detected before aggregate overflow became inconclusive.");
        Console.WriteLine($"AGGREGATE_SCOPE_FIRST=detected-before-inconclusive; scopes={scopeCount}; enumerated={scopes.EnumerationCount}; inspected-units={scopeProbe.LastEventInspectionUnits}; inspection-unit-budget={scopeProbe.MaximumInspectionUnitsPerEvent}; finding-recorded={scopeProbe.HasFinding(scopeLabel, LeakLocation.Scope)}; result={scopeFailure.GetType().Name}");

        var countableScopes = new CountableScopeProvider(scopeCount, scopeSentinel, sentinelIndex: null);
        using var countableScopeProbe = NewProbe(8, maximumFindingsPerEvent: scopeCount);
        countableScopeProbe.AddSentinel(scopeLabel, scopeSentinel);
        using var countableScopeFactory = CreateFactory(countableScopeProbe);
        countableScopeProbe.SetScopeProvider(countableScopes);
        countableScopeFactory.CreateLogger("LogLeak.Probe.Aggregate.ScopeCountable").LogInformation("safe");
        var countableScopeFailure = CaptureProbeFailure(countableScopeProbe);
        Require(countableScopeFailure is ProbeOverflowException && countableScopes.EnumerationCount == 0, "The countable scope collection was enumerated before the aggregate extent check.");
        Console.WriteLine($"AGGREGATE_SCOPE_COUNTABLE=expected-inconclusive; extent={countableScopes.ScopeCount}; enumerated={countableScopes.EnumerationCount}; result={countableScopeFailure.GetType().Name}");

        using var transientProbe = NewProbe(8, maximumPayloadCharacters: 2_000_000, maximumTransientAllocationBytes: 1_024);
        transientProbe.AddSentinel("aggregate-transient", NewSentinel("aggregate-transient"));
        using var transientFactory = CreateFactory(transientProbe);
        transientFactory.CreateLogger("LogLeak.Probe.Aggregate.Transient").Log(LogLevel.Information, new EventId(924), "safe", null, static (_, _) => new string('x', 4_096));
        var transientFailure = CaptureProbeFailure(transientProbe);
        Require(transientFailure is ProbeOverflowException && transientFailure.ToString().Contains("transient-allocation", StringComparison.Ordinal), "The transient allocation policy did not produce a named inconclusive result.");
        Console.WriteLine($"AGGREGATE_TRANSIENT=expected-inconclusive; transient-allocation-budget={transientProbe.MaximumTransientAllocationBytes}; result={transientFailure.GetType().Name}; message-names-budget=True");
        Console.WriteLine();
    }

    private static string BoundedText(string sentinel, int reserveCharacters = 0)
    {
        var fillerLength = MaximumPayloadCharacters - sentinel.Length - reserveCharacters;
        Require(fillerLength > 0, "The payload fixture sentinel did not fit within the configured bound.");
        return new string('x', fillerLength) + sentinel;
    }

    private static string BeyondBoundText(string sentinel) => new string('x', MaximumPayloadCharacters + 1) + sentinel;

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
        Require(findingLimitFailure is ProbeOverflowException && findingLimitProbe.AggregateBudgetExceeded && findingLimitFailure.ToString().Contains("finding", StringComparison.Ordinal), "The finding-resource limit did not fail conservatively with a named budget.");
        Console.WriteLine($"PASS overflow: event-limit={limit}, finding-limit=1, retained-before-dispose={beforeDispose}, post-overflow verification remains inconclusive, disposed-state=cleared.");
        Console.WriteLine();
    }

    private static void RunPerformanceSampleSet()
    {
        Console.WriteLine("BENCHMARK SAMPLE SET REPRODUCTION");
        BenchmarkEvidence.PrintSampleSet();
        Console.WriteLine($"sample policy: threshold = ceiling(worst recorded sample x (1 + {BenchmarkEvidence.HeadroomFraction:P0})), rounded to the next {BenchmarkEvidence.TimeRoundingMilliseconds:0} ms or {BenchmarkEvidence.MemoryRoundingBytes:N0} byte increment; this policy is fixed before the measured gate run.");
        Console.WriteLine($"derived threshold: emit={BenchmarkEvidence.EmitThresholdMilliseconds:F2} ms; matching={BenchmarkEvidence.MatchingThresholdMilliseconds:F2} ms; sampled heap delta={BenchmarkEvidence.MemoryThresholdBytes:N0} bytes");
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
        var emitElapsed = emitTimer.Elapsed.TotalMilliseconds;
        var emitPass = emitElapsed <= BenchmarkEvidence.EmitThresholdMilliseconds;
        var matchingPass = matchingElapsed.TotalMilliseconds <= BenchmarkEvidence.MatchingThresholdMilliseconds;
        var memoryPass = retained <= BenchmarkEvidence.MemoryThresholdBytes;

        Console.WriteLine($"benchmark host: calibration=Windows x64, target=net8.0, runtime={RuntimeInformation.FrameworkDescription}, observed-host={RuntimeInformation.OSDescription}, process={RuntimeInformation.ProcessArchitecture}; thresholds are host-relative, not cross-platform performance guarantees.");
        Console.WriteLine($"benchmark policy: threshold = ceiling(worst recorded sample x (1 + {BenchmarkEvidence.HeadroomFraction:P0})), rounded to the next {BenchmarkEvidence.TimeRoundingMilliseconds:0} ms or {BenchmarkEvidence.MemoryRoundingBytes:N0} byte increment; fixed before this measured gate run.");
        Console.WriteLine($"benchmark sample basis: committed sample set; samples={BenchmarkEvidence.SampleCount}; median emit={BenchmarkEvidence.EmitMedianMilliseconds:F2} ms, matching={BenchmarkEvidence.MatchingMedianMilliseconds:F2} ms, sampled heap delta={BenchmarkEvidence.MemoryMedianBytes:N0} bytes; worst emit={BenchmarkEvidence.EmitWorstMilliseconds:F2} ms, matching={BenchmarkEvidence.MatchingWorstMilliseconds:F2} ms, sampled heap delta={BenchmarkEvidence.MemoryWorstBytes:N0} bytes.");
        Console.WriteLine($"benchmark derived threshold: emit={BenchmarkEvidence.EmitThresholdMilliseconds:F2} ms; matching={BenchmarkEvidence.MatchingThresholdMilliseconds:F2} ms; sampled heap delta={BenchmarkEvidence.MemoryThresholdBytes:N0} bytes");
        PrintBenchmarkGate("emit", BenchmarkEvidence.EmitWorstMilliseconds, BenchmarkEvidence.EmitThresholdMilliseconds, emitElapsed, "ms", emitPass);
        PrintBenchmarkGate("matching", BenchmarkEvidence.MatchingWorstMilliseconds, BenchmarkEvidence.MatchingThresholdMilliseconds, matchingElapsed.TotalMilliseconds, "ms", matchingPass);
        PrintBenchmarkGate("sampled heap delta", BenchmarkEvidence.MemoryWorstBytes, BenchmarkEvidence.MemoryThresholdBytes, retained, "bytes", memoryPass);
        var benchmarkPass = emitPass && matchingPass && memoryPass;
        Console.WriteLine($"benchmark verdict: {(benchmarkPass ? "PASS" : "FAIL")} - all measured dimensions must remain within the fixed host-relative thresholds.");
        Require(benchmarkPass, "The benchmark exceeded the fixed host-relative acceptance threshold.");
        Console.WriteLine($"PASS benchmark: events={eventCount}, emit-elapsed-ms={emitElapsed:F2}, matching-elapsed-ms={matchingElapsed.TotalMilliseconds:F2}, peak-captured-memory-estimate-bytes={retained}, configured-limit={eventCount}, overflow-behavior=explicit-conservative, disposal=covered.");
        Console.WriteLine();
    }

    private static void PrintBenchmarkGate(string name, double baseline, double threshold, double measured, string unit, bool passed)
    {
        var headroom = threshold - measured;
        Console.WriteLine($"benchmark gate {name}: sample-worst={baseline:F2} {unit}; threshold={threshold:F2} {unit}; measured={measured:F2} {unit}; margin={BenchmarkEvidence.HeadroomFraction:P0}; headroom={headroom:F2} {unit}; {(passed ? "PASS" : "FAIL")}");
    }

    private static void PrintGoGate()
    {
        Console.WriteLine("PHASE 0 GO-GATE RECORD");
        Console.WriteLine("100% recall in every declared supported field: PASS - each four-field fixture reports a label and broad location.");
        Console.WriteLine("Zero deterministic false positives without the sentinel: PASS - redaction corpus and 100,000-event absent corpus produced no findings.");
        Console.WriteLine("Zero sentinel text/bytes in probe-owned diagnostics and simulated output paths: PASS - all four supported leak locations audited in memory and on-disk artifact bytes; real test-framework adapters and shared KeelMatrix.Telemetry remain deferred to Phase 1.");
        Console.WriteLine("Predictable redaction behavior: PASS - redacted text passes; opaque object values are explicitly unsupported and are not serialized.");
        Console.WriteLine("ASP.NET Core setup requires only a few lines: PASS - WebApplicationFactory logging setup is exercised.");
        Console.WriteLine($"Resource limits: PASS - event capture, {MaximumInspectionUnitsPerEvent} inspection units/event, {MaximumFindingsPerEvent} findings/event, a {MaximumTransientAllocationBytes:N0}-byte transient-allocation guard/event, and {MaximumPayloadCharacters}-character text units are guarded; overflow is explicit inconclusive.");
        Console.WriteLine("Supported/excluded field contract: PASS - {OriginalFormat} is excluded metadata and recorded excluded-field fixtures match the classifier output.");
        Console.WriteLine($"Benchmark overhead acceptable: PASS - fixed thresholds derive mechanically from the committed {BenchmarkEvidence.SampleCount}-sample host baseline with {BenchmarkEvidence.HeadroomFraction:P0} headroom; emit, matching, and sampled-memory verdicts are printed above.");
        Console.WriteLine("Diagnostic claim: PASS within probe-owned and simulated output paths; Phase 1 must prove real xUnit/NUnit/MSTest adapters and the shared KeelMatrix.Telemetry contract.");
        Console.WriteLine("Recommendation: continue only as a narrowly scoped product design/review decision after independent review of this evidence; do not treat this probe as a shipping implementation.");
    }

    private static BoundaryProbe NewProbe(int maximumEvents, int maximumSentinels = 128, int maximumFindings = MaximumFindingsTotal, int maximumPayloadCharacters = MaximumPayloadCharacters, int maximumInspectionUnitsPerEvent = MaximumInspectionUnitsPerEvent, int maximumFindingsPerEvent = MaximumFindingsPerEvent, long maximumTransientAllocationBytes = MaximumTransientAllocationBytes) => new(new CaptureOptions(maximumEvents, maximumSentinels, maximumFindings, maximumPayloadCharacters: maximumPayloadCharacters, maximumInspectionUnitsPerEvent: maximumInspectionUnitsPerEvent, maximumFindingsPerEvent: maximumFindingsPerEvent, maximumTransientAllocationBytes: maximumTransientAllocationBytes));

    private static string? GetOption(string[] args, string optionName)
    {
        var optionIndex = Array.IndexOf(args, optionName);
        if (optionIndex < 0)
        {
            return null;
        }

        if (optionIndex == args.Length - 1)
        {
            throw new InvalidOperationException($"Option '{optionName}' requires a path.");
        }

        return args[optionIndex + 1];
    }

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

    private sealed class CountingState : IEnumerable<KeyValuePair<string, object?>>
    {
        private readonly int _count;
        private readonly string _sentinel;

        public CountingState(int count, string sentinel, int? sentinelIndex = null)
        {
            _count = count;
            _sentinel = sentinel;
            _sentinelIndex = sentinelIndex;
        }

        private readonly int? _sentinelIndex;

        public int EnumerationCount { get; private set; }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var index = 0; index < _count; index++)
            {
                EnumerationCount++;
                var value = !_sentinelIndex.HasValue || _sentinelIndex.Value == index ? _sentinel : "safe-" + index;
                yield return new KeyValuePair<string, object?>("Value-" + index, value);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountableState : IReadOnlyCollection<KeyValuePair<string, object?>>
    {
        private readonly string _sentinel;

        public CountableState(int count, string sentinel)
        {
            Count = count;
            _sentinel = sentinel;
        }

        public int Count { get; }

        public int EnumerationCount { get; private set; }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                EnumerationCount++;
                yield return new KeyValuePair<string, object?>("Value-" + index, _sentinel);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private class CountingScopeProvider : IExternalScopeProvider
    {
        private readonly int _count;
        private readonly string _sentinel;
        private readonly int? _sentinelIndex;

        public CountingScopeProvider(int count, string sentinel, int? sentinelIndex)
        {
            _count = count;
            _sentinel = sentinel;
            _sentinelIndex = sentinelIndex;
        }

        public int EnumerationCount { get; private set; }

        public IDisposable Push(object? state) => NoopDisposable.Instance;

        public void ForEachScope<TState>(Action<object?, TState> callback, TState state)
        {
            for (var index = 0; index < _count; index++)
            {
                EnumerationCount++;
                var value = !_sentinelIndex.HasValue || _sentinelIndex.Value == index ? _sentinel : "safe-scope-" + index;
                callback(value, state);
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class CountableScopeProvider : CountingScopeProvider, ICountableScopeProvider
    {
        public CountableScopeProvider(int count, string sentinel, int? sentinelIndex)
            : base(count, sentinel, sentinelIndex)
        {
            ScopeCount = count;
        }

        public int ScopeCount { get; }
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
