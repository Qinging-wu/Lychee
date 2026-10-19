namespace Lychee.Core;

internal static class AudioMeterPollingPolicy
{
    internal static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(33);
    internal static readonly TimeSpan PausedInterval = TimeSpan.FromMilliseconds(250);

    internal static TimeSpan GetInterval(bool spectrumActive)
        => spectrumActive ? ActiveInterval : PausedInterval;
}
