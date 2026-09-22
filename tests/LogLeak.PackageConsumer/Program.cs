using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using KeelMatrix.LogLeak;
using KeelMatrix.LogLeak.Documentation;
using Microsoft.Extensions.Logging;

var netstandardAsset = PackageAssetLoader.AssetPath;
if (!File.Exists(netstandardAsset))
{
    throw new InvalidOperationException($"The packaged netstandard2.0 asset was not copied to '{netstandardAsset}'.");
}

var loadedLogLeakAssembly = typeof(LogLeakProbe).Assembly;
Console.WriteLine($"Consumer runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"LogLeak asset: {loadedLogLeakAssembly.Location}");
if (!string.Equals(
        Path.GetFullPath(loadedLogLeakAssembly.Location),
        Path.GetFullPath(netstandardAsset),
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("The package consumer did not execute the packaged netstandard2.0 LogLeak asset.");
}

SourceGeneratedLoggingExample.Run();

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

const string generatedSentinel = "synthetic-consumer-generated-67ab";
using var generatedProbe = new LogLeakProbe().AddSecret("G", generatedSentinel);
using (var generatedFactory = LoggerFactory.Create(builder => builder.AddProvider(generatedProbe.Provider)))
{
    GeneratedLogging.Write(generatedFactory.CreateLogger("PackageConsumer"), generatedSentinel);
}

var generatedResult = generatedProbe.Verify();
if (generatedResult.Status != LogLeakVerificationStatus.LeaksDetected
    || !generatedResult.Findings.Any(finding => finding.Location == LogLeakLocation.FormattedMessage))
{
    throw new InvalidOperationException("The packaged source-generated LoggerMessage path was not verified.");
}

const string scopeSentinel = "synthetic-consumer-scope-handle-78bc";
using var scopeProbe = new LogLeakProbe().AddSecret("H", scopeSentinel);
var scopeLogger = scopeProbe.Provider.CreateLogger("PackageConsumer");
var scopeHandle = scopeLogger.BeginScope(scopeSentinel);
if (scopeHandle is null)
{
    throw new InvalidOperationException("The package consumer scope handle was null.");
}

if ((scopeHandle.ToString() ?? string.Empty).Contains(scopeSentinel, StringComparison.Ordinal))
{
    throw new InvalidOperationException("The package consumer scope handle exposed the registered sentinel.");
}

scopeLogger.LogInformation("event inside scope");
scopeHandle.Dispose();
scopeLogger.LogInformation("event after scope");
if (scopeProbe.Verify().Findings.Count != 1)
{
    throw new InvalidOperationException("The package consumer scope handle did not preserve disposal behavior.");
}

AssertProviderArgumentIsSafe("categoryName", provider => provider.CreateLogger(null!), expectedParameterName: null);
AssertProviderArgumentIsSafe("formatter", provider => provider.CreateLogger("PackageConsumer").Log<string>(LogLevel.Information, default, "safe", null, null!), expectedParameterName: null);
AssertProviderArgumentIsSafe("newScopeProvider", provider => ((ISupportExternalScope)provider).SetScopeProvider(null!), expectedParameterName: null);
AssertProviderArgumentIsSafe("Value cannot be null.", provider => provider.CreateLogger(null!), expectedParameterName: "categoryName");
AssertProviderArgumentIsSafe("Value cannot be null. Parameter: categoryName.", provider => provider.CreateLogger(null!), expectedParameterName: "categoryName");
AssertProviderArgumentIsSafe("Value cannot be null.", provider => provider.CreateLogger("PackageConsumer").Log<string>(LogLevel.Information, default, "safe", null, null!), expectedParameterName: "formatter");
AssertProviderArgumentIsSafe("Value cannot be null. Parameter: formatter.", provider => provider.CreateLogger("PackageConsumer").Log<string>(LogLevel.Information, default, "safe", null, null!), expectedParameterName: "formatter");
AssertProviderArgumentIsSafe("Value cannot be null.", provider => ((ISupportExternalScope)provider).SetScopeProvider(null!), expectedParameterName: "newScopeProvider");
AssertProviderArgumentIsSafe("Value cannot be null. Parameter: newScopeProvider.", provider => ((ISupportExternalScope)provider).SetScopeProvider(null!), expectedParameterName: "newScopeProvider");

const string duringCaptureSentinel = "synthetic-consumer-during-capture-89cd";
using var duringCaptureProbe = new LogLeakProbe().AddSecret("I", duringCaptureSentinel);
using (var duringCaptureFactory = LoggerFactory.Create(builder => builder.AddProvider(duringCaptureProbe.Provider)))
{
    LogLeakVerificationResult? nested = null;
    var duringCaptureLogger = duringCaptureFactory.CreateLogger("PackageConsumer");
    duringCaptureLogger.Log(LogLevel.Information, default, duringCaptureSentinel, null, (state, _) =>
    {
        nested = duringCaptureProbe.Verify();
        return state;
    });

    if (nested?.Status != LogLeakVerificationStatus.Inconclusive)
    {
        throw new InvalidOperationException("Verification during an active formatter callback was not rejected.");
    }
}

if (duringCaptureProbe.Verify().Status != LogLeakVerificationStatus.LeaksDetected)
{
    throw new InvalidOperationException("The outer package consumer verification did not preserve its finding.");
}

const string exceptionSentinel = "synthetic-consumer-exception-capture-9ade";
using var exceptionProbe = new LogLeakProbe().AddSecret("K", exceptionSentinel);
var exceptionCallback = new DuringCaptureException(exceptionProbe, exceptionSentinel);
using (var exceptionFactory = LoggerFactory.Create(builder => builder.AddProvider(exceptionProbe.Provider)))
{
    exceptionFactory.CreateLogger("PackageConsumer").LogError(exceptionCallback, "safe exception event");
}

if (exceptionCallback.DuringCapture?.Status != LogLeakVerificationStatus.Inconclusive
    || !exceptionProbe.Verify().Findings.Any(finding => finding.Location == LogLeakLocation.ExceptionRepresentation))
{
    throw new InvalidOperationException("Verification during exception representation capture was not rejected.");
}

const string callbackStateSentinel = "synthetic-consumer-state-capture-abcf";
const string callbackScopeSentinel = "synthetic-consumer-scope-capture-def0";
using var callbackProbe = new LogLeakProbe()
    .AddSecret("L", callbackStateSentinel)
    .AddSecret("N", callbackScopeSentinel);
var callbackState = new DuringCaptureEntries(callbackProbe, "Authorization", callbackStateSentinel);
using (var callbackFactory = LoggerFactory.Create(builder => builder.AddProvider(callbackProbe.Provider)))
{
    var callbackLogger = callbackFactory.CreateLogger("PackageConsumer");
    callbackLogger.Log(LogLevel.Information, default, callbackState, null, static (_, _) => "safe state event");
    var callbackScope = new DuringCaptureEntries(callbackProbe, "ScopeToken", callbackScopeSentinel);
    using (callbackLogger.BeginScope(callbackScope))
    {
        callbackLogger.LogInformation("safe scope event");
    }

    if (callbackState.DuringCapture?.Status != LogLeakVerificationStatus.Inconclusive
        || callbackScope.DuringCapture?.Status != LogLeakVerificationStatus.Inconclusive)
    {
        throw new InvalidOperationException("Verification during state or scope enumeration was not rejected.");
    }
}

var callbackResult = callbackProbe.Verify();
if (callbackResult.Status != LogLeakVerificationStatus.LeaksDetected
    || !callbackResult.Findings.Any(finding => finding.Location == LogLeakLocation.StructuredProperty)
    || !callbackResult.Findings.Any(finding => finding.Location == LogLeakLocation.Scope))
{
    throw new InvalidOperationException("The package consumer callback findings were not preserved.");
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

static void AssertProviderArgumentIsSafe(string sentinel, Action<ILoggerProvider> action, string? expectedParameterName)
{
    using var probe = new LogLeakProbe().AddSecret("J", sentinel);
    var exception = AssertThrows<ArgumentNullException>(() => action(probe.Provider));
    if (!string.Equals(exception.ParamName, expectedParameterName, StringComparison.Ordinal)
        || exception.Message.Contains(sentinel, StringComparison.Ordinal)
        || exception.ToString().Contains(sentinel, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("A provider argument diagnostic contained the registered sentinel.");
    }
}

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

internal static partial class GeneratedLogging
{
    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "generated package value {Value}")]
    internal static partial void Write(ILogger logger, string value);
}

internal static class PackageAssetLoader
{
    internal static readonly string AssetPath = Path.Combine(AppContext.BaseDirectory, "consumer-assets", "netstandard2.0", "KeelMatrix.LogLeak.dll");

    [ModuleInitializer]
    internal static void Register()
    {
        AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
            string.Equals(assemblyName.Name, "KeelMatrix.LogLeak", StringComparison.Ordinal)
                && File.Exists(AssetPath)
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(AssetPath)
                : null;
    }
}

internal sealed class DuringCaptureException : Exception
{
    private readonly LogLeakProbe probe;
    private readonly string representation;

    internal DuringCaptureException(LogLeakProbe probe, string representation)
        : base("safe exception")
    {
        this.probe = probe;
        this.representation = representation;
    }

    internal LogLeakVerificationResult? DuringCapture { get; private set; }

    public override string ToString()
    {
        DuringCapture = probe.Verify();
        return representation;
    }
}

internal sealed class DuringCaptureEntries : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly LogLeakProbe probe;
    private readonly string key;
    private readonly string value;

    internal DuringCaptureEntries(LogLeakProbe probe, string key, string value)
    {
        this.probe = probe;
        this.key = key;
        this.value = value;
    }

    internal LogLeakVerificationResult? DuringCapture { get; private set; }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        DuringCapture = probe.Verify();
        yield return new KeyValuePair<string, object?>(key, value);
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => GetEnumerator();
}
