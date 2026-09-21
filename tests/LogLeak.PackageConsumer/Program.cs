using KeelMatrix.LogLeak;
using Microsoft.Extensions.Logging;

using var cleanProbe = new LogLeakProbe()
    .AddSecret("probe-pass", "synthetic-consumer-secret-12ab");
using (var cleanFactory = LoggerFactory.Create(builder => builder.AddProvider(cleanProbe.Provider)))
{
    cleanFactory.CreateLogger("PackageConsumer").LogInformation("safe package event");
    cleanProbe.AssertNoLeaks();
}

const string stateSentinel = "synthetic-consumer-dictionary-state-23cd";
using var stateProbe = new LogLeakProbe().AddSecret("S", stateSentinel);
using (var stateFactory = LoggerFactory.Create(builder => builder.AddProvider(stateProbe.Provider)))
{
    stateFactory.CreateLogger("PackageConsumer").Log(
        LogLevel.Information,
        new EventId(11),
        new Dictionary<string, string> { ["Authorization"] = stateSentinel },
        null,
        static (_, _) => "safe dictionary state event");
}

var stateResult = stateProbe.Verify();
if (stateResult.Status != LogLeakVerificationStatus.LeaksDetected
    || !stateResult.Findings.Any(finding => finding.Location == LogLeakLocation.StructuredProperty))
{
    throw new InvalidOperationException("The netstandard2.0 package asset did not inspect string-valued dictionary state.");
}

const string reentrantSentinel = "synthetic-consumer-reentrant-45ef";
using var reentrantProbe = new LogLeakProbe().AddSecret("R", reentrantSentinel);
using (var reentrantFactory = LoggerFactory.Create(builder => builder.AddProvider(reentrantProbe.Provider)))
{
    var reentrantLogger = reentrantFactory.CreateLogger("PackageConsumer");
    reentrantLogger.Log(
        LogLevel.Information,
        new EventId(12),
        "outer safe event",
        null,
        (state, _) =>
        {
            reentrantLogger.LogInformation("nested safe event");
            return state;
        });
}

var reentrantResult = reentrantProbe.Verify();
if (reentrantResult.Status != LogLeakVerificationStatus.Inconclusive
    || reentrantResult.CapturedEventCount != 1
    || !reentrantResult.InconclusiveReason!.Contains("reentrant", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("The netstandard2.0 package asset did not reject same-probe reentrant capture.");
}

const string plantedValue = "synthetic-consumer-planted-91de";
using var plantedProbe = new LogLeakProbe()
    .AddSecret("leak", plantedValue);
using (var plantedFactory = LoggerFactory.Create(builder => builder.AddProvider(plantedProbe.Provider)))
{
    plantedFactory.CreateLogger("PackageConsumer").LogInformation("planted value {Value}", plantedValue);
    var failure = AssertLeak(plantedProbe);
    if (failure.Contains(plantedValue, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The package consumer observed sentinel text in a library diagnostic.");
    }
}

using var boundedProbe = new LogLeakProbe(new LogLeakOptions(maximumCapturedEvents: 1))
    .AddSecret("B2", "synthetic-consumer-bound-34ef");
using (var boundedFactory = LoggerFactory.Create(builder => builder.AddProvider(boundedProbe.Provider)))
{
    var boundedLogger = boundedFactory.CreateLogger("PackageConsumer");
    boundedLogger.LogInformation("first bounded event");
    boundedLogger.LogInformation("second bounded event");
}

var boundedResult = boundedProbe.Verify();
if (boundedResult.Status != LogLeakVerificationStatus.Inconclusive
    || !boundedResult.InconclusiveReason!.Contains("capture event budget", StringComparison.Ordinal))
{
    throw new InvalidOperationException("The netstandard2.0 package asset did not fail closed on a deterministic capture bound breach.");
}

try
{
    boundedProbe.AssertNoLeaks();
    throw new InvalidOperationException("The netstandard2.0 package asset did not throw for a deterministic capture bound breach.");
}
catch (LogLeakInconclusiveException exception) when (!exception.ToString().Contains("synthetic-consumer-bound-34ef", StringComparison.Ordinal))
{
}

Console.WriteLine("Package consumer smoke passed.");

static string AssertLeak(LogLeakProbe probe)
{
    try
    {
        probe.AssertNoLeaks();
        throw new InvalidOperationException("The package consumer did not detect the planted value.");
    }
    catch (LogLeakAssertionException exception)
    {
        return exception.ToString();
    }
}
