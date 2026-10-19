using System.Collections.Immutable;

namespace Lychee.Core;

/// <summary>A completed, immutable 16-band spectrum snapshot.</summary>
public sealed record SpectrumFrame
{
    public const int BandCount = 16;

    public DateTimeOffset CapturedAt { get; }
    public ImmutableArray<float> Bands { get; }
    public bool IsValid { get; }

    public SpectrumFrame(DateTimeOffset capturedAt, ReadOnlySpan<float> bands, bool isValid = true)
    {
        if (bands.Length != BandCount)
            throw new ArgumentException($"A spectrum frame must contain {BandCount} bands.", nameof(bands));

        CapturedAt = capturedAt;
        Bands = ImmutableArray.Create(bands.ToArray());
        IsValid = isValid;
    }

    public static SpectrumFrame Silence(DateTimeOffset capturedAt)
        => new(capturedAt, new float[BandCount], isValid: false);
}
