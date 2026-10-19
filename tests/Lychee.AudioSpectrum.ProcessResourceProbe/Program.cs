using System.Diagnostics;

var durationSeconds = args.Length > 0 && int.TryParse(args[0], out var requestedDuration)
    ? Math.Clamp(requestedDuration, 5, 7_200)
    : 1_800;
var process = FindLycheeProcess(args.Skip(1).FirstOrDefault());
if (process is null) return 2;

Console.WriteLine($"Monitoring {process.ProcessName} PID {process.Id} for {TimeSpan.FromSeconds(durationSeconds):hh\\:mm\\:ss}.");
Console.WriteLine($"Executable: {GetExecutablePath(process) ?? "<unavailable>"}");
Console.WriteLine($"Process started: {process.StartTime:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine("Press E when Audio Meter is expanded, C when collapsed. Measurements are grouped by the most recently marked state; Q ends early.");

var groups = new Dictionary<string, MetricsAccumulator>(StringComparer.Ordinal)
{
    ["expanded"] = new MetricsAccumulator(),
    ["collapsed"] = new MetricsAccumulator(),
    ["unmarked"] = new MetricsAccumulator()
};
var state = "unmarked";
var stopwatch = Stopwatch.StartNew();
var lastSampleAt = stopwatch.Elapsed;
var lastCpu = process.TotalProcessorTime;
var nextSampleAt = TimeSpan.Zero;
var lastSummaryAt = TimeSpan.Zero;

while (stopwatch.Elapsed.TotalSeconds < durationSeconds)
{
    await Task.Delay(100);
    if (!Console.IsInputRedirected && Console.KeyAvailable)
    {
        var key = char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar);
        var nextState = key switch { 'E' => "expanded", 'C' => "collapsed", 'Q' => "quit", _ => state };
        if (nextState == "quit") break;
        if (nextState != state)
        {
            state = nextState;
            lastSampleAt = stopwatch.Elapsed;
            lastCpu = process.TotalProcessorTime;
            nextSampleAt = lastSampleAt + TimeSpan.FromSeconds(10);
            Console.WriteLine($"[{stopwatch.Elapsed:hh\\:mm\\:ss}] State marked: {state}");
        }
    }

    if (stopwatch.Elapsed < nextSampleAt) continue;

    try
    {
        process.Refresh();
        var now = stopwatch.Elapsed;
        var cpu = process.TotalProcessorTime;
        var interval = now - lastSampleAt;
        var cpuPercent = interval.TotalMilliseconds <= 0
            ? 0
            : (cpu - lastCpu).TotalMilliseconds / (interval.TotalMilliseconds * Environment.ProcessorCount) * 100;
        var metrics = new Sample(cpuPercent, process.WorkingSet64, process.PrivateMemorySize64);
        groups[state].Add(metrics);
        if (now - lastSummaryAt >= TimeSpan.FromMinutes(1))
        {
            Console.WriteLine($"[{now:hh\\:mm\\:ss}] {state}: CPU {cpuPercent:0.0}% | WS {ToMiB(metrics.WorkingSetBytes):0.0} MiB | Private {ToMiB(metrics.PrivateBytes):0.0} MiB");
            lastSummaryAt = now;
        }
        lastSampleAt = now;
        lastCpu = cpu;
        nextSampleAt = now + TimeSpan.FromSeconds(10);
    }
    catch (InvalidOperationException)
    {
        Console.Error.WriteLine("Lychee process exited before the probe ended.");
        break;
    }
}

Console.WriteLine("\nResource summary by marked panel state:");
foreach (var (name, metrics) in groups)
{
    if (metrics.Count == 0) continue;
    Console.WriteLine($"{name}: {metrics.Count} samples; CPU avg/max {metrics.AverageCpu:0.0}/{metrics.MaximumCpu:0.0}%; WS {ToMiB(metrics.MinimumWorkingSet):0.0}-{ToMiB(metrics.MaximumWorkingSet):0.0} MiB; Private {ToMiB(metrics.MinimumPrivate):0.0}-{ToMiB(metrics.MaximumPrivate):0.0} MiB");
}
return 0;

static Process? FindLycheeProcess(string? pidText)
{
    if (pidText is not null)
    {
        if (int.TryParse(pidText, out var pid))
        {
            try { return Process.GetProcessById(pid); }
            catch (ArgumentException) { }
        }
        Console.Error.WriteLine("The supplied PID was invalid or no longer running.");
        return null;
    }

    var processes = Process.GetProcessesByName("Lychee");
    if (processes.Length == 1) return processes[0];
    foreach (var candidate in processes) candidate.Dispose();
    Console.Error.WriteLine(processes.Length == 0
        ? "No Lychee process found. Start Lychee first or supply its PID."
        : "More than one Lychee process found; pass the intended PID as the second argument.");
    return null;
}

static double ToMiB(long bytes) => bytes / (1024d * 1024d);

static string? GetExecutablePath(Process process)
{
    try { return process.MainModule?.FileName; }
    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
    {
        return null;
    }
}

readonly record struct Sample(double CpuPercent, long WorkingSetBytes, long PrivateBytes);

sealed class MetricsAccumulator
{
    public int Count { get; private set; }
    public double AverageCpu { get; private set; }
    public double MaximumCpu { get; private set; }
    public long MinimumWorkingSet { get; private set; } = long.MaxValue;
    public long MaximumWorkingSet { get; private set; }
    public long MinimumPrivate { get; private set; } = long.MaxValue;
    public long MaximumPrivate { get; private set; }

    public void Add(Sample sample)
    {
        Count++;
        AverageCpu += (sample.CpuPercent - AverageCpu) / Count;
        MaximumCpu = Math.Max(MaximumCpu, sample.CpuPercent);
        MinimumWorkingSet = Math.Min(MinimumWorkingSet, sample.WorkingSetBytes);
        MaximumWorkingSet = Math.Max(MaximumWorkingSet, sample.WorkingSetBytes);
        MinimumPrivate = Math.Min(MinimumPrivate, sample.PrivateBytes);
        MaximumPrivate = Math.Max(MaximumPrivate, sample.PrivateBytes);
    }
}
