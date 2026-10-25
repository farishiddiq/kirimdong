using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kirimdong.App.Controls;

/// <summary>
/// A piece of paper stuck on the box: a filled shape with cut corners, optionally a row of
/// punched holes down its left edge (a tear-off slip). No outline, no shadow: the edge is the fill.
/// </summary>
public class PaperCard : Decorator
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(PaperCard),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NotchProperty = DependencyProperty.Register(
        nameof(Notch), typeof(double), typeof(PaperCard),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PunchedProperty = DependencyProperty.Register(
        nameof(Punched), typeof(bool), typeof(PaperCard),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoleBrushProperty = DependencyProperty.Register(
        nameof(HoleBrush), typeof(Brush), typeof(PaperCard),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(PaperCard),
        new FrameworkPropertyMetadata(new Thickness(0), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double Notch { get => (double)GetValue(NotchProperty); set => SetValue(NotchProperty, value); }
    public bool Punched { get => (bool)GetValue(PunchedProperty); set => SetValue(PunchedProperty, value); }
    public Brush HoleBrush { get => (Brush)GetValue(HoleBrushProperty); set => SetValue(HoleBrushProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    protected override Size MeasureOverride(Size constraint)
    {
        var p = Padding;
        var inner = new Size(Math.Max(0, constraint.Width - p.Left - p.Right), Math.Max(0, constraint.Height - p.Top - p.Bottom));
        Child?.Measure(inner);
        var desired = Child?.DesiredSize ?? new Size();
        return new Size(desired.Width + p.Left + p.Right, desired.Height + p.Top + p.Bottom);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var p = Padding;
        Child?.Arrange(new Rect(p.Left, p.Top,
            Math.Max(0, arrangeSize.Width - p.Left - p.Right), Math.Max(0, arrangeSize.Height - p.Top - p.Bottom)));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, n = Math.Min(Notch, Math.Min(w, h) / 3);
        if (w <= 0 || h <= 0) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(n, 0), true, true);
            c.LineTo(new Point(w - n, 0), false, false);
            c.LineTo(new Point(w, n), false, false);
            c.LineTo(new Point(w, h - n), false, false);
            c.LineTo(new Point(w - n, h), false, false);
            c.LineTo(new Point(n, h), false, false);
            c.LineTo(new Point(0, h - n), false, false);
            c.LineTo(new Point(0, n), false, false);
        }
        g.Freeze();
        dc.DrawGeometry(Fill, null, g);

        if (Punched)
        {
            const double radius = 3.5, step = 14, x = 14;
            for (var y = n + step / 2; y < h - n; y += step)
                dc.DrawEllipse(HoleBrush, null, new Point(x, y), radius, radius);
        }
    }
}

/// <summary>A shipping-label barcode. Bars are derived from <see cref="Code"/>, so each PC's label is its own.</summary>
public class Barcode : FrameworkElement
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(Barcode),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(
        nameof(Ink), typeof(Brush), typeof(Barcode),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Code { get => (string)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var seed = 17;
        foreach (var ch in Code ?? "") seed = unchecked(seed * 31 + ch);
        var rnd = new Random(seed);
        double x = 0, h = ActualHeight, w = ActualWidth;
        // Guard bars at both ends, like a real code.
        dc.DrawRectangle(Ink, null, new Rect(0, 0, 3, h));
        dc.DrawRectangle(Ink, null, new Rect(6, 0, 3, h));
        x = 13;
        while (x < w - 13)
        {
            var bar = 2 + rnd.Next(4) * 2;
            var gap = 2 + rnd.Next(3) * 2;
            if (x + bar > w - 13) break;
            dc.DrawRectangle(Ink, null, new Rect(x, 0, bar, h));
            x += bar + gap;
        }
        dc.DrawRectangle(Ink, null, new Rect(w - 9, 0, 3, h));
        dc.DrawRectangle(Ink, null, new Rect(w - 3, 0, 3, h));
    }
}

/// <summary>A strip of packing tape with torn ends. Purely decorative material, sits over a label edge.</summary>
public class Tape : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Tape),
        new FrameworkPropertyMetadata(Brushes.Tan, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var rnd = new Random(7);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(4, 0), true, true);
            c.LineTo(new Point(w - 4, 0), false, false);
            for (var y = 0.0; y < h; y += 4) c.LineTo(new Point(w - rnd.Next(0, 7), y + 2), false, false);
            c.LineTo(new Point(w - 4, h), false, false);
            c.LineTo(new Point(4, h), false, false);
            for (var y = h; y > 0; y -= 4) c.LineTo(new Point(rnd.Next(0, 7), y - 2), false, false);
        }
        g.Freeze();
        dc.DrawGeometry(Fill, null, g);
    }
}
