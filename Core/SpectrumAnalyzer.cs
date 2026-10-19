using NAudio.Wave;

namespace Lychee.Core;

/// <summary>Decodes interleaved PCM audio and converts it into logarithmic spectrum bands.</summary>
public sealed class SpectrumAnalyzer
{
    public const int FftSize = 4096;
    public const int HopSize = FftSize / 2;
    private const double MinimumFrequency = 40;
    private const double MaximumFrequency = 16_000;
    private const double DisplayFloorDb = -65;
    private const double DisplayCeilingDb = -10;
    private const double AveragePowerWeight = 0.8;
    private const double PeakPowerWeight = 0.2;

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _blockAlign;
    private readonly WaveFormatEncoding _encoding;
    private readonly int _bitsPerSample;
    private readonly double[][] _history;
    private readonly double[] _window = new double[FftSize];
    private readonly double[] _windowed = new double[FftSize];
    private readonly double[] _imaginary = new double[FftSize];
    private readonly double[] _powerByBin = new double[FftSize / 2 + 1];
    private readonly double[] _cosine = new double[FftSize / 2];
    private readonly double[] _sine = new double[FftSize / 2];
    private readonly int[] _bitReversal = new int[FftSize];
    private readonly BandRange[] _bands = new BandRange[SpectrumFrame.BandCount];
    private readonly double _windowNormalization;
    private readonly double _effectiveMaximumFrequency;
    private int _writeIndex;
    private long _totalFrames;

    public SpectrumAnalyzer(WaveFormat waveFormat)
    {
        ArgumentNullException.ThrowIfNull(waveFormat);

        var standardFormat = waveFormat is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : waveFormat;

        _sampleRate = standardFormat.SampleRate;
        _channels = standardFormat.Channels;
        _blockAlign = standardFormat.BlockAlign;
        _encoding = standardFormat.Encoding;
        _bitsPerSample = standardFormat.BitsPerSample;
        if (_sampleRate <= 0 || _channels <= 0 || _blockAlign <= 0)
            throw new NotSupportedException("The audio endpoint reported an invalid wave format.");
        if (_encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat))
            throw new NotSupportedException($"Unsupported audio encoding: {_encoding}.");
        if (_encoding == WaveFormatEncoding.IeeeFloat && _bitsPerSample is not (32 or 64))
            throw new NotSupportedException($"Unsupported IEEE float depth: {_bitsPerSample} bits.");
        if (_encoding == WaveFormatEncoding.Pcm && _bitsPerSample is not (8 or 16 or 24 or 32))
            throw new NotSupportedException($"Unsupported PCM depth: {_bitsPerSample} bits.");

        _history = Enumerable.Range(0, _channels).Select(_ => new double[FftSize]).ToArray();
        for (var i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
        }
        _windowNormalization = 2 / _window.Sum();

        for (var i = 0; i < FftSize / 2; i++)
        {
            var angle = 2 * Math.PI * i / FftSize;
            _cosine[i] = Math.Cos(angle);
            _sine[i] = Math.Sin(angle);
        }
        for (var i = 0; i < FftSize; i++)
        {
            _bitReversal[i] = ReverseBits(i, 12);
        }

        _effectiveMaximumFrequency = Math.Min(MaximumFrequency, _sampleRate / 2.0);
        BuildBands();
    }

    /// <summary>Appends one captured packet; returns frames produced after a complete FFT window.</summary>
    public int Process(ReadOnlySpan<byte> packet, DateTimeOffset capturedAt, Action<SpectrumFrame> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        var framesProduced = 0;
        var completeBytes = packet.Length - packet.Length % _blockAlign;

        for (var offset = 0; offset < completeBytes; offset += _blockAlign)
        {
            for (var channel = 0; channel < _channels; channel++)
            {
                var sampleOffset = offset + channel * (_bitsPerSample / 8);
                _history[channel][_writeIndex] = ReadSample(packet.Slice(sampleOffset, _bitsPerSample / 8));
            }

            _writeIndex = (_writeIndex + 1) % FftSize;
            _totalFrames++;

            if (_totalFrames >= FftSize && (_totalFrames - FftSize) % HopSize == 0)
            {
                publish(Analyze(capturedAt));
                framesProduced++;
            }
        }

        return framesProduced;
    }

    private SpectrumFrame Analyze(DateTimeOffset capturedAt)
    {
        Array.Clear(_powerByBin);

        for (var channel = 0; channel < _channels; channel++)
        {
            for (var i = 0; i < FftSize; i++)
            {
                _windowed[i] = _history[channel][(_writeIndex + i) % FftSize] * _window[i];
                _imaginary[i] = 0;
            }

            Transform();
            for (var bin = 1; bin <= FftSize / 2; bin++)
            {
                var amplitude = Math.Sqrt(_windowed[bin] * _windowed[bin] + _imaginary[bin] * _imaginary[bin])
                    * _windowNormalization;
                _powerByBin[bin] += amplitude * amplitude / _channels;
            }
        }

        Span<float> values = stackalloc float[SpectrumFrame.BandCount];
        for (var bandIndex = 0; bandIndex < _bands.Length; bandIndex++)
        {
            var band = _bands[bandIndex];
            double averagePower;
            double peakPower;
            if (band.FirstBin <= band.LastBin)
            {
                double sumPower = 0;
                peakPower = 0;
                var binCount = 0;
                for (var bin = band.FirstBin; bin <= band.LastBin; bin++)
                {
                    var binPower = _powerByBin[bin];
                    sumPower += binPower;
                    peakPower = Math.Max(peakPower, binPower);
                    binCount++;
                }

                averagePower = sumPower / binCount;
            }
            else
            {
                averagePower = peakPower = InterpolatePower(band.CenterBin);
            }

            var displayPower = AveragePowerWeight * averagePower + PeakPowerWeight * peakPower;
            var decibels = displayPower <= 1e-20 ? DisplayFloorDb : 10 * Math.Log10(displayPower);
            values[bandIndex] = (float)Math.Clamp(
                (decibels - DisplayFloorDb) / (DisplayCeilingDb - DisplayFloorDb), 0, 1);
        }

        return new SpectrumFrame(capturedAt, values);
    }

    private void BuildBands()
    {
        if (_effectiveMaximumFrequency <= MinimumFrequency)
        {
            Array.Fill(_bands, new BandRange(1, 0, 1));
            return;
        }

        var minimumLog = Math.Log(MinimumFrequency);
        var logSpan = Math.Log(_effectiveMaximumFrequency) - minimumLog;
        var binResolution = (double)_sampleRate / FftSize;

        for (var bandIndex = 0; bandIndex < _bands.Length; bandIndex++)
        {
            var low = Math.Exp(minimumLog + logSpan * bandIndex / _bands.Length);
            var high = Math.Exp(minimumLog + logSpan * (bandIndex + 1) / _bands.Length);
            var center = Math.Sqrt(low * high);
            var firstBin = Math.Max(1, (int)Math.Ceiling(low / binResolution));
            var lastBin = Math.Min(FftSize / 2, (int)Math.Floor(high / binResolution));
            _bands[bandIndex] = new BandRange(firstBin, lastBin, center / binResolution);
        }
    }

    private double InterpolatePower(double position)
    {
        var lower = Math.Clamp((int)Math.Floor(position), 1, FftSize / 2);
        var upper = Math.Min(lower + 1, FftSize / 2);
        var fraction = Math.Clamp(position - lower, 0, 1);
        return _powerByBin[lower] * (1 - fraction) + _powerByBin[upper] * fraction;
    }

    private double ReadSample(ReadOnlySpan<byte> sample)
    {
        if (_encoding == WaveFormatEncoding.IeeeFloat)
        {
            var value = _bitsPerSample == 32
                ? BitConverter.Int32BitsToSingle(BitConverter.ToInt32(sample))
                : BitConverter.Int64BitsToDouble(BitConverter.ToInt64(sample));
            return double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
        }

        return _bitsPerSample switch
        {
            8 => (sample[0] - 128) / 128.0,
            16 => BitConverter.ToInt16(sample) / 32768.0,
            24 => ReadInt24(sample) / 8_388_608.0,
            32 => BitConverter.ToInt32(sample) / 2_147_483_648.0,
            _ => 0
        };
    }

    private static int ReadInt24(ReadOnlySpan<byte> sample)
    {
        var value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
        return (value & 0x800000) == 0 ? value : value | unchecked((int)0xFF000000);
    }

    private void Transform()
    {
        for (var i = 0; i < FftSize; i++)
        {
            var reversed = _bitReversal[i];
            if (reversed <= i) continue;
            (_windowed[i], _windowed[reversed]) = (_windowed[reversed], _windowed[i]);
            (_imaginary[i], _imaginary[reversed]) = (_imaginary[reversed], _imaginary[i]);
        }

        for (var length = 2; length <= FftSize; length <<= 1)
        {
            var half = length >> 1;
            var twiddleStep = FftSize / length;
            for (var start = 0; start < FftSize; start += length)
            {
                for (var offset = 0; offset < half; offset++)
                {
                    var twiddle = offset * twiddleStep;
                    var even = start + offset;
                    var odd = even + half;
                    var oddReal = _windowed[odd] * _cosine[twiddle] + _imaginary[odd] * _sine[twiddle];
                    var oddImaginary = _imaginary[odd] * _cosine[twiddle] - _windowed[odd] * _sine[twiddle];
                    var evenReal = _windowed[even];
                    var evenImaginary = _imaginary[even];
                    _windowed[even] = evenReal + oddReal;
                    _imaginary[even] = evenImaginary + oddImaginary;
                    _windowed[odd] = evenReal - oddReal;
                    _imaginary[odd] = evenImaginary - oddImaginary;
                }
            }
        }
    }

    private static int ReverseBits(int value, int bitCount)
    {
        var reversed = 0;
        for (var i = 0; i < bitCount; i++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }
        return reversed;
    }

    private readonly record struct BandRange(int FirstBin, int LastBin, double CenterBin);
}
