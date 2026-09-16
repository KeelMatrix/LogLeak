namespace LogLeak.Probe.Runner;

internal static class BenchmarkEvidence
{
    // These samples were measured on Windows x64 with the net8.0 target and .NET 8.0.31.
    // The policy is fixed: threshold = ceiling(worst sample x 1.25), rounded upward.
    private static readonly double[] EmitSamplesMilliseconds =
    {
        87.53, 84.91, 83.14, 83.94, 82.41, 84.10, 82.03, 84.43, 86.88, 80.88
    };

    private static readonly double[] MatchingSamplesMilliseconds =
    {
        44.08, 44.22, 41.66, 41.66, 41.78, 42.49, 41.72, 42.67, 43.35, 40.38
    };

    private static readonly long[] MemorySamplesBytes =
    {
        8_310_816, 8_310_816, 8_310_816, 8_318_904, 8_310_760,
        8_310_760, 8_310_760, 8_310_760, 8_310_816, 8_310_760
    };

    public const double SafetyMargin = 0.25;
    public const double TimeRoundingMilliseconds = 1;
    public const long MemoryRoundingBytes = 100_000;

    public static int SampleCount => EmitSamplesMilliseconds.Length;

    public static double EmitMedianMilliseconds => Median(EmitSamplesMilliseconds);

    public static double MatchingMedianMilliseconds => Median(MatchingSamplesMilliseconds);

    public static long MemoryMedianBytes => Median(MemorySamplesBytes);

    public static double EmitWorstMilliseconds => EmitSamplesMilliseconds.Max();

    public static double MatchingWorstMilliseconds => MatchingSamplesMilliseconds.Max();

    public static long MemoryWorstBytes => MemorySamplesBytes.Max();

    public static double EmitThresholdMilliseconds => RoundTime(EmitWorstMilliseconds);

    public static double MatchingThresholdMilliseconds => RoundTime(MatchingWorstMilliseconds);

    public static long MemoryThresholdBytes => checked((long)(Math.Ceiling(MemoryWorstBytes * (1 + SafetyMargin) / MemoryRoundingBytes) * MemoryRoundingBytes));

    public static void PrintSampleSet()
    {
        Console.WriteLine("sample host: Windows x64; target=net8.0; runtime=.NET 8.0.31; workload=100000 events");
        Console.WriteLine($"sample count: {SampleCount}");
        for (var index = 0; index < SampleCount; index++)
        {
            Console.WriteLine($"sample {index + 1}: emit={EmitSamplesMilliseconds[index]:F2} ms; matching={MatchingSamplesMilliseconds[index]:F2} ms; sampled heap delta={MemorySamplesBytes[index]:N0} bytes");
        }

        Console.WriteLine($"sample median: emit={EmitMedianMilliseconds:F2} ms; matching={MatchingMedianMilliseconds:F2} ms; sampled heap delta={MemoryMedianBytes:N0} bytes");
        Console.WriteLine($"sample worst: emit={EmitWorstMilliseconds:F2} ms; matching={MatchingWorstMilliseconds:F2} ms; sampled heap delta={MemoryWorstBytes:N0} bytes");
    }

    private static double RoundTime(double worst)
    {
        return Math.Ceiling(worst * (1 + SafetyMargin) / TimeRoundingMilliseconds) * TimeRoundingMilliseconds;
    }

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
}
