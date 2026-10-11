// A picture enlarged by a whole factor — brief-imf-2-picture-scale-profile.md R-imf2-3.
//
// Lanczos-3, separable, in managed code. The brief names SkiaSharp's resampler; the Skia this build carries (m119)
// offers only the cubic resamplers, and a managed filter gives the same bytes on every machine and every SIMD path,
// which a reading that is compared byte for byte (RasterDeterminismTests, the CLI against the in-process call) needs.
// A whole factor k maps a source pixel's edges [i, i + 1) onto [k·i, k·(i + 1)), so a point of the enlarged picture
// is a point of the source divided by k — the one mapping SchematicImageRead.ToSource applies.

namespace CircuitRF.Design.Imaging;

public static class Resample
{
    /// <summary>Lanczos' a: three lobes.</summary>
    private const int Lobes = 3;

    /// <summary><paramref name="src"/> enlarged <paramref name="factor"/> times each way (1: the picture itself).</summary>
    public static RasterImage Enlarge(RasterImage src, int factor, int maxThreads = 0)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor));
        if (factor == 1) return src;
        int w = src.Width, h = src.Height, W = w * factor, H = h * factor;

        // The taps of each output phase: the same for every output pixel at that phase.
        var taps = new (int Offset, double[] Weights)[factor];
        for (int ph = 0; ph < factor; ph++)
        {
            double centre = (ph + 0.5) / factor - 0.5;          // the output pixel's centre in source pixels, from i
            int first = (int)Math.Floor(centre) - Lobes + 1;
            var wts = new double[2 * Lobes];
            double sum = 0;
            for (int k = 0; k < wts.Length; k++)
            {
                wts[k] = Kernel(centre - (first + k));
                sum += wts[k];
            }
            for (int k = 0; k < wts.Length; k++) wts[k] /= sum;
            taps[ph] = (first, wts);
        }

        // Horizontal pass into floats, then vertical into bytes; edges clamp.
        var p = src.Rgba;
        var mid = new float[(long)W * h * 3];
        Bands.For(h, maxThreads, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
                for (int X = 0; X < W; X++)
                {
                    int i = X / factor;
                    var (off, wts) = taps[X % factor];
                    double r = 0, g = 0, b = 0;
                    for (int k = 0; k < wts.Length; k++)
                    {
                        int x = Math.Clamp(i + off + k, 0, w - 1);
                        int s = (y * w + x) * 4;
                        r += wts[k] * p[s];
                        g += wts[k] * p[s + 1];
                        b += wts[k] * p[s + 2];
                    }
                    long o = ((long)y * W + X) * 3;
                    mid[o] = (float)r;
                    mid[o + 1] = (float)g;
                    mid[o + 2] = (float)b;
                }
        });
        var r8 = new byte[(long)W * H * 4];
        Bands.For(H, maxThreads, (Y0, Y1) =>
        {
            for (int Y = Y0; Y < Y1; Y++)
            {
                int j = Y / factor;
                var (off, wts) = taps[Y % factor];
                for (int X = 0; X < W; X++)
                {
                    double r = 0, g = 0, b = 0;
                    for (int k = 0; k < wts.Length; k++)
                    {
                        int y = Math.Clamp(j + off + k, 0, h - 1);
                        long s = ((long)y * W + X) * 3;
                        r += wts[k] * mid[s];
                        g += wts[k] * mid[s + 1];
                        b += wts[k] * mid[s + 2];
                    }
                    long o = ((long)Y * W + X) * 4;
                    r8[o] = Byte(r);
                    r8[o + 1] = Byte(g);
                    r8[o + 2] = Byte(b);
                    r8[o + 3] = 255;
                }
            }
        });
        return new RasterImage(W, H, r8);
    }

    private static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static double Kernel(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-12) return 1;
        if (x >= Lobes) return 0;
        double px = Math.PI * x;
        return Lobes * Math.Sin(px) * Math.Sin(px / Lobes) / (px * px);
    }
}
