using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogLeak.Probe.Runner;

internal static class BenchmarkEvidence
{
    private const string SupportedRuleId = "worst-normalized-sample-plus-headroom-v3";
    private const string SupportedSchema = "benchmark-sample-set-v3";
    private const int RequiredSampleCount = 10;
    private const string PolicyFileName = "BENCHMARK-POLICY.md";
    private const string SampleSetFileName = "BenchmarkSamples.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SampleSetDocument CommittedSampleSet = LoadSampleSet(GetDefaultSampleSetPath());

    internal static BenchmarkStatistics CommittedStatistics => Calculate(CommittedSampleSet);

    public static void PrintSampleSet()
    {
        var statistics = CommittedStatistics;
        Console.WriteLine("sample host: Windows x64; target=net8.0; runtime=.NET 8.0.31; workload=100000 events plus deterministic CPU reference");
        Console.WriteLine($"sample count: {CommittedSampleSet.Samples.Count}");
        foreach (var sample in CommittedSampleSet.Samples)
        {
            Console.WriteLine($"sample {sample.Sample}: reference={sample.ReferenceMilliseconds:F2} ms; emit={sample.EmitMilliseconds:F2} ms; matching={sample.MatchingMilliseconds:F2} ms; sampled heap delta={sample.MemoryBytes:N0} bytes");
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

        Console.WriteLine($"PASS benchmark threshold binding: live gate consumes normalized emit {FormatRatio(liveStatistics.EmitNormalizedThreshold)}, matching {FormatRatio(liveStatistics.MatchingNormalizedThreshold)}, memory {FormatRatio(liveStatistics.MemoryNormalizedThreshold)} ratio units from the committed sample-set recomputation.");
        Console.WriteLine($"PASS benchmark provenance: samples={sampleSet!.Samples.Count}; normalized median=emit {FormatRatio(statistics.EmitNormalizedMedian)}, matching {FormatRatio(statistics.MatchingNormalizedMedian)}, memory {FormatRatio(statistics.MemoryNormalizedMedian)}; normalized worst=emit {FormatRatio(statistics.EmitNormalizedWorst)}, matching {FormatRatio(statistics.MatchingNormalizedWorst)}, memory {FormatRatio(statistics.MemoryNormalizedWorst)}; normalized thresholds=emit {FormatRatio(statistics.EmitNormalizedThreshold)}, matching {FormatRatio(statistics.MatchingNormalizedThreshold)}, memory {FormatRatio(statistics.MemoryNormalizedThreshold)}; rule={sampleSet.Rule.RuleId}; headroom={sampleSet.Rule.HeadroomPercent}%.");
    }

    private static void ValidateLiveThresholdBinding(BenchmarkStatistics liveStatistics, BenchmarkStatistics recomputedStatistics, List<string> failures)
    {
        CompareThreshold("normalized emit", liveStatistics.EmitNormalizedThreshold, recomputedStatistics.EmitNormalizedThreshold, failures);
        CompareThreshold("normalized matching", liveStatistics.MatchingNormalizedThreshold, recomputedStatistics.MatchingNormalizedThreshold, failures);
        CompareThreshold("normalized memory", liveStatistics.MemoryNormalizedThreshold, recomputedStatistics.MemoryNormalizedThreshold, failures);
    }

    private static void CompareThreshold(string name, double consumed, double recomputed, List<string> failures)
    {
        if (!NearlyEqual(consumed, recomputed))
        {
            failures.Add($"live gate {name} threshold is {FormatRatio(consumed)}, but recomputation from the committed sample set is {FormatRatio(recomputed)}.");
        }
    }

    private static void ValidateSampleSet(SampleSetDocument sampleSet, List<string> failures)
    {
        if (!string.Equals(sampleSet.Schema, SupportedSchema, StringComparison.Ordinal))
        {
            failures.Add($"sample set schema '{sampleSet.Schema}' is unsupported; expected '{SupportedSchema}'.");
        }

        if (!string.Equals(sampleSet.Rule.RuleId, SupportedRuleId, StringComparison.Ordinal))
        {
            failures.Add($"policy rule '{sampleSet.Rule.RuleId}' is not implemented by the runner; expected '{SupportedRuleId}'.");
        }

        if (sampleSet.Rule.HeadroomPercent <= 0 || sampleSet.Rule.HeadroomPercent >= 100)
        {
            failures.Add($"headroom percent {sampleSet.Rule.HeadroomPercent} is outside the supported range.");
        }

        if (sampleSet.Rule.NormalizedRounding <= 0 || !double.IsFinite(sampleSet.Rule.NormalizedRounding))
        {
            failures.Add("normalized ratio rounding must be a positive finite value.");
        }

        if (sampleSet.Samples is null || sampleSet.Samples.Count != RequiredSampleCount)
        {
            failures.Add($"committed sample set has {sampleSet.Samples?.Count ?? 0} samples; exactly {RequiredSampleCount} are required.");
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

            if (!double.IsFinite(sample.ReferenceMilliseconds) || sample.ReferenceMilliseconds <= 0)
            {
                failures.Add($"sample {index + 1} has an invalid reference measurement.");
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

        var expectedFormula = $"CPU threshold = ceiling(worst normalized sample × {1 + sampleSet.Rule.HeadroomPercent / 100d:0.##})";
        if (!policy.Contains(expectedFormula, StringComparison.Ordinal))
        {
            failures.Add($"policy rule text does not state the implemented formula '{expectedFormula}'.");
        }

        var expectedRounding = $"Normalized CPU thresholds round upward to the next {sampleSet.Rule.NormalizedRounding.ToString("0.###", CultureInfo.InvariantCulture)} ratio unit. The {sampleSet.Rule.HeadroomPercent}% headroom is applied to the worst normalized value, not to a single favorable observation and not to the current run.";
        if (!policy.Contains(expectedRounding, StringComparison.Ordinal))
        {
            failures.Add("policy rounding or headroom statement does not match the committed rule.");
        }

        var expectedRationale = $"The {sampleSet.Rule.HeadroomPercent}% headroom is a fixed design allowance applied after reference normalization: it is not recalibrated from the current run, and it leaves a closed margin for a meaningful normalized regression.";
        if (!policy.Contains(expectedRationale, StringComparison.Ordinal))
        {
            failures.Add("policy does not include the fixed headroom design rationale.");
        }

        const string expectedEstimator = "The live `--performance` gate measures a deterministic CPU reference workload and the product workload in the same process on every attempt, takes an odd number of at least five attempts, and normalizes each CPU metric by that attempt's reference measurement before comparing the median normalized ratio with these derived thresholds. Raw absolute measurements, sampled heap observations, and every per-attempt normalized ratio remain visible as evidence; the closed normalized CPU criterion reduces host-contention sensitivity without making the gate advisory. A product/reference slowdown that affects both identically can normalize away, and only sustained regressions that move the median are detected.";
        if (!policy.Contains(expectedEstimator, StringComparison.Ordinal))
        {
            failures.Add("policy does not explain the repeated-attempt median estimator and its detection limits.");
        }

        ValidateSampleTable(sampleSet, policy, failures);
        RequirePolicySummary(policy, $"- Median raw: reference `{statistics.ReferenceMedianMilliseconds:F2} ms`, emit `{statistics.EmitMedianMilliseconds:F2} ms`, matching `{statistics.MatchingMedianMilliseconds:F2} ms`, sampled heap delta `{statistics.MemoryMedianBytes:N0} bytes`.", "raw median", failures);
        RequirePolicySummary(policy, $"- Median normalized: emit `{FormatRatio(statistics.EmitNormalizedMedian)}`, matching `{FormatRatio(statistics.MatchingNormalizedMedian)}`, sampled heap delta `{FormatRatio(statistics.MemoryNormalizedMedian)}` ratio units.", "normalized median", failures);
        RequirePolicySummary(policy, $"- Worst raw: reference `{statistics.ReferenceWorstMilliseconds:F2} ms`, emit `{statistics.EmitWorstMilliseconds:F2} ms`, matching `{statistics.MatchingWorstMilliseconds:F2} ms`, sampled heap delta `{statistics.MemoryWorstBytes:N0} bytes`.", "raw worst", failures);
        RequirePolicySummary(policy, $"- Worst normalized: emit `{FormatRatio(statistics.EmitNormalizedWorst)}`, matching `{FormatRatio(statistics.MatchingNormalizedWorst)}`, sampled heap delta `{FormatRatio(statistics.MemoryNormalizedWorst)}` ratio units.", "normalized worst", failures);
        RequirePolicySummary(policy, $"- Derived normalized CPU thresholds: emit `{FormatRatio(statistics.EmitNormalizedThreshold)}`, matching `{FormatRatio(statistics.MatchingNormalizedThreshold)}`. The committed sampled-heap reference is retained for provenance and diagnostic comparison only.", "threshold", failures);

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
        CompareMetadataValue(metadata, "normalized-rounding", rule.NormalizedRounding.ToString("0.###", CultureInfo.InvariantCulture), failures);
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
        var rows = Regex.Matches(policy, @"^\|\s*(?<sample>\d+)\s*\|\s*(?<reference>\d+(?:\.\d+)?)\s*\|\s*(?<emit>\d+(?:\.\d+)?)\s*\|\s*(?<matching>\d+(?:\.\d+)?)\s*\|\s*(?<memory>[\d,]+)\s*\|\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
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

            if (!double.TryParse(row.Groups["reference"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var reference) || !NearlyEqual(reference, sample.ReferenceMilliseconds))
            {
                failures.Add($"policy table reference value at sample {index + 1} does not match the executable sample set.");
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
        var reference = sampleSet.Samples.Select(sample => sample.ReferenceMilliseconds).ToArray();
        var emit = sampleSet.Samples.Select(sample => sample.EmitMilliseconds).ToArray();
        var matching = sampleSet.Samples.Select(sample => sample.MatchingMilliseconds).ToArray();
        var memory = sampleSet.Samples.Select(sample => sample.MemoryBytes).ToArray();
        var normalizedEmit = sampleSet.Samples.Select(sample => sample.EmitMilliseconds / sample.ReferenceMilliseconds).ToArray();
        var normalizedMatching = sampleSet.Samples.Select(sample => sample.MatchingMilliseconds / sample.ReferenceMilliseconds).ToArray();
        var normalizedMemory = sampleSet.Samples.Select(sample => sample.MemoryBytes / sample.ReferenceMilliseconds).ToArray();
        var referenceWorst = reference.Max();
        var emitWorst = emit.Max();
        var matchingWorst = matching.Max();
        var memoryWorst = memory.Max();
        var normalizedEmitWorst = normalizedEmit.Max();
        var normalizedMatchingWorst = normalizedMatching.Max();
        var normalizedMemoryWorst = normalizedMemory.Max();
        var margin = 1 + sampleSet.Rule.HeadroomPercent / 100d;
        var emitThreshold = Math.Ceiling(normalizedEmitWorst * margin / sampleSet.Rule.NormalizedRounding) * sampleSet.Rule.NormalizedRounding;
        var matchingThreshold = Math.Ceiling(normalizedMatchingWorst * margin / sampleSet.Rule.NormalizedRounding) * sampleSet.Rule.NormalizedRounding;
        var memoryThreshold = Math.Ceiling(normalizedMemoryWorst * margin / sampleSet.Rule.NormalizedRounding) * sampleSet.Rule.NormalizedRounding;
        return new BenchmarkStatistics(
            sampleSet.Samples.Count,
            sampleSet.Rule.HeadroomPercent / 100d,
            sampleSet.Rule.NormalizedRounding,
            Median(reference),
            Median(emit),
            Median(matching),
            Median(memory),
            Median(normalizedEmit),
            Median(normalizedMatching),
            Median(normalizedMemory),
            referenceWorst,
            emitWorst,
            matchingWorst,
            memoryWorst,
            normalizedEmitWorst,
            normalizedMatchingWorst,
            normalizedMemoryWorst,
            emitThreshold,
            matchingThreshold,
            memoryThreshold);
    }

    private static void PrintStatistics(BenchmarkStatistics statistics)
    {
        Console.WriteLine($"sample median raw: reference={statistics.ReferenceMedianMilliseconds:F2} ms; emit={statistics.EmitMedianMilliseconds:F2} ms; matching={statistics.MatchingMedianMilliseconds:F2} ms; sampled heap delta={statistics.MemoryMedianBytes:N0} bytes");
        Console.WriteLine($"sample median normalized: emit={FormatRatio(statistics.EmitNormalizedMedian)}; matching={FormatRatio(statistics.MatchingNormalizedMedian)}; sampled heap delta={FormatRatio(statistics.MemoryNormalizedMedian)} ratio units");
        Console.WriteLine($"sample worst raw: reference={statistics.ReferenceWorstMilliseconds:F2} ms; emit={statistics.EmitWorstMilliseconds:F2} ms; matching={statistics.MatchingWorstMilliseconds:F2} ms; sampled heap delta={statistics.MemoryWorstBytes:N0} bytes");
        Console.WriteLine($"sample worst normalized: emit={FormatRatio(statistics.EmitNormalizedWorst)}; matching={FormatRatio(statistics.MatchingNormalizedWorst)}; sampled heap delta={FormatRatio(statistics.MemoryNormalizedWorst)} ratio units");
    }

    private static string FormatMilliseconds(double value) => value.ToString(value % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture);

    private static string FormatRatio(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

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
        public double NormalizedRounding { get; set; }
    }

    private sealed class BenchmarkSample
    {
        public int Sample { get; set; }
        public double ReferenceMilliseconds { get; set; }
        public double EmitMilliseconds { get; set; }
        public double MatchingMilliseconds { get; set; }
        public long MemoryBytes { get; set; }
    }

    internal sealed record BenchmarkStatistics(
        int SampleCount,
        double HeadroomFraction,
        double NormalizedRounding,
        double ReferenceMedianMilliseconds,
        double EmitMedianMilliseconds,
        double MatchingMedianMilliseconds,
        long MemoryMedianBytes,
        double EmitNormalizedMedian,
        double MatchingNormalizedMedian,
        double MemoryNormalizedMedian,
        double ReferenceWorstMilliseconds,
        double EmitWorstMilliseconds,
        double MatchingWorstMilliseconds,
        long MemoryWorstBytes,
        double EmitNormalizedWorst,
        double MatchingNormalizedWorst,
        double MemoryNormalizedWorst,
        double EmitNormalizedThreshold,
        double MatchingNormalizedThreshold,
        double MemoryNormalizedThreshold);
}
