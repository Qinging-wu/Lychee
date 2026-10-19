using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Lychee.Core;

namespace Lychee.Controls;

/// <summary>Compact 16-band spectrum with peak holds and short-lived particles.</summary>
public sealed class AudioSpectrumView : FrameworkElement
{
    private const int BandCount = 16;
    private const int MaximumParticles = 64;
    private const double BarGap = 3;
    private const double HorizontalPadding = 5;
    private const double VerticalPadding = 7;
    private const double AttackTimeSeconds = 0.02;
    private const double ReleaseTimeSeconds = 0.10;
    private const double PeakHoldSeconds = 0.25;
    private const double MinimumParticleLevel = 0.24;
    private const double ParticleRiseThreshold = 0.06;
    private const double StaleFrameSeconds = 0.28;
    private const double DisplayLiftExponent = 0.82;

    private static readonly Color[] SpectrumColors =
    [
        Color.FromRgb(255, 75, 82),
        Color.FromRgb(255, 132, 56),
        Color.FromRgb(255, 203, 70),
        Color.FromRgb(177, 222, 74),
        Color.FromRgb(70, 214, 151),
        Color.FromRgb(63, 202, 220),
        Color.FromRgb(79, 135, 255),
        Color.FromRgb(173, 92, 255)
    ];

    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromArgb(80, 7, 9, 18)));
    private static readonly Pen BaselinePen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(45, 210, 220, 255)), 1));
    private static readonly Brush StatusBrush = Freeze(new SolidColorBrush(Color.FromArgb(155, 205, 211, 229)));
    private static readonly Brush[] BarBrushes = CreateBarBrushes();
    private static readonly Brush[] GlowBrushes = CreateGlowBrushes();
    private static readonly Brush[] PeakBrushes = CreatePeakBrushes();
    private static readonly Brush[,] ParticleBrushes = CreateParticleBrushes();

    private readonly double[] _levels = new double[BandCount];
    private readonly double[] _peaks = new double[BandCount];
    private readonly double[] _peakUpdatedAt = new double[BandCount];
    private readonly double[] _lastParticleAt = new double[BandCount];
    private readonly double[] _previousTargets = new double[BandCount];
    private readonly List<Particle> _particles = new(MaximumParticles);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IAudioSpectrumProvider? _provider;
    private SpectrumFrame? _lastFrame;
    private FormattedText? _formattedStatus;
    private string? _formattedStatusText;
    private double _formattedStatusPixelsPerDip;
    private DateTimeOffset _lastFrameAt;
    private double _lastRenderSeconds;
    private bool _renderingSubscribed;

    internal int ParticleCount => _particles.Count;
    internal double GetSmoothedLevel(int band) => _levels[band];
    internal double GetPeakLevel(int band) => _peaks[band];

    internal static double MapDisplayLevel(int band, double normalizedLevel)
    {
        var level = Math.Clamp(normalizedLevel, 0, 1);
        return Math.Pow(level, DisplayLiftExponent);
    }

    public AudioSpectrumView()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        DataContextChanged += (_, _) => UpdateModuleReference();
        Loaded += (_, _) => UpdateRenderingSubscription();
        Unloaded += (_, _) => SetRenderingSubscription(false);
        IsVisibleChanged += (_, _) => UpdateRenderingSubscription();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRoundedRectangle(BackgroundBrush, null, bounds, 5, 5);
        if (ActualWidth <= HorizontalPadding * 2 || ActualHeight <= VerticalPadding * 2) return;

        var now = _clock.Elapsed.TotalSeconds;
        UpdateLevels(now);
        UpdateParticles(now);

        var usableWidth = ActualWidth - HorizontalPadding * 2 - BarGap * (BandCount - 1);
        var barWidth = usableWidth / BandCount;
        if (barWidth <= 0) return;

        var baselineY = ActualHeight - VerticalPadding;
        var barAreaHeight = Math.Max(1, ActualHeight - VerticalPadding * 2 - 5);
        drawingContext.DrawLine(BaselinePen, new Point(HorizontalPadding, baselineY),
            new Point(ActualWidth - HorizontalPadding, baselineY));

        for (var band = 0; band < BandCount; band++)
        {
            var x = HorizontalPadding + band * (barWidth + BarGap);
            var height = Math.Max(2, _levels[band] * barAreaHeight);
            var y = baselineY - height;
            var barRect = new Rect(x, y, barWidth, height);
            var glowRect = new Rect(x - 1, y - 1, barWidth + 2, height + 1);
            var radius = Math.Min(2, barWidth / 2);

            drawingContext.DrawRoundedRectangle(GlowBrushes[band], null, glowRect, radius + 1, radius + 1);
            drawingContext.DrawRoundedRectangle(BarBrushes[band], null, barRect, radius, radius);

            if (_peaks[band] > 0.02)
            {
                var peakY = baselineY - Math.Max(2, _peaks[band] * barAreaHeight);
                drawingContext.DrawRoundedRectangle(PeakBrushes[band], null,
                    new Rect(x + barWidth * 0.18, peakY - 1, barWidth * 0.64, 2.5), 1.2, 1.2);
            }
        }

        DrawParticles(drawingContext, now);
        DrawStatus(drawingContext);
    }

    private void UpdateLevels(double now)
    {
        var frame = _provider?.LatestSpectrum;
        if (frame is not null && frame.IsValid && frame.CapturedAt > _lastFrameAt)
        {
            _lastFrame = frame;
            _lastFrameAt = frame.CapturedAt;
            SpawnParticles(frame, now);
        }

        var elapsed = Math.Clamp(now - _lastRenderSeconds, 0, 0.1);
        _lastRenderSeconds = now;
        if (elapsed <= 0) return;

        var frameIsFresh = _lastFrame is { IsValid: true } latest &&
                           (DateTimeOffset.UtcNow - latest.CapturedAt).TotalSeconds <= StaleFrameSeconds;

        for (var band = 0; band < BandCount; band++)
        {
            var target = frameIsFresh ? MapDisplayLevel(band, _lastFrame!.Bands[band]) : 0;
            var timeConstant = target > _levels[band] ? AttackTimeSeconds : ReleaseTimeSeconds;
            var blend = 1 - Math.Exp(-elapsed / timeConstant);
            _levels[band] += (target - _levels[band]) * blend;

            if (_levels[band] >= _peaks[band])
            {
                _peaks[band] = _levels[band];
                _peakUpdatedAt[band] = now;
            }
            else if (now - _peakUpdatedAt[band] >= PeakHoldSeconds)
            {
                _peaks[band] = Math.Max(_levels[band], _peaks[band] - elapsed * 0.42);
            }
        }
    }

    private void SpawnParticles(SpectrumFrame frame, double now)
    {
        if ((DateTimeOffset.UtcNow - frame.CapturedAt).TotalSeconds > StaleFrameSeconds) return;

        var random = Random.Shared;
        for (var band = 0; band < BandCount && _particles.Count < MaximumParticles; band++)
        {
            var level = Math.Clamp(frame.Bands[band], 0, 1);
            var increase = level - _previousTargets[band];
            _previousTargets[band] = level;
            if (level < MinimumParticleLevel || increase < ParticleRiseThreshold || now - _lastParticleAt[band] < 0.09) continue;

            var usableWidth = ActualWidth - HorizontalPadding * 2 - BarGap * (BandCount - 1);
            var barWidth = usableWidth / BandCount;
            var x = HorizontalPadding + band * (barWidth + BarGap) + barWidth / 2;
            var baselineY = ActualHeight - VerticalPadding;
            var barAreaHeight = Math.Max(1, ActualHeight - VerticalPadding * 2 - 5);
            var y = baselineY - Math.Max(2, level * barAreaHeight);
            var lifetime = 0.3 + random.NextDouble() * 0.3;
            _particles.Add(new Particle(
                x + (random.NextDouble() - 0.5) * barWidth * 0.5,
                y,
                (random.NextDouble() - 0.5) * 18,
                -(18 + random.NextDouble() * 28),
                0.9 + random.NextDouble() * 1.2,
                now,
                lifetime,
                band));
            _lastParticleAt[band] = now;
        }
    }

    private void UpdateParticles(double now)
    {
        for (var index = _particles.Count - 1; index >= 0; index--)
        {
            var particle = _particles[index];
            var elapsed = now - particle.LastUpdatedAt;
            particle.X += particle.VelocityX * elapsed;
            particle.Y += particle.VelocityY * elapsed;
            particle.LastUpdatedAt = now;
            if (now - particle.BornAt >= particle.Lifetime || particle.Y < -4)
                _particles.RemoveAt(index);
            else
                _particles[index] = particle;
        }
    }

    private void DrawParticles(DrawingContext drawingContext, double now)
    {
        foreach (var particle in _particles)
        {
            var remaining = 1 - (now - particle.BornAt) / particle.Lifetime;
            var opacityIndex = Math.Clamp((int)Math.Round(remaining * 15), 0, 15);
            var radius = particle.Radius * Math.Clamp(remaining, 0.2, 1);
            drawingContext.DrawEllipse(ParticleBrushes[particle.ColorIndex, opacityIndex], null,
                new Point(particle.X, particle.Y), radius, radius);
        }
    }

    private void DrawStatus(DrawingContext drawingContext)
    {
        var frameIsFresh = _lastFrame is { IsValid: true } latest &&
                           (DateTimeOffset.UtcNow - latest.CapturedAt).TotalSeconds <= StaleFrameSeconds;
        if (frameIsFresh) return;

        var status = _provider?.SpectrumStatus;
        var text = status switch
        {
            "Spectrum unavailable" => "Spectrum unavailable",
            "Spectrum paused" or "Spectrum inactive" => "",
            _ => "Waiting for audio..."
        };
        if (text.Length == 0) return;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_formattedStatus is null || _formattedStatusText != text || _formattedStatusPixelsPerDip != pixelsPerDip)
        {
            _formattedStatusText = text;
            _formattedStatusPixelsPerDip = pixelsPerDip;
            _formattedStatus = new FormattedText(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 9, StatusBrush, pixelsPerDip);
        }
        drawingContext.DrawText(_formattedStatus, new Point(HorizontalPadding + 1, 2));
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            SetRenderingSubscription(false);
            return;
        }

        var now = _clock.Elapsed.TotalSeconds;
        if (now - _lastRenderSeconds < 1.0 / 30.0) return;
        InvalidateVisual();
    }

    private void UpdateModuleReference() => _provider = DataContext as IAudioSpectrumProvider;

    private void UpdateRenderingSubscription() => SetRenderingSubscription(IsLoaded && IsVisible);

    private void SetRenderingSubscription(bool subscribe)
    {
        if (_renderingSubscribed == subscribe) return;
        _renderingSubscribed = subscribe;
        if (subscribe)
            CompositionTarget.Rendering += OnRendering;
        else
            CompositionTarget.Rendering -= OnRendering;
    }

    private static Brush[] CreateBarBrushes()
    {
        var brushes = new Brush[BandCount];
        for (var band = 0; band < BandCount; band++)
        {
            var color = ColorForBand(band);
            var dark = Color.FromRgb((byte)(color.R * 0.38), (byte)(color.G * 0.38), (byte)(color.B * 0.38));
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 1),
                EndPoint = new Point(0, 0)
            };
            brush.GradientStops.Add(new GradientStop(dark, 0));
            brush.GradientStops.Add(new GradientStop(color, 0.72));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(
                (byte)Math.Min(255, color.R + 25),
                (byte)Math.Min(255, color.G + 25),
                (byte)Math.Min(255, color.B + 25)), 1));
            brushes[band] = Freeze(brush);
        }
        return brushes;
    }

    private static Brush[] CreateGlowBrushes()
    {
        var brushes = new Brush[BandCount];
        for (var band = 0; band < BandCount; band++)
        {
            var color = ColorForBand(band);
            brushes[band] = Freeze(new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B)));
        }
        return brushes;
    }

    private static Brush[] CreatePeakBrushes()
    {
        var brushes = new Brush[BandCount];
        for (var band = 0; band < BandCount; band++)
        {
            var color = ColorForBand(band);
            brushes[band] = Freeze(new SolidColorBrush(Color.FromRgb(
                (byte)Math.Min(255, color.R + 35),
                (byte)Math.Min(255, color.G + 35),
                (byte)Math.Min(255, color.B + 35))));
        }
        return brushes;
    }

    private static Brush[,] CreateParticleBrushes()
    {
        var brushes = new Brush[BandCount, 16];
        for (var band = 0; band < BandCount; band++)
        {
            var color = ColorForBand(band);
            for (var opacity = 0; opacity < 16; opacity++)
            {
                var alpha = (byte)Math.Round(255 * opacity / 15.0);
                brushes[band, opacity] = Freeze(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)));
            }
        }
        return brushes;
    }

    private static Color ColorForBand(int band)
    {
        var position = band * (SpectrumColors.Length - 1.0) / (BandCount - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, SpectrumColors.Length - 1);
        var fraction = position - lower;
        var first = SpectrumColors[lower];
        var second = SpectrumColors[upper];
        return Color.FromRgb(
            (byte)Math.Round(first.R + (second.R - first.R) * fraction),
            (byte)Math.Round(first.G + (second.G - first.G) * fraction),
            (byte)Math.Round(first.B + (second.B - first.B) * fraction));
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }

    private struct Particle(
        double x,
        double y,
        double velocityX,
        double velocityY,
        double radius,
        double bornAt,
        double lifetime,
        int colorIndex)
    {
        public double X = x;
        public double Y = y;
        public double VelocityX = velocityX;
        public double VelocityY = velocityY;
        public double Radius = radius;
        public double BornAt = bornAt;
        public double Lifetime = lifetime;
        public double LastUpdatedAt = bornAt;
        public int ColorIndex = colorIndex;
    }
}
