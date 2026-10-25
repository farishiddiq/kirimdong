using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Kirimdong.App.Controls;

/// <summary>
/// Colours one of the four edge buttons on a peer label: ink with orange glyph when that edge is the one
/// chosen (binding value equals the parameter), kraft with ink glyph otherwise. <see cref="Glyph"/> picks the glyph colour.
/// </summary>
public sealed class EdgeBrush : IValueConverter
{
    public bool Glyph { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var selected = string.Equals(value as string, parameter as string, StringComparison.Ordinal);
        var key = (selected, Glyph) switch
        {
            (true, false) => "Ink",
            (true, true) => "Accent",
            (false, false) => "KraftLight",
            (false, true) => "Ink",
        };
        return (Brush)Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
