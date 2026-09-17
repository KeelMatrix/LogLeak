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
