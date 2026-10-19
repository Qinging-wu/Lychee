using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lychee.Controls;
using Lychee.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--live", StringComparison.OrdinalIgnoreCase))
            return RenderLiveCapture(args);

        const int width = 260;
        const int height = 80;
        var bands = new float[SpectrumFrame.BandCount];
        for (var index = 0; index < bands.Length; index++)
            bands[index] = 0.24f + (index % 4) * 0.19f;

        var provider = new PreviewProvider(new float[SpectrumFrame.BandCount]);
        var view = CreateView(provider, width, height);
        Thread.Sleep(140);
        provider.SetBands(bands);
        Thread.Sleep(120);
        view.InvalidateVisual();
        view.UpdateLayout();
        var bitmap = Render(view, width, height);
        AssertVisibleSpectrum(bitmap, width, height);

        var outputPath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(AppContext.BaseDirectory, "spectrum-preview.png");
        SavePng(bitmap, outputPath);
        Console.WriteLine($"Rendered 16-band AudioSpectrumView preview: {outputPath}");

        VerifyStaleFrameDecay();
        Console.WriteLine("Stale-frame decay check passed: bars and peak indicators naturally return toward zero without new audio frames.");
        VerifyResponseTimingAndParticles();
        Console.WriteLine("Response checks passed: 20 ms attack, 100 ms release, 250 ms peak hold, particle threshold, cooldown, and 64-particle cap.");
        VerifyDpiRender(120, 215);
        VerifyDpiRender(144, 215);
        Console.WriteLine("DPI render checks passed at 125% and 150% output scale with 215 logical pixels of content width: all 16 bars remain visible within the scaled canvas.");
        return 0;
    }

    private static void VerifyStaleFrameDecay()
    {
        var bands = Enumerable.Repeat(0.62f, SpectrumFrame.BandCount).ToArray();
        var provider = new PreviewProvider(new SpectrumFrame(DateTimeOffset.UtcNow, bands));
        var view = CreateView(provider, 260, 80);
        RenderTargetBitmap bitmap = Render(view, 260, 80);

        for (var frame = 0; frame < 4; frame++)
        {
            Thread.Sleep(33);
            view.InvalidateVisual();
            view.UpdateLayout();
            bitmap = Render(view, 260, 80);
        }

        var activePixels = CountColorfulPixels(bitmap, 260, 80);
        for (var frame = 0; frame < 50; frame++)
        {
            Thread.Sleep(33);
            view.InvalidateVisual();
            view.UpdateLayout();
            bitmap = Render(view, 260, 80);
        }

        var decayedPixels = CountColorfulPixels(bitmap, 260, 80);
        if (activePixels < 500 || decayedPixels >= activePixels * 0.2)
            throw new InvalidOperationException(
                $"Expected stale spectrum bars and peaks to decay substantially; colorful pixels changed from {activePixels} to {decayedPixels}.");
    }

    private static void VerifyResponseTimingAndParticles()
    {
        var liftedLowBand = AudioSpectrumView.MapDisplayLevel(0, 0.5);
        var contouredMidBand = AudioSpectrumView.MapDisplayLevel(8, 0.5);
        var zeroLevel = AudioSpectrumView.MapDisplayLevel(8, 0);
        if (liftedLowBand <= 0.5 || Math.Abs(liftedLowBand - contouredMidBand) > 1e-9 || zeroLevel != 0)
            throw new InvalidOperationException(
                $"Display lift should raise ordinary bars consistently without frequency contouring and preserve zero (low {liftedLowBand:0.000}, mid {contouredMidBand:0.000}, zero {zeroLevel:0.000}).");

        var bands = new float[SpectrumFrame.BandCount];
        var provider = new PreviewProvider(bands);
        var view = CreateView(provider, 260, 80);
        _ = Render(view, 260, 80);

        bands = new float[SpectrumFrame.BandCount];
        bands[0] = 0.8f;
        provider.SetBands(bands);
        Thread.Sleep(50);
        _ = Render(view, 260, 80);
        var initialLevel = view.GetSmoothedLevel(0);
        var initialPeak = view.GetPeakLevel(0);
        if (initialLevel < 0.8 * 0.8 || initialPeak < initialLevel)
            throw new InvalidOperationException(
                $"The 20 ms attack did not reach 80% of its target within 50 ms (level {initialLevel:0.000}).");

        provider.SetBands(new float[SpectrumFrame.BandCount]);
        Thread.Sleep(120);
        _ = Render(view, 260, 80);
        var releasedLevel = view.GetSmoothedLevel(0);
        if (releasedLevel > initialLevel * 0.4)
            throw new InvalidOperationException(
                $"The 100 ms release remained above 40% after 120 ms (level {releasedLevel:0.000}, initial {initialLevel:0.000}).");
        if (view.GetPeakLevel(0) < initialPeak - 0.001)
            throw new InvalidOperationException("The peak marker did not hold during its 250 ms hold interval.");

        Thread.Sleep(160);
        _ = Render(view, 260, 80);
        if (view.GetPeakLevel(0) >= initialPeak - 0.015)
            throw new InvalidOperationException("The peak marker did not begin descending after its hold interval.");

        var particleProvider = new PreviewProvider(new float[SpectrumFrame.BandCount]);
        var particleView = CreateView(particleProvider, 260, 80);
        _ = Render(particleView, 260, 80);
        var belowFloor = new float[SpectrumFrame.BandCount];
        belowFloor[0] = 0.23f;
        particleProvider.SetBands(belowFloor);
        Thread.Sleep(110);
        _ = Render(particleView, 260, 80);
        if (particleView.ParticleCount != 0)
            throw new InvalidOperationException("Particle emission occurred below the 0.24 minimum level.");

        var firstRise = new float[SpectrumFrame.BandCount];
        firstRise[0] = 0.33f;
        particleProvider.SetBands(firstRise);
        Thread.Sleep(20);
        _ = Render(particleView, 260, 80);
        if (particleView.ParticleCount != 1)
            throw new InvalidOperationException("A 0.10 rise above the minimum level did not emit exactly one particle.");

        var insideCooldown = new float[SpectrumFrame.BandCount];
        insideCooldown[0] = 0.43f;
        particleProvider.SetBands(insideCooldown);
        Thread.Sleep(20);
        _ = Render(particleView, 260, 80);
        if (particleView.ParticleCount != 1)
            throw new InvalidOperationException("Particle cooldown did not suppress a second rapid emission.");

        var cappedProvider = new PreviewProvider(new float[SpectrumFrame.BandCount]);
        var cappedView = CreateView(cappedProvider, 260, 80);
        _ = Render(cappedView, 260, 80);
        Thread.Sleep(110);
        var maximumObservedParticles = 0;
        for (var burst = 0; burst < 4; burst++)
        {
            cappedProvider.SetBands(Enumerable.Repeat(0.5f, SpectrumFrame.BandCount).ToArray());
            Thread.Sleep(2);
            _ = Render(cappedView, 260, 80);
            maximumObservedParticles = Math.Max(maximumObservedParticles, cappedView.ParticleCount);
            if (cappedView.ParticleCount > 64)
                throw new InvalidOperationException("The particle system exceeded its 64-particle cap.");
            if (burst == 3) break;

            cappedProvider.SetBands(new float[SpectrumFrame.BandCount]);
            Thread.Sleep(2);
            _ = Render(cappedView, 260, 80);
            Thread.Sleep(100);
        }
        if (maximumObservedParticles < 48)
            throw new InvalidOperationException(
                $"Expected repeated bursts to exercise the particle cap; maximum observed was {maximumObservedParticles}.");
    }

    private static void VerifyDpiRender(double dpi, int logicalWidth)
    {
        const int logicalHeight = 80;
        var scale = dpi / 96;
        var width = (int)Math.Ceiling(logicalWidth * scale);
        var height = (int)Math.Ceiling(logicalHeight * scale);
        var bands = Enumerable.Repeat(0.58f, SpectrumFrame.BandCount).ToArray();
        var view = CreateView(new PreviewProvider(bands), logicalWidth, logicalHeight);
        RenderTargetBitmap bitmap = RenderAtDpi(view, width, height, dpi);
        for (var frame = 0; frame < 5; frame++)
        {
            Thread.Sleep(33);
            view.InvalidateVisual();
            view.UpdateLayout();
            bitmap = RenderAtDpi(view, width, height, dpi);
        }

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        var logicalUsableWidth = logicalWidth - 10 - 3 * (SpectrumFrame.BandCount - 1);
        var logicalBarWidth = logicalUsableWidth / (double)SpectrumFrame.BandCount;
        for (var band = 0; band < SpectrumFrame.BandCount; band++)
        {
            var centerX = (int)Math.Round((5 + band * (logicalBarWidth + 3) + logicalBarWidth / 2) * scale);
            var coloredPixels = 0;
            for (var y = (int)(10 * scale); y < height - (int)(7 * scale); y++)
            {
                var offset = y * stride + centerX * 4;
                if (IsColorful(pixels[offset], pixels[offset + 1], pixels[offset + 2]))
                    coloredPixels++;
            }

            if (coloredPixels < 3)
                throw new InvalidOperationException($"Band {band} was clipped or not rendered at {dpi / 96:P0} DPI and {logicalWidth} logical pixels wide.");
        }

        for (var y = 0; y < height; y++)
        {
            var left = y * stride;
            var right = y * stride + (width - 1) * 4;
            if (IsColorful(pixels[left + 2], pixels[left + 1], pixels[left]) ||
                IsColorful(pixels[right + 2], pixels[right + 1], pixels[right]))
                throw new InvalidOperationException($"Spectrum content overflowed the canvas edge at {dpi / 96:P0} DPI and {logicalWidth} logical pixels wide.");
        }
    }

    private static int RenderLiveCapture(string[] args)
    {
        var duration = args.Length > 1 && int.TryParse(args[1], out var requestedSeconds)
            ? Math.Clamp(requestedSeconds, 3, 60)
            : 15;
        var outputPath = args.Length > 2
            ? Path.GetFullPath(args[2])
            : Path.Combine(AppContext.BaseDirectory, "spectrum-live.png");
        var source = new WasapiAudioCaptureSource();
        try
        {
            var provider = new LiveCaptureProvider(source);
            source.StatusChanged += status => Console.WriteLine($"Capture status: {status}");
            source.StatusChanged += provider.SetStatus;
            source.SetActive(true);
            var view = CreateView(provider, 260, 80);
            var lastCapturedAt = DateTimeOffset.MinValue;
            var snapshots = 0;
            var lastRenderAt = DateTimeOffset.MinValue;
            var endsAt = DateTimeOffset.UtcNow.AddSeconds(duration);
            Console.WriteLine($"Listening to the default Render/Console output for {duration} seconds. Play music throughout the capture.");
            while (DateTimeOffset.UtcNow < endsAt)
            {
                var frame = source.LatestFrame;
                if (frame.IsValid && frame.CapturedAt > lastCapturedAt)
                {
                    snapshots++;
                    lastCapturedAt = frame.CapturedAt;
                }

                var now = DateTimeOffset.UtcNow;
                if ((now - lastRenderAt).TotalMilliseconds >= 33)
                {
                    view.InvalidateVisual();
                    view.UpdateLayout();
                    _ = Render(view, 260, 80);
                    lastRenderAt = now;
                }
                Thread.Sleep(20);
            }

            var latest = source.LatestFrame;
            Console.WriteLine($"Live spectrum snapshots: {snapshots}");
            if (!latest.IsValid || (DateTimeOffset.UtcNow - latest.CapturedAt).TotalSeconds > 0.28)
            {
                Console.Error.WriteLine("No fresh live spectrum was available. Keep music playing and retry.");
                return 2;
            }

            Console.WriteLine($"Captured 16 bands: {string.Join(",", latest.Bands.Select(value => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))}");
            view.InvalidateVisual();
            view.UpdateLayout();
            var bitmap = Render(view, 260, 80);
            SavePng(bitmap, outputPath);
            Console.WriteLine($"Rendered a real-capture AudioSpectrumView frame (not raw audio): {outputPath}");
            return 0;
        }
        finally
        {
            source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static AudioSpectrumView CreateView(IAudioSpectrumProvider provider, int width, int height)
    {
        var view = new AudioSpectrumView
        {
            DataContext = provider,
            Width = width,
            Height = height
        };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        return view;
    }

    private static RenderTargetBitmap Render(AudioSpectrumView view, int width, int height)
    {
        view.InvalidateVisual();
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        return bitmap;
    }

    private static RenderTargetBitmap RenderAtDpi(AudioSpectrumView view, int width, int height, double dpi)
    {
        view.InvalidateVisual();
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(view);
        return bitmap;
    }

    private static void SavePng(RenderTargetBitmap bitmap, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using (var stream = File.Create(outputPath))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }
    }

    private static void AssertVisibleSpectrum(RenderTargetBitmap bitmap, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        var saturatedPixels = CountColorfulPixels(pixels);

        if (saturatedPixels < 350)
            throw new InvalidOperationException($"Expected colorful spectrum bars, found only {saturatedPixels} saturated pixels.");

        var usableWidth = width - 10 - 3 * (SpectrumFrame.BandCount - 1);
        var barWidth = usableWidth / (double)SpectrumFrame.BandCount;
        for (var band = 0; band < SpectrumFrame.BandCount; band++)
        {
            var centerX = (int)Math.Round(5 + band * (barWidth + 3) + barWidth / 2);
            var coloredColumnPixels = 0;
            for (var y = 10; y < height - 7; y++)
            {
                var offset = y * stride + centerX * 4;
                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                if (Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) > 45)
                    coloredColumnPixels++;
            }

            if (coloredColumnPixels < 3)
                throw new InvalidOperationException($"Spectrum band {band} did not render a visible colored bar.");
        }
    }

    private static int CountColorfulPixels(RenderTargetBitmap bitmap, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return CountColorfulPixels(pixels);
    }

    private static int CountColorfulPixels(byte[] pixels)
    {
        var count = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            if (IsColorful(red, green, blue))
                count++;
        }
        return count;
    }

    private static bool IsColorful(byte red, byte green, byte blue)
        => Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) > 45;

    private sealed class PreviewProvider : IAudioSpectrumProvider
    {
        private float[]? _bands;
        private SpectrumFrame? _fixedFrame;

        public PreviewProvider(float[] initialBands) => _bands = initialBands;
        public PreviewProvider(SpectrumFrame frame) => _fixedFrame = frame;
        public SpectrumFrame? LatestSpectrum => _fixedFrame ?? new SpectrumFrame(DateTimeOffset.UtcNow, _bands!);
        public string SpectrumStatus => "Listening";

        public void SetBands(float[] bands) => _bands = bands;
    }

    private sealed class LiveCaptureProvider(WasapiAudioCaptureSource source) : IAudioSpectrumProvider
    {
        private string _status = "Waiting for audio...";

        public SpectrumFrame? LatestSpectrum => source.LatestFrame;
        public string SpectrumStatus => Volatile.Read(ref _status);

        public void SetStatus(string status) => Volatile.Write(ref _status, status);
    }
}
