using Lychee.Core;
using System.Globalization;
using System.Reflection;
using NAudio.CoreAudioApi;

var duration = args.Length > 0 && int.TryParse(args[0], out var requestedSeconds)
    ? Math.Clamp(requestedSeconds, 3, 60)
    : 15;
var simulateDefaultDeviceChange = args.Contains("--simulate-device-change", StringComparer.OrdinalIgnoreCase);
var source = new WasapiAudioCaptureSource();
source.StatusChanged += status => Console.WriteLine($"Status: {status}");
var deviceChanges = 0;
long simulatedChangeAtUtcTicks = 0;
source.DefaultDeviceChanged += () =>
{
    Interlocked.Increment(ref deviceChanges);
    Console.WriteLine("Default Render/Console device changed.");
};
source.SetActive(true);

Task? simulatedChangeTask = null;
if (simulateDefaultDeviceChange)
{
    simulatedChangeTask = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, duration / 3)));
        Interlocked.Exchange(ref simulatedChangeAtUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        Console.WriteLine("Simulating the WASAPI default Render/Console notification.");
        var notificationField = typeof(WasapiAudioCaptureSource).GetField("_notificationClient", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(WasapiAudioCaptureSource).FullName, "_notificationClient");
        var notificationClient = notificationField.GetValue(source)
            ?? throw new InvalidOperationException("The WASAPI notification client was not initialized.");
        var callback = notificationClient.GetType().GetMethod("OnDefaultDeviceChanged", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(notificationClient.GetType().FullName, "OnDefaultDeviceChanged");
        callback.Invoke(notificationClient, [DataFlow.Render, Role.Console, "simulated-default-endpoint"]);
    });
}

var frames = 0;
var changedFrames = 0;
var framesAfterSimulatedChange = 0;
var lastCapturedAt = DateTimeOffset.MinValue;
var longestFrameGap = TimeSpan.Zero;
var observedFrameAgeTotalMs = 0d;
var minimumObservedFrameAgeMs = double.PositiveInfinity;
var maximumObservedFrameAgeMs = 0d;
var bandMinimums = Enumerable.Repeat(float.PositiveInfinity, SpectrumFrame.BandCount).ToArray();
var bandMaximums = Enumerable.Repeat(float.NegativeInfinity, SpectrumFrame.BandCount).ToArray();
float[]? previousBands = null;
var endAt = DateTimeOffset.UtcNow.AddSeconds(duration);
Console.WriteLine($"Listening to the default Render/Console endpoint for {duration} seconds. Play audio now for a live signal check.");
while (DateTimeOffset.UtcNow < endAt)
{
    var frame = source.LatestFrame;
    if (frame.IsValid && frame.CapturedAt > lastCapturedAt)
    {
        var observedAgeMs = Math.Max(0, (DateTimeOffset.UtcNow - frame.CapturedAt).TotalMilliseconds);
        observedFrameAgeTotalMs += observedAgeMs;
        minimumObservedFrameAgeMs = Math.Min(minimumObservedFrameAgeMs, observedAgeMs);
        maximumObservedFrameAgeMs = Math.Max(maximumObservedFrameAgeMs, observedAgeMs);
        frames++;
        if (lastCapturedAt != DateTimeOffset.MinValue)
            longestFrameGap = TimeSpan.FromTicks(Math.Max(longestFrameGap.Ticks, (frame.CapturedAt - lastCapturedAt).Ticks));
        var currentBands = frame.Bands.ToArray();
        var simulatedChangeTicks = Interlocked.Read(ref simulatedChangeAtUtcTicks);
        if (simulatedChangeTicks > 0 && frame.CapturedAt.UtcTicks > simulatedChangeTicks)
            framesAfterSimulatedChange++;
        if (previousBands is not null && currentBands.Where((value, index) => Math.Abs(value - previousBands[index]) >= 0.01f).Any())
            changedFrames++;
        for (var band = 0; band < SpectrumFrame.BandCount; band++)
        {
            bandMinimums[band] = Math.Min(bandMinimums[band], currentBands[band]);
            bandMaximums[band] = Math.Max(bandMaximums[band], currentBands[band]);
        }
        previousBands = currentBands;
        lastCapturedAt = frame.CapturedAt;
    }
    await Task.Delay(20);
}

if (simulatedChangeTask is not null)
    await simulatedChangeTask;

var latest = source.LatestFrame;
Console.WriteLine($"Frames observed: {frames}");
Console.WriteLine($"Default device changes observed: {Volatile.Read(ref deviceChanges)}");
if (simulateDefaultDeviceChange)
    Console.WriteLine($"Frames observed after simulated device notification: {framesAfterSimulatedChange}");
Console.WriteLine($"Frames with changed bands: {changedFrames}");
Console.WriteLine($"Longest frame gap: {longestFrameGap.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
Console.WriteLine(frames > 0
    ? $"Observed frame age (capture-to-poll, includes up to 20 ms polling delay): min/avg/max {minimumObservedFrameAgeMs.ToString("0", CultureInfo.InvariantCulture)}/{(observedFrameAgeTotalMs / frames).ToString("0", CultureInfo.InvariantCulture)}/{maximumObservedFrameAgeMs.ToString("0", CultureInfo.InvariantCulture)} ms"
    : "Observed frame age: n/a (no live frames)");
Console.WriteLine($"Latest frame age: {(latest.IsValid ? (DateTimeOffset.UtcNow - latest.CapturedAt).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) : "n/a")} ms");
Console.WriteLine($"Latest spectrum: {(latest.IsValid ? string.Join(",", latest.Bands.Select(value => value.ToString("0.00", CultureInfo.InvariantCulture))) : "no audio frame")}");
if (frames > 0)
    Console.WriteLine($"Band ranges: {string.Join(" ", Enumerable.Range(0, SpectrumFrame.BandCount).Select(band => $"{band}:{bandMinimums[band].ToString("0.00", CultureInfo.InvariantCulture)}-{bandMaximums[band].ToString("0.00", CultureInfo.InvariantCulture)}"))}");

source.SetActive(false);
await WaitForSilenceAsync(source);
if (frames > 0 && source.LatestFrame.IsValid)
    throw new InvalidOperationException("A live spectrum snapshot remained valid after capture deactivation.");
source.SetActive(true);
await Task.Delay(150);
source.SetActive(false);
await Task.Delay(150);
source.SetActive(true);
await Task.Delay(250);

var firstDispose = source.DisposeAsync().AsTask();
var secondDispose = source.DisposeAsync().AsTask();
await Task.WhenAll(firstDispose, secondDispose);
Console.WriteLine("Repeated deactivate/reactivate and concurrent DisposeAsync checks completed.");
if (frames == 0)
    Console.WriteLine("No live audio frames were received; verify audio was playing and an active output device was available.");
if (simulateDefaultDeviceChange && Volatile.Read(ref deviceChanges) == 0)
    throw new InvalidOperationException("The simulated WASAPI device notification did not reach the source event.");
if (simulateDefaultDeviceChange && frames > 0 && framesAfterSimulatedChange == 0)
    throw new InvalidOperationException("Live capture did not resume after the simulated WASAPI device notification.");

static async Task WaitForSilenceAsync(WasapiAudioCaptureSource source)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while (source.LatestFrame.IsValid && DateTimeOffset.UtcNow < deadline)
        await Task.Delay(20);
    if (source.LatestFrame.IsValid)
        throw new TimeoutException("The capture source did not clear its latest frame after deactivation.");
}
