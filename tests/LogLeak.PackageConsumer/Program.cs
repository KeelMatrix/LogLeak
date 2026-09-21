using KeelMatrix.LogLeak;
using Microsoft.Extensions.Logging;

using var cleanProbe = new LogLeakProbe()
    .AddSecret("probe-pass", "synthetic-consumer-secret-12ab");
using (var cleanFactory = LoggerFactory.Create(builder => builder.AddProvider(cleanProbe.Provider)))
{
    cleanFactory.CreateLogger("PackageConsumer").LogInformation("safe package event");
    cleanProbe.AssertNoLeaks();
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
