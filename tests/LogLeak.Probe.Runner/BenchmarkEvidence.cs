using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogLeak.Probe.Runner;

internal static class BenchmarkEvidence
{
    private const string SupportedRuleId = "worst-recorded-sample-plus-headroom-v1";
    private const string PolicyFileName = "BENCHMARK-POLICY.md";
    private const string SampleSetFileName = "BenchmarkSamples.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SampleSetDocument CommittedSampleSet = LoadSampleSet(GetDefaultSampleSetPath());

    internal static BenchmarkStatistics CommittedStatistics => Calculate(CommittedSampleSet);

    public static void PrintSampleSet()
    {
        var statistics = CommittedStatistics;
        Console.WriteLine("sample host: Windows x64; target=net8.0; runtime=.NET 8.0.31; workload=100000 events");
        Console.WriteLine($"sample count: {CommittedSampleSet.Samples.Count}");
        foreach (var sample in CommittedSampleSet.Samples)
        {
            Console.WriteLine($"sample {sample.Sample}: emit={sample.EmitMilliseconds:F2} ms; matching={sample.MatchingMilliseconds:F2} ms; sampled heap delta={sample.MemoryBytes:N0} bytes");
        }

        PrintStatistics(statistics);
    }

    public static void ValidateProvenance(string? policyPath = null, string? sampleSetPath = null)
    {
        var failures = new List<string>();
        SampleSetDocument? sampleSet = null;
        string? policy = null;

        try
        {
            sampleSet = LoadSampleSet(sampleSetPath ?? GetDefaultSampleSetPath());
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            failures.Add($"sample set could not be loaded: {exception.Message}");
        }

        try
        {
            policy = File.ReadAllText(policyPath ?? GetDefaultPolicyPath());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add($"benchmark policy could not be loaded: {exception.Message}");
        }

        if (sampleSet is not null)
        {
            ValidateSampleSet(sampleSet, failures);
        }

        if (sampleSet is not null && policy is not null && failures.Count == 0)
        {
            ValidatePolicyDocument(sampleSet, policy, failures);
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Benchmark provenance check failed:\n- " + string.Join("\n- ", failures));
        }

        var statistics = Calculate(sampleSet!);
        var liveStatistics = CommittedStatistics;
        ValidateLiveThresholdBinding(liveStatistics, statistics, failures);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Benchmark provenance check failed:\n- " + string.Join("\n- ", failures));
        }

        Console.WriteLine($"PASS benchmark threshold binding: live gate consumes emit {FormatMilliseconds(liveStatistics.EmitThresholdMilliseconds)} ms, matching {FormatMilliseconds(liveStatistics.MatchingThresholdMilliseconds)} ms, memory {liveStatistics.MemoryThresholdBytes:N0} bytes from the committed sample-set recomputation.");
        Console.WriteLine($"PASS benchmark provenance: samples={sampleSet!.Samples.Count}; median=emit {statistics.EmitMedianMilliseconds:F2} ms, matching {statistics.MatchingMedianMilliseconds:F2} ms, memory {statistics.MemoryMedianBytes:N0} bytes; worst=emit {statistics.EmitWorstMilliseconds:F2} ms, matching {statistics.MatchingWorstMilliseconds:F2} ms, memory {statistics.MemoryWorstBytes:N0} bytes; thresholds=emit {FormatMilliseconds(statistics.EmitThresholdMilliseconds)} ms, matching {FormatMilliseconds(statistics.MatchingThresholdMilliseconds)} ms, memory {statistics.MemoryThresholdBytes:N0} bytes; rule={sampleSet.Rule.RuleId}; headroom={sampleSet.Rule.HeadroomPercent}%");
    }

    private static void ValidateLiveThresholdBinding(BenchmarkStatistics liveStatistics, BenchmarkStatistics recomputedStatistics, List<string> failures)
    {
        CompareThreshold("emit", liveStatistics.EmitThresholdMilliseconds, recomputedStatistics.EmitThresholdMilliseconds, failures);
        CompareThreshold("matching", liveStatistics.MatchingThresholdMilliseconds, recomputedStatistics.MatchingThresholdMilliseconds, failures);
        CompareThreshold("memory", liveStatistics.MemoryThresholdBytes, recomputedStatistics.MemoryThresholdBytes, failures);
    }

    private static void CompareThreshold(string name, double consumed, double recomputed, List<string> failures)
    {
        if (!NearlyEqual(consumed, recomputed))
        {
            failures.Add($"live gate {name} threshold is {FormatMilliseconds(consumed)} ms, but recomputation from the committed sample set is {FormatMilliseconds(recomputed)} ms.");
        }
    }

    private static void CompareThreshold(string name, long consumed, long recomputed, List<string> failures)
    {
        if (consumed != recomputed)
        {
            failures.Add($"live gate {name} threshold is {consumed:N0} bytes, but recomputation from the committed sample set is {recomputed:N0} bytes.");
        }
    }

    private static void ValidateSampleSet(SampleSetDocument sampleSet, List<string> failures)
    {
        if (!string.Equals(sampleSet.Schema, "benchmark-sample-set-v1", StringComparison.Ordinal))
        {
            failures.Add($"sample set schema '{sampleSet.Schema}' is unsupported.");
        }

        if (!string.Equals(sampleSet.Rule.RuleId, SupportedRuleId, StringComparison.Ordinal))
        {
            failures.Add($"policy rule '{sampleSet.Rule.RuleId}' is not implemented by the runner; expected '{SupportedRuleId}'.");
        }

        if (sampleSet.Rule.HeadroomPercent <= 0 || sampleSet.Rule.HeadroomPercent >= 100)
        {
            failures.Add($"headroom percent {sampleSet.Rule.HeadroomPercent} is outside the supported range.");
        }

        if (sampleSet.Rule.TimeRoundingMilliseconds <= 0 || !double.IsFinite(sampleSet.Rule.TimeRoundingMilliseconds))
        {
            failures.Add("time rounding must be a positive finite value.");
        }

        if (sampleSet.Rule.MemoryRoundingBytes <= 0)
        {
            failures.Add("memory rounding must be positive.");
        }

        if (sampleSet.Samples is null || sampleSet.Samples.Count < 10)
        {
            failures.Add($"committed sample set has {sampleSet.Samples?.Count ?? 0} samples; at least ten are required.");
            return;
        }

        for (var index = 0; index < sampleSet.Samples.Count; index++)
        {
            var sample = sampleSet.Samples[index];
            if (sample.Sample != index + 1)
            {
                failures.Add($"sample sequence is internally inconsistent at position {index + 1}: recorded index is {sample.Sample}.");
            }

            if (!double.IsFinite(sample.EmitMilliseconds) || sample.EmitMilliseconds < 0)
            {
                failures.Add($"sample {index + 1} has an invalid emit measurement.");
            }

            if (!double.IsFinite(sample.MatchingMilliseconds) || sample.MatchingMilliseconds < 0)
            {
                failures.Add($"sample {index + 1} has an invalid matching measurement.");
            }

            if (sample.MemoryBytes < 0)
            {
                failures.Add($"sample {index + 1} has a negative memory measurement.");
            }
        }
    }

    private static void ValidatePolicyDocument(SampleSetDocument sampleSet, string policy, List<string> failures)
    {
        var statistics = Calculate(sampleSet);
        var metadataMatch = Regex.Match(policy, @"<!--\s*benchmark-policy:\s*(?<metadata>[^>]+?)\s*-->", RegexOptions.CultureInvariant);
        if (!metadataMatch.Success)
        {
            failures.Add("policy metadata marker is missing.");
        }
        else
        {
            var metadata = ParseMetadata(metadataMatch.Groups["metadata"].Value, failures);
            CompareMetadata(metadata, sampleSet.Rule, failures);
        }

        var expectedFormula = $"threshold = ceiling(worst recorded sample × {1 + sampleSet.Rule.HeadroomPercent / 100d:0.##})";
        if (!policy.Contains(expectedFormula, StringComparison.Ordinal))
        {
            failures.Add($"policy rule text does not state the implemented formula '{expectedFormula}'.");
        }

        var expectedRounding = $"Time thresholds round upward to the next {sampleSet.Rule.TimeRoundingMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms. Sampled heap-delta thresholds round upward to the next {sampleSet.Rule.MemoryRoundingBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes. The {sampleSet.Rule.HeadroomPercent}% headroom is applied to the worst value, not to a single favorable observation and not to the current run.";
        if (!policy.Contains(expectedRounding, StringComparison.Ordinal))
        {
            failures.Add("policy rounding or headroom statement does not match the committed rule.");
        }

        var expectedRationale = $"The {sampleSet.Rule.HeadroomPercent}% headroom is a fixed design allowance: the observed spread across the ten samples is below 10% in every dimension, so it absorbs ordinary host scheduling noise while still failing a meaningful regression.";
        if (!policy.Contains(expectedRationale, StringComparison.Ordinal))
        {
            failures.Add("policy does not include the fixed headroom design rationale.");
        }

        const string expectedEstimator = "The live `--performance` gate measures the current run at least five times in one process and takes the minimum emit, matching, and sampled-memory measurement for each dimension before comparing those minima with these derived thresholds. The minimum is a contention-resistant estimator: a transient scheduler interruption affects only an outlier attempt, while a real regression raises the minimum across attempts.";
        if (!policy.Contains(expectedEstimator, StringComparison.Ordinal))
        {
            failures.Add("policy does not explain the repeated-attempt minimum estimator.");
        }

        ValidateSampleTable(sampleSet, policy, failures);
        RequirePolicySummary(policy, $"- Median: emit `{statistics.EmitMedianMilliseconds:F2} ms`, matching `{statistics.MatchingMedianMilliseconds:F2} ms`, sampled heap delta `{statistics.MemoryMedianBytes:N0} bytes`.", "median", failures);
        RequirePolicySummary(policy, $"- Worst: emit `{statistics.EmitWorstMilliseconds:F2} ms`, matching `{statistics.MatchingWorstMilliseconds:F2} ms`, sampled heap delta `{statistics.MemoryWorstBytes:N0} bytes`.", "worst", failures);
        RequirePolicySummary(policy, $"- Derived thresholds: emit `{FormatMilliseconds(statistics.EmitThresholdMilliseconds)} ms`, matching `{FormatMilliseconds(statistics.MatchingThresholdMilliseconds)} ms`, sampled heap delta `{statistics.MemoryThresholdBytes:N0} bytes`.", "threshold", failures);

        RequirePolicySummary(policy, "Earlier pre-release revisions of this probe set wrote threshold values directly (margin 75%, then 125%). Those hand-set values are superseded: thresholds are now derived from the committed sample set by the fixed rule, and a threshold or margin change requires a committed sample-set or policy change with the recompute check passing.", "provenance", failures);
    }

    private static Dictionary<string, string> ParseMetadata(string metadataText, List<string> failures)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in metadataText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                failures.Add($"policy metadata entry '{part}' is malformed.");
                continue;
            }

            metadata[pair[0]] = pair[1];
        }

        return metadata;
    }

    private static void CompareMetadata(IReadOnlyDictionary<string, string> metadata, BenchmarkRule rule, List<string> failures)
    {
        CompareMetadataValue(metadata, "rule-id", rule.RuleId, failures);
        CompareMetadataValue(metadata, "headroom-percent", rule.HeadroomPercent.ToString(CultureInfo.InvariantCulture), failures);
        CompareMetadataValue(metadata, "time-rounding-ms", rule.TimeRoundingMilliseconds.ToString("0.###", CultureInfo.InvariantCulture), failures);
        CompareMetadataValue(metadata, "memory-rounding-bytes", rule.MemoryRoundingBytes.ToString(CultureInfo.InvariantCulture), failures);
    }

    private static void CompareMetadataValue(IReadOnlyDictionary<string, string> metadata, string key, string expected, List<string> failures)
    {
        if (!metadata.TryGetValue(key, out var actual))
        {
            failures.Add($"policy metadata is missing '{key}'.");
        }
        else if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            failures.Add($"policy metadata '{key}' is '{actual}', expected '{expected}'.");
        }
    }

    private static void ValidateSampleTable(SampleSetDocument sampleSet, string policy, List<string> failures)
    {
        var rows = Regex.Matches(policy, @"^\|\s*(?<sample>\d+)\s*\|\s*(?<emit>\d+(?:\.\d+)?)\s*\|\s*(?<matching>\d+(?:\.\d+)?)\s*\|\s*(?<memory>[\d,]+)\s*\|\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (rows.Count != sampleSet.Samples.Count)
        {
            failures.Add($"policy table contains {rows.Count} sample rows, but the committed sample set contains {sampleSet.Samples.Count}.");
            return;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var sample = sampleSet.Samples[index];
            if (!int.TryParse(row.Groups["sample"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleIndex) || sampleIndex != sample.Sample)
            {
                failures.Add($"policy table sample index at row {index + 1} does not match the executable sample set.");
            }

            if (!double.TryParse(row.Groups["emit"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var emit) || !NearlyEqual(emit, sample.EmitMilliseconds))
            {
                failures.Add($"policy table emit value at sample {index + 1} does not match the executable sample set.");
            }

            if (!double.TryParse(row.Groups["matching"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var matching) || !NearlyEqual(matching, sample.MatchingMilliseconds))
            {
                failures.Add($"policy table matching value at sample {index + 1} does not match the executable sample set.");
            }

            if (!long.TryParse(row.Groups["memory"].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out var memory) || memory != sample.MemoryBytes)
            {
                failures.Add($"policy table memory value at sample {index + 1} does not match the executable sample set.");
            }
        }
    }

    private static void RequirePolicySummary(string policy, string expected, string name, List<string> failures)
    {
        if (!policy.Contains(expected, StringComparison.Ordinal))
        {
            failures.Add($"policy {name} summary does not match recomputation: {expected}");
        }
    }

    private static SampleSetDocument LoadSampleSet(string path)
    {
        var sampleSet = JsonSerializer.Deserialize<SampleSetDocument>(File.ReadAllText(path), JsonOptions);
        return sampleSet ?? throw new InvalidOperationException("sample set is empty.");
    }

    private static string GetDefaultPolicyPath() => Path.Combine(AppContext.BaseDirectory, PolicyFileName);

    private static string GetDefaultSampleSetPath() => Path.Combine(AppContext.BaseDirectory, SampleSetFileName);

    private static BenchmarkStatistics Calculate(SampleSetDocument sampleSet)
    {
        var emit = sampleSet.Samples.Select(sample => sample.EmitMilliseconds).ToArray();
        var matching = sampleSet.Samples.Select(sample => sample.MatchingMilliseconds).ToArray();
        var memory = sampleSet.Samples.Select(sample => sample.MemoryBytes).ToArray();
        var emitWorst = emit.Max();
        var matchingWorst = matching.Max();
        var memoryWorst = memory.Max();
        var margin = 1 + sampleSet.Rule.HeadroomPercent / 100d;
        var emitThreshold = Math.Ceiling(emitWorst * margin / sampleSet.Rule.TimeRoundingMilliseconds) * sampleSet.Rule.TimeRoundingMilliseconds;
        var matchingThreshold = Math.Ceiling(matchingWorst * margin / sampleSet.Rule.TimeRoundingMilliseconds) * sampleSet.Rule.TimeRoundingMilliseconds;
        var memoryThreshold = checked((long)(Math.Ceiling(memoryWorst * margin / sampleSet.Rule.MemoryRoundingBytes) * sampleSet.Rule.MemoryRoundingBytes));
        return new BenchmarkStatistics(
            sampleSet.Samples.Count,
            sampleSet.Rule.HeadroomPercent / 100d,
            sampleSet.Rule.TimeRoundingMilliseconds,
            sampleSet.Rule.MemoryRoundingBytes,
            Median(emit),
            Median(matching),
            Median(memory),
            emitWorst,
            matchingWorst,
            memoryWorst,
            emitThreshold,
            matchingThreshold,
            memoryThreshold);
    }

    private static void PrintStatistics(BenchmarkStatistics statistics)
    {
        Console.WriteLine($"sample median: emit={statistics.EmitMedianMilliseconds:F2} ms; matching={statistics.MatchingMedianMilliseconds:F2} ms; sampled heap delta={statistics.MemoryMedianBytes:N0} bytes");
        Console.WriteLine($"sample worst: emit={statistics.EmitWorstMilliseconds:F2} ms; matching={statistics.MatchingWorstMilliseconds:F2} ms; sampled heap delta={statistics.MemoryWorstBytes:N0} bytes");
    }

    private static string FormatMilliseconds(double value) => value.ToString(value % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture);

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) <= 0.000001;

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

    private static long Median(IReadOnlyList<long> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

    private sealed class SampleSetDocument
    {
        public string Schema { get; set; } = string.Empty;
        public BenchmarkRule Rule { get; set; } = new();
        public List<BenchmarkSample> Samples { get; set; } = new();
    }

    private sealed class BenchmarkRule
    {
        public string RuleId { get; set; } = string.Empty;
        public int HeadroomPercent { get; set; }
        public double TimeRoundingMilliseconds { get; set; }
        public long MemoryRoundingBytes { get; set; }
    }

    private sealed class BenchmarkSample
    {
        public int Sample { get; set; }
        public double EmitMilliseconds { get; set; }
        public double MatchingMilliseconds { get; set; }
        public long MemoryBytes { get; set; }
    }

    internal sealed record BenchmarkStatistics(
        int SampleCount,
        double HeadroomFraction,
        double TimeRoundingMilliseconds,
        long MemoryRoundingBytes,
        double EmitMedianMilliseconds,
        double MatchingMedianMilliseconds,
        long MemoryMedianBytes,
        double EmitWorstMilliseconds,
        double MatchingWorstMilliseconds,
        long MemoryWorstBytes,
        double EmitThresholdMilliseconds,
        double MatchingThresholdMilliseconds,
        long MemoryThresholdBytes);
}
