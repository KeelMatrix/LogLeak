using KeelMatrix.LogLeak;
using Microsoft.Extensions.Logging;

var loadedLogLeakAssembly = typeof(LogLeakProbe).Assembly;
Console.WriteLine($"Consumer runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"LogLeak asset: {loadedLogLeakAssembly.Location}");
if (!loadedLogLeakAssembly.Location.EndsWith(
        Path.Combine("net8.0", "KeelMatrix.LogLeak.dll"),
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("The normal package consumer did not load the packaged net8.0 asset.");
}

const string documentedSentinel = "synthetic-logger-value";
using var documentedProbe = new LogLeakProbe().AddSecret("token", documentedSentinel);
using (var documentedFactory = LoggerFactory.Create(builder => builder.AddProvider(documentedProbe.Provider)))
{
    DocumentedGeneratedLogging.RequestCompleted(documentedFactory.CreateLogger("PaymentClient"));
}

documentedProbe.AssertNoLeaks();

using var cleanProbe = new LogLeakProbe().AddSecret("C", "synthetic-normal-consumer-clean-12ab");
using (var cleanFactory = LoggerFactory.Create(builder => builder.AddProvider(cleanProbe.Provider)))
{
    cleanFactory.CreateLogger("PackageConsumer.Net8").LogInformation("safe package event");
}

cleanProbe.AssertNoLeaks();

const string generatedSentinel = "synthetic-normal-consumer-generated-34cd";
using var generatedProbe = new LogLeakProbe().AddSecret("G", generatedSentinel);
using (var generatedFactory = LoggerFactory.Create(builder => builder.AddProvider(generatedProbe.Provider)))
{
    GeneratedLogging.Write(generatedFactory.CreateLogger("PackageConsumer.Net8"), generatedSentinel);
}

var generatedResult = generatedProbe.Verify();
if (generatedResult.Status != LogLeakVerificationStatus.LeaksDetected
    || !generatedResult.Findings.Any(finding => finding.Location == LogLeakLocation.FormattedMessage))
{
    throw new InvalidOperationException("The normal net8.0 package consumer did not verify source-generated logging.");
}

AssertPlantedLeakIsSafe();
AssertProviderArgumentCollisionsAreSafe();

Console.WriteLine("Normal net8.0 package consumer smoke passed.");

static void AssertPlantedLeakIsSafe()
{
    const string plantedValue = "synthetic-normal-consumer-planted-56ef";
    using var probe = new LogLeakProbe().AddSecret("leak", plantedValue);
    using (var factory = LoggerFactory.Create(builder => builder.AddProvider(probe.Provider)))
    {
        factory.CreateLogger("PackageConsumer.Net8").LogInformation("planted value {Value}", plantedValue);
    }

    try
    {
        probe.AssertNoLeaks();
        throw new InvalidOperationException("The normal net8.0 package consumer did not detect the planted value.");
    }
    catch (LogLeakAssertionException exception)
    {
        var diagnostic = exception.ToString();
        if (diagnostic.Contains(plantedValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The normal net8.0 package consumer exposed the planted value in a diagnostic.");
        }
    }
}

static void AssertProviderArgumentCollisionsAreSafe()
{
    const string fixedWording = "Value cannot be null.";
    const string categoryNameDiagnostic = "Value cannot be null. Parameter: categoryName.";
    const string formatterDiagnostic = "Value cannot be null. Parameter: formatter.";
    const string scopeProviderDiagnostic = "Value cannot be null. Parameter: newScopeProvider.";

    AssertProviderArgumentIsSafe(fixedWording, provider => provider.CreateLogger(null!), "categoryName");
    AssertProviderArgumentIsSafe(categoryNameDiagnostic, provider => provider.CreateLogger(null!), "categoryName");
    AssertProviderArgumentIsSafe(fixedWording, provider => provider.CreateLogger("PackageConsumer.Net8").Log<string>(LogLevel.Information, default, "safe", null, null!), "formatter");
    AssertProviderArgumentIsSafe(formatterDiagnostic, provider => provider.CreateLogger("PackageConsumer.Net8").Log<string>(LogLevel.Information, default, "safe", null, null!), "formatter");
    AssertProviderArgumentIsSafe(fixedWording, provider => ((ISupportExternalScope)provider).SetScopeProvider(null!), "newScopeProvider");
    AssertProviderArgumentIsSafe(scopeProviderDiagnostic, provider => ((ISupportExternalScope)provider).SetScopeProvider(null!), "newScopeProvider");
}

static void AssertProviderArgumentIsSafe(string sentinel, Action<ILoggerProvider> action, string parameterName)
{
    using var probe = new LogLeakProbe().AddSecret("marker", sentinel);
    var exception = AssertThrows<ArgumentNullException>(() => action(probe.Provider));
    if (!string.Equals(exception.ParamName, parameterName, StringComparison.Ordinal)
        || exception.Message.Contains(sentinel, StringComparison.Ordinal)
        || exception.ToString().Contains(sentinel, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The normal net8.0 package consumer observed an unsafe provider argument diagnostic.");
    }
}

static TException AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

internal static partial class DocumentedGeneratedLogging
{
    [LoggerMessage(EventId = 42, Level = LogLevel.Information, Message = "request completed")]
    internal static partial void RequestCompleted(ILogger logger);
}

internal static partial class GeneratedLogging
{
    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "generated package value {Value}")]
    internal static partial void Write(ILogger logger, string value);
}
