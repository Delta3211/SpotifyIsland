using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpotifyIsland.Services;

public class ExtractedPalette
{
    public Color AccentColor { get; set; } = Color.FromRgb(30, 215, 96);
    public Color GlowColor { get; set; } = Color.FromArgb(140, 30, 215, 96);
    public Color DarkColor { get; set; } = Color.FromRgb(16, 16, 18);
    public Color SecondaryAccent { get; set; } = Color.FromRgb(40, 180, 110);
}

public static class ColorExtractor
{
    private static readonly Color DefaultSpotifyGreen = Color.FromRgb(30, 215, 96);

    public static ExtractedPalette Extract(BitmapSource? bitmap)
    {
        var result = new ExtractedPalette();
        if (bitmap == null) return result;

        try
        {
            // Format check / convert to Bgra32
            FormatConvertedBitmap formatted = new FormatConvertedBitmap();
            formatted.BeginInit();
            formatted.Source = bitmap;
            formatted.DestinationFormat = PixelFormats.Bgra32;
            formatted.EndInit();

            // Downsample to 32x32 for ultra fast analysis
            int targetWidth = 32;
            int targetHeight = 32;
            double scaleX = (double)targetWidth / Math.Max(1, formatted.PixelWidth);
            double scaleY = (double)targetHeight / Math.Max(1, formatted.PixelHeight);

            TransformedBitmap scaled = new TransformedBitmap(formatted, new ScaleTransform(scaleX, scaleY));
            int stride = scaled.PixelWidth * 4;
            byte[] pixels = new byte[scaled.PixelHeight * stride];
            scaled.CopyPixels(pixels, stride, 0);

            List<(Color color, double score, double sat, double val)> candidates = new();

            for (int y = 0; y < scaled.PixelHeight; y++)
            {
                for (int x = 0; x < scaled.PixelWidth; x++)
                {
                    int idx = (y * stride) + (x * 4);
                    byte b = pixels[idx];
                    byte g = pixels[idx + 1];
                    byte r = pixels[idx + 2];
                    byte a = pixels[idx + 3];

                    if (a < 128) continue;

                    RgbToHsv(r, g, b, out double h, out double s, out double v);

                    // Skip pure blacks, extreme whites, and desaturated greys
                    if (v < 0.20 || v > 0.98 || s < 0.25) continue;

                    // Score based on saturation and balanced brightness (favor vivid, rich colors)
                    double vibrancy = s * (1.0 - Math.Abs(v - 0.70));
                    candidates.Add((Color.FromRgb(r, g, b), vibrancy, s, v));
                }
            }

            if (candidates.Count > 0)
            {
                // Sort by vibrancy score
                var best = candidates.OrderByDescending(c => c.score).First();
                
                // Boost brightness slightly if too dark for a vibrant button
                Color boosted = BoostVibrancy(best.color, best.sat, best.val);
                result.AccentColor = boosted;
                result.GlowColor = Color.FromArgb(150, boosted.R, boosted.G, boosted.B);
                
                // Dark glass tint based on accent
                byte darkR = (byte)Math.Clamp((int)(boosted.R * 0.12), 12, 40);
                byte darkG = (byte)Math.Clamp((int)(boosted.G * 0.12), 12, 40);
                byte darkB = (byte)Math.Clamp((int)(boosted.B * 0.12), 14, 45);
                result.DarkColor = Color.FromRgb(darkR, darkG, darkB);

                // Secondary color
                var secondary = candidates.Where(c => Math.Abs(c.color.R - boosted.R) + Math.Abs(c.color.G - boosted.G) > 60)
                                          .OrderByDescending(c => c.score)
                                          .FirstOrDefault();
                if (secondary.color != default)
                {
                    result.SecondaryAccent = secondary.color;
                }
                else
                {
                    result.SecondaryAccent = Color.FromRgb(
                        (byte)Math.Clamp(boosted.R * 0.8, 0, 255),
                        (byte)Math.Clamp(boosted.G * 0.8, 0, 255),
                        (byte)Math.Clamp(boosted.B * 0.8, 0, 255)
                    );
                }
            }
            else
            {
                result.AccentColor = DefaultSpotifyGreen;
                result.GlowColor = Color.FromArgb(140, 30, 215, 96);
                result.DarkColor = Color.FromRgb(16, 18, 16);
            }
        }
        catch
        {
            result.AccentColor = DefaultSpotifyGreen;
            result.GlowColor = Color.FromArgb(140, 30, 215, 96);
            result.DarkColor = Color.FromRgb(16, 18, 16);
        }

        return result;
    }

    private static Color BoostVibrancy(Color c, double s, double v)
    {
        // Ensure the color is punchy and readable against dark glass
        if (v < 0.65)
        {
            double factor = 0.75 / Math.Max(0.1, v);
            byte r = (byte)Math.Clamp((int)(c.R * factor), 0, 255);
            byte g = (byte)Math.Clamp((int)(c.G * factor), 0, 255);
            byte b = (byte)Math.Clamp((int)(c.B * factor), 0, 255);
            return Color.FromRgb(r, g, b);
        }
        return c;
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;

        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        v = max;
        s = max == 0 ? 0 : delta / max;

        if (delta == 0)
        {
            h = 0;
        }
        else
        {
            if (rd == max)
                h = (gd - bd) / delta + (gd < bd ? 6 : 0);
            else if (gd == max)
                h = (bd - rd) / delta + 2;
            else
                h = (rd - gd) / delta + 4;
            h /= 6;
        }
    }
}
