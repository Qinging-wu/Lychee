using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lychee;

public partial class ColorPickerWindow : Window
{
    private bool _ready;
    private bool _updating;
    private bool _draggingSpectrum;
    private double _hue;
    private double _saturation;
    private double _brightness;

    public Color SelectedColor { get; private set; }

    public ColorPickerWindow(Color initialColor)
    {
        InitializeComponent();
        SelectedColor = initialColor;
        SpectrumCanvas.SizeChanged += Spectrum_SizeChanged;
        SetFromRgb(initialColor.R, initialColor.G, initialColor.B);
        _ready = true;
        RefreshPicker();
    }

    private void Spectrum_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var rectangle in SpectrumCanvas.Children.OfType<System.Windows.Shapes.Rectangle>())
        {
            rectangle.Width = SpectrumCanvas.ActualWidth;
            rectangle.Height = SpectrumCanvas.ActualHeight;
        }
        PositionMarker();
    }

    private void Hue_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating) return;
        _hue = HueSlider.Value;
        RefreshPicker();
    }

    private void Spectrum_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSpectrum = true;
        SpectrumCanvas.CaptureMouse();
        SetSpectrumPoint(e.GetPosition(SpectrumCanvas));
    }

    private void Spectrum_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingSpectrum && e.LeftButton == MouseButtonState.Pressed)
            SetSpectrumPoint(e.GetPosition(SpectrumCanvas));
    }

    private void Spectrum_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingSpectrum = false;
        SpectrumCanvas.ReleaseMouseCapture();
    }

    private void SetSpectrumPoint(Point point)
    {
        if (SpectrumCanvas.ActualWidth <= 0 || SpectrumCanvas.ActualHeight <= 0) return;
        _saturation = Math.Clamp(point.X / SpectrumCanvas.ActualWidth, 0, 1);
        _brightness = 1 - Math.Clamp(point.Y / SpectrumCanvas.ActualHeight, 0, 1);
        RefreshPicker();
    }

    private void RgbSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating) return;
        SetFromRgb((byte)Math.Round(RedSlider.Value), (byte)Math.Round(GreenSlider.Value), (byte)Math.Round(BlueSlider.Value));
        RefreshPicker();
    }

    private void RgbText_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_ready || _updating || !byte.TryParse(RedTextBox.Text, out var red) ||
            !byte.TryParse(GreenTextBox.Text, out var green) || !byte.TryParse(BlueTextBox.Text, out var blue)) return;
        SetFromRgb(red, green, blue);
        RefreshPicker();
    }

    private void Hex_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_ready || _updating) return;
        var value = HexTextBox.Text.Trim().TrimStart('#');
        if (value.Length == 3 && int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            value = string.Concat(value.Select(character => new string(character, 2)));
        if (value.Length == 8) value = value[2..];
        if (value.Length != 6 || !byte.TryParse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(value[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue)) return;
        SetFromRgb(red, green, blue);
        RefreshPicker();
    }

    private void SetFromRgb(byte red, byte green, byte blue)
    {
        var r = red / 255d;
        var g = green / 255d;
        var b = blue / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        _brightness = max;
        _saturation = max == 0 ? 0 : delta / max;
        if (delta == 0) _hue = 0;
        else if (max == r) _hue = 60 * (((g - b) / delta) % 6);
        else if (max == g) _hue = 60 * ((b - r) / delta + 2);
        else _hue = 60 * ((r - g) / delta + 4);
        if (_hue < 0) _hue += 360;
    }

    private void RefreshPicker()
    {
        if (!_ready) return;
        _updating = true;
        var hueColor = FromHsv(_hue, 1, 1);
        SpectrumCanvas.Background = new SolidColorBrush(hueColor);
        var color = FromHsv(_hue, _saturation, _brightness);
        SelectedColor = color;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        RedTextBox.Text = color.R.ToString(CultureInfo.InvariantCulture);
        GreenTextBox.Text = color.G.ToString(CultureInfo.InvariantCulture);
        BlueTextBox.Text = color.B.ToString(CultureInfo.InvariantCulture);
        HexTextBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ColorSwatch.Background = new SolidColorBrush(color);
        HueSlider.Value = _hue;
        _updating = false;
        PositionMarker();
    }

    private void PositionMarker()
    {
        Canvas.SetLeft(SpectrumMarker, _saturation * Math.Max(0, SpectrumCanvas.ActualWidth - SpectrumMarker.Width));
        Canvas.SetTop(SpectrumMarker, (1 - _brightness) * Math.Max(0, SpectrumCanvas.ActualHeight - SpectrumMarker.Height));
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60 % 2) - 1));
        var m = value - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
