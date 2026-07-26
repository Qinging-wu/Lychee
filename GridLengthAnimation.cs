using System.Windows;
using System.Windows.Media.Animation;

namespace Lychee;

public sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(nameof(From), typeof(GridLength?), typeof(GridLengthAnimation));

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(nameof(To), typeof(GridLength?), typeof(GridLengthAnimation));

    public GridLength? From
    {
        get => (GridLength?)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength? To
    {
        get => (GridLength?)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction { get; set; }

    public override Type TargetPropertyType => typeof(GridLength);

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue,
        AnimationClock animationClock)
    {
        if (animationClock == null) return From ?? GridLength.Auto;

        var fromVal = From?.Value ?? ((GridLength)defaultOriginValue).Value;
        var toVal = To?.Value ?? ((GridLength)defaultDestinationValue).Value;

        var progress = animationClock.CurrentProgress ?? 0;
        if (EasingFunction != null) progress = EasingFunction.Ease(progress);

        var value = fromVal + (toVal - fromVal) * progress;
        if (value < 0) value = 0;

        return new GridLength(value, GridUnitType.Pixel);
    }

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();
}

