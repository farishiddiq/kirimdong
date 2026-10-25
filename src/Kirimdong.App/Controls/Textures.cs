using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kirimdong.App.Controls;

/// <summary>
/// Material surfaces drawn once at startup: kraft board with its fibres and the faint ridges of the
/// corrugation underneath, and label paper. Seeded, so every launch looks the same.
/// </summary>
public static class Textures
{
    public static Brush Kraft(Color baseColor, int seed)
    {
        const int size = 384;
        var pixels = new byte[size * size * 4];
        var rnd = new Random(seed);
        var lum = new double[size * size];

        for (var y = 0; y < size; y++)
        {
            // Flutes of the corrugated layer show through the liner as soft horizontal ridges (period 12px; tiles evenly).
            var ridge = Math.Sin(y / 12.0 * Math.PI * 2) * 2.2;
            for (var x = 0; x < size; x++)
                lum[y * size + x] = ridge + (rnd.NextDouble() - 0.5) * 9;
        }

        // Fibres: short, mostly horizontal strands, a little darker or lighter than the board.
        for (var f = 0; f < 1400; f++)
        {
            double x = rnd.Next(size), y = rnd.Next(size);
            var angle = (rnd.NextDouble() - 0.5) * 0.9;
            var length = 6 + rnd.Next(22);
            var tone = rnd.NextDouble() < 0.7 ? -(4 + rnd.NextDouble() * 9) : 3 + rnd.NextDouble() * 6;
            for (var i = 0; i < length; i++)
            {
                var px = ((int)(x + Math.Cos(angle) * i) % size + size) % size;
                var py = ((int)(y + Math.Sin(angle) * i) % size + size) % size;
                lum[py * size + px] += tone;
            }
        }

        // A few darker specks of recycled pulp.
        for (var s = 0; s < 260; s++)
        {
            int x = rnd.Next(size), y = rnd.Next(size);
            lum[y * size + x] -= 18 + rnd.Next(14);
        }

        for (var i = 0; i < lum.Length; i++)
        {
            var d = lum[i];
            pixels[i * 4 + 0] = Clamp(baseColor.B + d * 0.8);
            pixels[i * 4 + 1] = Clamp(baseColor.G + d * 0.9);
            pixels[i * 4 + 2] = Clamp(baseColor.R + d);
            pixels[i * 4 + 3] = 255;
        }
        return Tile(pixels, size);
    }

    public static Brush Paper(Color baseColor, int seed)
    {
        const int size = 256;
        var pixels = new byte[size * size * 4];
        var rnd = new Random(seed);
        for (var i = 0; i < size * size; i++)
        {
            var d = (rnd.NextDouble() - 0.5) * 5;
            pixels[i * 4 + 0] = Clamp(baseColor.B + d);
            pixels[i * 4 + 1] = Clamp(baseColor.G + d);
            pixels[i * 4 + 2] = Clamp(baseColor.R + d);
            pixels[i * 4 + 3] = 255;
        }
        return Tile(pixels, size);
    }

    private static Brush Tile(byte[] pixels, int size)
    {
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bmp.Freeze();
        var brush = new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
