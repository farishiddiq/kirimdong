using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Kirimdong.App.Controls;

/// <summary>
/// Colours one of the four edge chips on a PC tile: black with a white arrow when that edge is the one
/// chosen (binding value equals the parameter), white with a black arrow otherwise. <see cref="Glyph"/> picks the arrow colour.
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
            (true, true) => "OnInk",
            (false, false) => "Card",
            (false, true) => "Ink",
        };
        return (Brush)Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
