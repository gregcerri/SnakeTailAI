using System.Windows;
using System.Windows.Media;

namespace SnakeTailAI.Controls;

/// <summary>A small area chart of recent values (lines per second). Set <see cref="Version"/> to redraw after the data changes.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(Sparkline), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Version
    {
        get => (int)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double w = ActualWidth, h = ActualHeight;
        if (values == null || values.Count < 2 || w <= 0 || h <= 0)
            return;

        double max = Math.Max(1, values.Max());
        double step = w / (values.Count - 1);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            Point P(int i) => new(i * step, h - 1 - (h - 2) * values[i] / max);
            lc.BeginFigure(P(0), false, false);
            ac.BeginFigure(new Point(0, h), true, true);
            ac.LineTo(P(0), false, false);
            for (int i = 1; i < values.Count; i++)
            {
                lc.LineTo(P(i), true, true);
                ac.LineTo(P(i), false, false);
            }
            ac.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        area.Freeze();

        var color = Stroke is SolidColorBrush s ? s.Color : Colors.DeepSkyBlue;
        var fill = new LinearGradientBrush(Color.FromArgb(0x70, color.R, color.G, color.B), Color.FromArgb(0x05, color.R, color.G, color.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.4) { LineJoin = PenLineJoin.Round }, line);
    }
}
