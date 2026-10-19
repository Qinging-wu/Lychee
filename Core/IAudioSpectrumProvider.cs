namespace Lychee.Core;

/// <summary>Audio-specific read-only view model surface; does not expand the shared info-module contract.</summary>
public interface IAudioSpectrumProvider
{
    SpectrumFrame? LatestSpectrum { get; }
    string SpectrumStatus { get; }
}
