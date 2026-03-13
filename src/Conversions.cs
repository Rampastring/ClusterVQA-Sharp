using System;

namespace ClusterVQA;

/// <summary>
/// Color space conversions, quantization, and dithering utilities.
/// Operates on flat float arrays in [H, W, 3] row-major layout.
/// </summary>
public static class Conversions
{
    public static bool UseYuvColorspace { get; set; } = true;

    // Rec.601 luma weights
    private const double WR = 0.299;
    private const double WG = 0.587;
    private const double WB = 0.114;

    /// <summary>
    /// Convert an RGB float pixel (0..1) to YUV using the BT.601 matrix
    /// (same formula as skimage.color.rgb2yuv).
    /// </summary>
    public static void Rgb2Yuv(double r, double g, double b, out double y, out double u, out double v)
    {
        y = WR * r + WG * g + WB * b;
        u = -0.14714119 * r - 0.28886916 * g + 0.43601035 * b;
        v = 0.61497538 * r - 0.51496512 * g - 0.10001026 * b;
    }

    /// <summary>
    /// Convert a YUV pixel back to RGB (inverse of Rgb2Yuv).
    /// </summary>
    public static void Yuv2Rgb(double y, double u, double v, out double r, out double g, out double b)
    {
        r = y + 1.13988303 * v;
        g = y - 0.39465604 * u - 0.58059860 * v;
        b = y + 2.03211091 * u;
    }

    /// <summary>
    /// Convert a flat [H*W*3] RGB float image to perceptual space (YUV*100 or weighted RGB*100).
    /// </summary>
    public static void Rgb2Perceptual(ReadOnlySpan<float> rgb, Span<float> output)
    {
        int numPixels = rgb.Length / 3;
        if (UseYuvColorspace)
        {
            for (int i = 0; i < numPixels; i++)
            {
                int idx = i * 3;
                Rgb2Yuv(rgb[idx], rgb[idx + 1], rgb[idx + 2],
                    out double y, out double u, out double v);
                output[idx] = (float)(y * 100.0);
                output[idx + 1] = (float)(u * 100.0);
                output[idx + 2] = (float)(v * 100.0);
            }
        }
        else
        {
            for (int i = 0; i < numPixels; i++)
            {
                int idx = i * 3;
                output[idx] = (float)(rgb[idx] * WR * 100.0);
                output[idx + 1] = (float)(rgb[idx + 1] * WG * 100.0);
                output[idx + 2] = (float)(rgb[idx + 2] * WB * 100.0);
            }
        }
    }

    /// <summary>
    /// Convert a flat [H*W*3] perceptual image back to RGB floats (0..1 range).
    /// </summary>
    public static void Perceptual2Rgb(ReadOnlySpan<float> perceptual, Span<float> output)
    {
        int numPixels = perceptual.Length / 3;
        if (UseYuvColorspace)
        {
            for (int i = 0; i < numPixels; i++)
            {
                int idx = i * 3;
                Yuv2Rgb(perceptual[idx] / 100.0, perceptual[idx + 1] / 100.0, perceptual[idx + 2] / 100.0,
                    out double r, out double g, out double b);
                output[idx] = (float)r;
                output[idx + 1] = (float)g;
                output[idx + 2] = (float)b;
            }
        }
        else
        {
            for (int i = 0; i < numPixels; i++)
            {
                int idx = i * 3;
                output[idx] = (float)(perceptual[idx] / 100.0 / WR);
                output[idx + 1] = (float)(perceptual[idx + 1] / 100.0 / WG);
                output[idx + 2] = (float)(perceptual[idx + 2] / 100.0 / WB);
            }
        }
    }

    /// <summary>
    /// Quantize a float (0..1) RGB value to 5-bit (0..31) per channel.
    /// Equivalent to: clip(floor(value * 31.5 + 0.5), 0, 31)
    /// </summary>
    public static int RoundToRgb555(float value)
    {
        return Math.Clamp((int)(value * 31.5f + 0.5f), 0, 31);
    }

    /// <summary>
    /// Quantize a flat RGB float image in-place to RGB555 (stores int values 0..31 as floats).
    /// Returns the result via the output span as integer channel values 0..31.
    /// </summary>
    public static void RoundRgbFloatToRgb555(ReadOnlySpan<float> rgbFloat, Span<int> output)
    {
        for (int i = 0; i < rgbFloat.Length; i++)
        {
            output[i] = Math.Clamp((int)(rgbFloat[i] * 31.5f + 0.5f), 0, 31);
        }
    }

    /// <summary>
    /// Expand 5-bit channel values (0..31) to 8-bit (0..255).
    /// Formula: (val &lt;&lt; 3) | (val &gt;&gt; 2)  i.e. ABCDE -> ABCDEABC
    /// </summary>
    public static int ExpandRgb555ToRgb888(int val5)
    {
        return (val5 << 3) | (val5 >> 2);
    }

    /// <summary>
    /// Expand a flat array of 5-bit channel values to 8-bit.
    /// </summary>
    public static void ExpandRgb555ToRgb888(ReadOnlySpan<int> rgb555, Span<int> output)
    {
        for (int i = 0; i < rgb555.Length; i++)
        {
            output[i] = (rgb555[i] << 3) | (rgb555[i] >> 2);
        }
    }

    // Threshold map for 4x4 ordered dithering (from Wikipedia)
    private static readonly double[,] ThresholdMap4X4 =
    {
        { -0.5 + 0.0 / 16.0, -0.5 + 8.0 / 16.0, -0.5 + 2.0 / 16.0, -0.5 + 10.0 / 16.0 },
        { -0.5 + 12.0 / 16.0, -0.5 + 4.0 / 16.0, -0.5 + 14.0 / 16.0, -0.5 + 6.0 / 16.0 },
        { -0.5 + 3.0 / 16.0, -0.5 + 11.0 / 16.0, -0.5 + 1.0 / 16.0, -0.5 + 9.0 / 16.0 },
        { -0.5 + 15.0 / 16.0, -0.5 + 7.0 / 16.0, -0.5 + 13.0 / 16.0, -0.5 + 5.0 / 16.0 },
    };

    /// <summary>
    /// Build a 4x4 tiled dither table for a given image dimension.
    /// Returns a flat float[h * w] array.
    /// </summary>
    public static float[] Make4X4DitherTable(int h, int w)
    {
        var table = new float[h * w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                table[y * w + x] = (float)ThresholdMap4X4[y % 4, x % 4];
            }
        }
        return table;
    }
}
