using System;
using System.Windows;
using System.Windows.Media;

namespace Kirimdong.App.Controls;

/// <summary>
/// Shipment-style progress: a check node at each end, a dotted line between them, the travelled part in
/// orange with a knob where the parcel is now. Grey all through when the parcel did not arrive.
/// </summary>
public class DotTrack : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(DotTrack), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDoneProperty = DependencyProperty.Register(
        nameof(IsDone), typeof(bool), typeof(DotTrack), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsProblemProperty = DependencyProperty.Register(
        nameof(IsProblem), typeof(bool), typeof(DotTrack), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OnDarkProperty = DependencyProperty.Register(
        nameof(OnDark), typeof(bool), typeof(DotTrack), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsDone { get => (bool)GetValue(IsDoneProperty); set => SetValue(IsDoneProperty, value); }
    public bool IsProblem { get => (bool)GetValue(IsProblemProperty); set => SetValue(IsProblemProperty, value); }
    public bool OnDark { get => (bool)GetValue(OnDarkProperty); set => SetValue(OnDarkProperty, value); }

    private static readonly Brush Orange = Freeze(new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)));   // orange-500
    private static readonly Brush GreyLight = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4))); // neutral-300
    private static readonly Brush GreyDark = Freeze(new SolidColorBrush(Color.FromRgb(0x52, 0x52, 0x52)));  // neutral-600

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, 22);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, y = ActualHeight / 2, r = 9;
        if (w < 4 * r) return;
        var grey = OnDark ? GreyDark : GreyLight;
        var value = IsDone ? 1 : Math.Clamp(Value, 0, 1);
        var active = IsProblem ? grey : Orange;
        double x0 = r, x1 = w - r, at = x0 + (x1 - x0) * value;

        for (var x = x0 + r + 5; x < x1 - r - 2; x += 7)
            dc.DrawEllipse(x <= at ? active : grey, null, new Point(x, y), 1.7, 1.7);

        Node(dc, x0, y, r, active, true);
        Node(dc, x1, y, r, IsDone ? Orange : grey, IsDone);

        if (!IsDone && !IsProblem && value > 0.001)
        {
            // Where the parcel is now.
            dc.DrawEllipse(Orange, null, new Point(at, y), 7, 7);
            dc.DrawEllipse(Brushes.White, null, new Point(at, y), 2.6, 2.6);
        }
    }

    private void Node(DrawingContext dc, double x, double y, double r, Brush fill, bool filled)
    {
        var tick = new StreamGeometry();
        using (var c = tick.Open())
        {
            c.BeginFigure(new Point(x - 3.6, y + 0.2), false, false);
            c.LineTo(new Point(x - 1, y + 2.8), true, true);
            c.LineTo(new Point(x + 3.8, y - 2.6), true, true);
        }
        tick.Freeze();
        if (filled)
        {
            dc.DrawEllipse(fill, null, new Point(x, y), r, r);
            dc.DrawGeometry(null, new Pen(Brushes.White, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, tick);
        }
        else
        {
            dc.DrawEllipse(null, new Pen(fill, 1.6), new Point(x, y), r - 0.8, r - 0.8);
            dc.DrawGeometry(null, new Pen(fill, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, tick);
        }
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
