using System.Buffers.Binary;
using Lychee.Core;
using NAudio.Wave;

const int sampleRate = 48_000;
const int frameCount = 8_192;

var mutableBands = new float[SpectrumFrame.BandCount];
mutableBands[0] = 0.5f;
var immutableFrame = new SpectrumFrame(DateTimeOffset.UtcNow, mutableBands);
mutableBands[0] = 0;
Assert(immutableFrame.Bands[0] == 0.5f, "SpectrumFrame retained a mutable input buffer.");
Assert(!SpectrumFrame.Silence(DateTimeOffset.UtcNow).IsValid, "A silence frame was marked as valid audio.");
Assert(AudioMeterPollingPolicy.GetInterval(spectrumActive: true) == TimeSpan.FromMilliseconds(33),
    "Expanded Audio Meter polling must remain at 30 FPS.");
Assert(AudioMeterPollingPolicy.GetInterval(spectrumActive: false) == TimeSpan.FromMilliseconds(250),
    "Collapsed Audio Meter polling must use the low-frequency interval.");

foreach (var frequency in new[] { 100d, 1_000d, 8_000d })
{
    var frame = AnalyzeFloatTone(frequency, oppositePhase: false);
    var expectedBand = GetBandIndex(frequency, Math.Min(16_000, sampleRate / 2d));
    var loudestBand = IndexOfMaximum(frame.Bands);
    Assert(Math.Abs(loudestBand - expectedBand) <= 1,
        $"{frequency:0} Hz peaked in band {loudestBand}; expected band near {expectedBand}.");
    Assert(frame.Bands[expectedBand] > 0.35f,
        $"{frequency:0} Hz produced too little energy in expected band {expectedBand} (value {frame.Bands[expectedBand]:0.000}; bands: {string.Join(",", frame.Bands.Select(value => value.ToString("0.000")))}).");
}

foreach (var frequency in new[] { 100d, 1_000d, 8_000d })
{
    var frame = AnalyzeFloatTone(frequency, oppositePhase: false, amplitude: 0.05);
    var expectedBand = GetBandIndex(frequency, Math.Min(16_000, sampleRate / 2d));
    Assert(Math.Abs(IndexOfMaximum(frame.Bands) - expectedBand) <= 1,
        $"Equal-amplitude {frequency:0} Hz tone did not peak in its expected band.");
    Assert(frame.Bands[expectedBand] >= 0.55f,
        $"Equal-amplitude {frequency:0} Hz tone response was too low for the 55 dB display range ({frame.Bands[expectedBand]:0.000}).");
}

var quietTone = AnalyzeFloatTone(1_000, oppositePhase: false, amplitude: 0.05);
var tonePlus3Db = AnalyzeFloatTone(1_000, oppositePhase: false, amplitude: 0.05 * Math.Sqrt(2));
var toneBand = GetBandIndex(1_000, Math.Min(16_000, sampleRate / 2d));
var observed3DbRise = tonePlus3Db.Bands[toneBand] - quietTone.Bands[toneBand];
Assert(Math.Abs(observed3DbRise - (3f / 55f)) <= 0.01f,
    $"A 3 dB power increase should raise the unclipped bar by about {3f / 55f:0.000} across the 55 dB range; observed {observed3DbRise:0.000}.");

var whiteNoiseMeans = AnalyzeWhiteNoiseMeans();
var lowBandMean = whiteNoiseMeans.Take(4).Average();
var highBandMean = whiteNoiseMeans.Skip(12).Average();
var whiteNoiseBias = highBandMean - lowBandMean;
Console.WriteLine($"White-noise mean normalized height: low 4 = {lowBandMean:0.000}, high 4 = {highBandMean:0.000}, delta = {whiteNoiseBias:0.000}.");
Assert(whiteNoiseBias <= 0.12,
    $"White noise showed excessive high-band dominance ({whiteNoiseBias:0.000}; allowed <= 0.120).");

var invertedStereo = AnalyzeFloatTone(1_000, oppositePhase: true);
var invertedBand = GetBandIndex(1_000, Math.Min(16_000, sampleRate / 2d));
Assert(invertedStereo.Bands[invertedBand] > 0.35f,
    "Equal and opposite stereo channels cancelled instead of retaining their power.");

var float64Frame = AnalyzeFloat64Tone(1_000);
Assert(float64Frame.Bands[invertedBand] > 0.35f,
    "64-bit IEEE float audio did not produce energy in the expected frequency band.");

var extensiblePcmFrame = AnalyzeExtensibleTone(1_000, bitsPerSample: 16);
Assert(extensiblePcmFrame.Bands[invertedBand] > 0.25f,
    "16-bit WAVEFORMATEXTENSIBLE PCM did not produce energy in the expected frequency band.");
var extensibleFloatFrame = AnalyzeExtensibleTone(1_000, bitsPerSample: 32);
Assert(extensibleFloatFrame.Bands[invertedBand] > 0.35f,
    "32-bit WAVEFORMATEXTENSIBLE float did not produce energy in the expected frequency band.");

foreach (var bitDepth in new[] { 8, 16, 24, 32 })
{
    var pcmFrame = AnalyzePcmTone(1_000, bitDepth);
    var expectedBand = GetBandIndex(1_000, Math.Min(16_000, sampleRate / 2d));
    Assert(pcmFrame.Bands[expectedBand] > 0.25f,
        $"{bitDepth}-bit PCM did not produce energy in the expected frequency band.");
}

var lowRateFrame = AnalyzeFloatTone(3_500, oppositePhase: false, rate: 8_000);
var lowRateBand = GetBandIndex(3_500, 4_000);
Assert(lowRateFrame.Bands[lowRateBand] > 0.25f,
    "Frequency bands did not adapt to a sample rate below 16 kHz.");

Console.WriteLine("Audio spectrum smoke checks passed: low/mid/high tones, equal-amplitude tone response, 3 dB mapping, deterministic white noise, inverted stereo, float32/float64, PCM 8/16/24/32-bit, WAVEFORMATEXTENSIBLE PCM/float, 8 kHz sample rate, and active/paused meter intervals.");

static SpectrumFrame AnalyzeFloatTone(double frequency, bool oppositePhase, int rate = sampleRate, double amplitude = 0.25)
{
    var format = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
    var analyzer = new SpectrumAnalyzer(format);
    var data = new byte[frameCount * format.BlockAlign];
    for (var sample = 0; sample < frameCount; sample++)
    {
        var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * sample / rate));
        var offset = sample * format.BlockAlign;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
        var right = oppositePhase ? -value : value;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 4, 4), BitConverter.SingleToInt32Bits(right));
    }

    return ProcessOne(analyzer, data);
}

static double[] AnalyzeWhiteNoiseMeans()
{
    const int durationSeconds = 5;
    const double rmsAmplitude = 0.05;
    var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    var analyzer = new SpectrumAnalyzer(format);
    var data = new byte[durationSeconds * sampleRate * format.BlockAlign];
    var random = new Random(0x51A7);
    var uniformScale = rmsAmplitude * Math.Sqrt(3);
    for (var sample = 0; sample < durationSeconds * sampleRate; sample++)
    {
        var offset = sample * format.BlockAlign;
        var left = (float)((random.NextDouble() * 2 - 1) * uniformScale);
        var right = (float)((random.NextDouble() * 2 - 1) * uniformScale);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(left));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 4, 4), BitConverter.SingleToInt32Bits(right));
    }

    var sums = new double[SpectrumFrame.BandCount];
    var frames = 0;
    analyzer.Process(data, DateTimeOffset.UtcNow, frame =>
    {
        for (var band = 0; band < SpectrumFrame.BandCount; band++)
            sums[band] += frame.Bands[band];
        frames++;
    });
    Assert(frames >= 100, $"Expected at least 100 white-noise frames from five seconds; received {frames}.");
    return sums.Select(sum => sum / frames).ToArray();
}

static SpectrumFrame AnalyzePcmTone(double frequency, int bitDepth)
{
    var format = new WaveFormat(sampleRate, bitDepth, 1);
    var analyzer = new SpectrumAnalyzer(format);
    var bytesPerSample = bitDepth / 8;
    var data = new byte[frameCount * format.BlockAlign];
    for (var sample = 0; sample < frameCount; sample++)
    {
        var normalized = 0.25 * Math.Sin(2 * Math.PI * frequency * sample / sampleRate);
        var offset = sample * format.BlockAlign;
        switch (bitDepth)
        {
            case 8:
                data[offset] = (byte)Math.Clamp((int)Math.Round(normalized * 128 + 128), 0, 255);
                break;
            case 16:
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, 2), (short)Math.Round(normalized * 32767));
                break;
            case 24:
            {
                var value = (int)Math.Round(normalized * 8_388_607);
                data[offset] = (byte)value;
                data[offset + 1] = (byte)(value >> 8);
                data[offset + 2] = (byte)(value >> 16);
                break;
            }
            case 32:
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), (int)Math.Round(normalized * int.MaxValue));
                break;
        }
    }

    Assert(format.BlockAlign == bytesPerSample, $"Unexpected {bitDepth}-bit PCM block alignment.");
    return ProcessOne(analyzer, data);
}

static SpectrumFrame AnalyzeFloat64Tone(double frequency)
{
    const int channels = 2;
    var format = WaveFormat.CreateCustomFormat(
        WaveFormatEncoding.IeeeFloat,
        sampleRate,
        channels,
        sampleRate * channels * 8,
        channels * 8,
        64);
    var analyzer = new SpectrumAnalyzer(format);
    var data = new byte[frameCount * format.BlockAlign];
    for (var sample = 0; sample < frameCount; sample++)
    {
        var value = 0.25 * Math.Sin(2 * Math.PI * frequency * sample / sampleRate);
        var offset = sample * format.BlockAlign;
        var encoded = BitConverter.DoubleToInt64Bits(value);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset, 8), encoded);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset + 8, 8), encoded);
    }

    return ProcessOne(analyzer, data);
}

static SpectrumFrame AnalyzeExtensibleTone(double frequency, int bitsPerSample)
{
    const int channels = 2;
    var format = new WaveFormatExtensible(sampleRate, bitsPerSample, channels);
    var standardFormat = format.ToStandardWaveFormat();
    Assert(standardFormat.Encoding == (bitsPerSample == 32 ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm),
        $"Unexpected WAVEFORMATEXTENSIBLE subformat for {bitsPerSample}-bit samples: {standardFormat.Encoding}.");

    var analyzer = new SpectrumAnalyzer(format);
    var data = new byte[frameCount * format.BlockAlign];
    for (var sample = 0; sample < frameCount; sample++)
    {
        var normalized = 0.25 * Math.Sin(2 * Math.PI * frequency * sample / sampleRate);
        var offset = sample * format.BlockAlign;
        if (bitsPerSample == 32)
        {
            var encoded = BitConverter.SingleToInt32Bits((float)normalized);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), encoded);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 4, 4), encoded);
        }
        else
        {
            var encoded = (short)Math.Round(normalized * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, 2), encoded);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 2, 2), encoded);
        }
    }

    return ProcessOne(analyzer, data);
}

static SpectrumFrame ProcessOne(SpectrumAnalyzer analyzer, byte[] data)
{
    SpectrumFrame? latest = null;
    var capturedAt = DateTimeOffset.UtcNow;
    var splitAt = data.Length / 2;
    var framesProduced = analyzer.Process(data.AsSpan(0, splitAt), capturedAt, frame => latest = frame);
    framesProduced += analyzer.Process(data.AsSpan(splitAt), capturedAt.AddMilliseconds(1), frame => latest = frame);
    Assert(framesProduced == 3, $"Expected three overlapping FFT frames from {frameCount} samples; got {framesProduced}.");
    return latest ?? throw new InvalidOperationException("The analyzer did not produce a complete FFT frame.");
}

static int GetBandIndex(double frequency, double upperFrequency)
{
    var normalized = Math.Log(frequency / 40) / Math.Log(upperFrequency / 40);
    return Math.Clamp((int)Math.Floor(normalized * SpectrumFrame.BandCount), 0, SpectrumFrame.BandCount - 1);
}

static int IndexOfMaximum(IReadOnlyList<float> values)
{
    var maximumIndex = 0;
    for (var index = 1; index < values.Count; index++)
        if (values[index] > values[maximumIndex]) maximumIndex = index;
    return maximumIndex;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
