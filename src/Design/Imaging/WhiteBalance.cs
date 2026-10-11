// The light a photograph was taken in, taken back out — brief-img-13-board-photos.md R-im13-3.
//
// Two corrections, in this order:
//
//   1. GREY WORLD — per-channel gains that make the picture's mean colour grey. A board is mostly solder mask, and the
//      mask is not grey: the plain assumption over every pixel would bleach a green board towards grey and turn its
//      copper pink. So the mean is taken over the pixels that are near grey (silkscreen, tin, the bench, the board's
//      shadows) — in two passes, a loose one on the colours as taken (a cast pushes a grey bench past the tight limit)
//      and a tight one on the colours that pass corrected — and over every pixel only when too few are.
//      Clipped pixels (a highlight) say nothing about the light's colour and are left out of both. Gains are clamped.
//
//   2. FLAT FIELD — the light's fall-off across the picture divided out, so one side of a board is not read as a
//      different colour from the other. The illumination is the luminance blurred with a radius large against any
//      feature on the board (three box passes, ≈ a Gaussian), and each pixel's three channels are scaled by ONE factor —
//      mean illumination over local — so the hue is kept. Pixels outside the picture (a rectified photo's corners) are
//      left out of the blur: it is normalised by the blurred mask, so the board's edge is not darkened by the paper.
//
// Both run in fixed row bands, and every sum is formed in a fixed order: the same bytes on any thread count (D17).

namespace CircuitRF.Design.Imaging;

public static class WhiteBalance
{
    /// <summary>A pixel is near grey below this CIELAB chroma — the pixels grey world is taken over.</summary>
    public const double GreyChroma = 12.0;

    /// <summary>Near-grey pixels are used only when at least this share of the picture is near grey.</summary>
    public const double MinGreyShare = 0.02;

    /// <summary>A channel at or above this is clipped: a highlight, not the light's colour.</summary>
    public const int Clipped = 250;

    /// <summary>A gain is held within [1 / MaxGain, MaxGain].</summary>
    public const double MaxGain = 2.0;

    /// <summary>The flat field's blur radius as a share of the picture's shorter side.</summary>
    public const double FlatFieldRadiusShare = 0.125;

    /// <summary>The first pass takes pixels below this chroma: a colour cast pushes a grey bench or a white legend past
    /// <see cref="GreyChroma"/> before it is corrected, and the second pass, on the corrected colours, tightens it.</summary>
    public const double FirstPassChroma = 20.0;

    /// <summary>The per-channel gains grey world gives <paramref name="image"/>, over the pixels <paramref name="inside"/>
    /// marks (all, when null) — two passes, the second over the pixels near grey once the first's gains are applied.</summary>
    public static (double R, double G, double B) GreyWorldGains(RasterImage image, BinaryImage? inside = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var first = Pass(image, inside, (1, 1, 1), FirstPassChroma);
        var second = Pass(image, inside, first, GreyChroma);
        return (Clamp(second.R), Clamp(second.G), Clamp(second.B));

        static double Clamp(double g) => Math.Clamp(g, 1 / MaxGain, MaxGain);
    }

    /// <summary>One grey-world pass over the colours as <paramref name="k"/> corrects them: the gains that make the mean
    /// of the near-grey pixels grey (of every pixel, when too few are near grey).</summary>
    private static (double R, double G, double B) Pass(RasterImage image, BinaryImage? inside, (double R, double G, double B) k,
                                                       double chroma)
    {
        var p = image.Rgba;
        int n = image.Width * image.Height;
        double gr = 0, gg = 0, gb = 0, ar = 0, ag = 0, ab = 0;
        long greys = 0, all = 0;
        var memo = new Dictionary<int, bool>();
        for (int i = 0; i < n; i++)
        {
            if (inside is not null && inside.Pixels[i] == 0) continue;
            int o = i * 4;
            int r = p[o], g = p[o + 1], b = p[o + 2];
            if (r >= Clipped || g >= Clipped || b >= Clipped) continue;
            double cr = r * k.R, cg = g * k.G, cb = b * k.B;
            all++;
            ar += cr; ag += cg; ab += cb;
            int rgb = (r << 16) | (g << 8) | b;
            if (!memo.TryGetValue(rgb, out bool nearGrey))
            {
                var lab = CieLab.FromRgb(Byte(cr), Byte(cg), Byte(cb));
                memo[rgb] = nearGrey = Math.Sqrt(lab.A * lab.A + lab.B * lab.B) < chroma;
            }
            if (!nearGrey) continue;
            greys++;
            gr += cr; gg += cg; gb += cb;
        }
        if (all == 0) return (1, 1, 1);
        bool useGrey = greys >= MinGreyShare * all;
        double mr = useGrey ? gr / greys : ar / all, mg = useGrey ? gg / greys : ag / all, mb = useGrey ? gb / greys : ab / all;
        double grey = (mr + mg + mb) / 3;
        return (k.R * Gain(grey, mr), k.G * Gain(grey, mg), k.B * Gain(grey, mb));

        static double Gain(double grey, double mean) => mean > 0 ? Math.Clamp(grey / mean, 1 / MaxGain, MaxGain) : 1;
    }

    /// <summary><paramref name="image"/> with the light's colour (grey world) and its fall-off (flat field) taken out.
    /// Pixels outside <paramref name="inside"/> are kept as they are.</summary>
    public static RasterImage Correct(RasterImage image, BinaryImage? inside = null, int maxThreads = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width, h = image.Height, n = w * h;
        var (kr, kg, kb) = GreyWorldGains(image, inside);
        var src = image.Rgba;

        // Luminance after the gains, and the mask the blur is normalised by.
        var lum = new float[n];
        var weight = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (inside is not null && inside.Pixels[i] == 0) continue;
            int o = i * 4;
            lum[i] = (float)(0.2126 * src[o] * kr + 0.7152 * src[o + 1] * kg + 0.0722 * src[o + 2] * kb);
            weight[i] = 1;
        }
        int radius = Math.Max(4, (int)Math.Round(FlatFieldRadiusShare * Math.Min(w, h)));
        var blurred = Blur(lum, w, h, radius, maxThreads);
        var norm = Blur(weight, w, h, radius, maxThreads);

        double sum = 0;
        long count = 0;
        for (int i = 0; i < n; i++)
        {
            if (weight[i] == 0 || norm[i] <= 0) continue;
            sum += blurred[i] / norm[i];
            count++;
        }
        double mean = count > 0 ? sum / count : 1;

        var dst = new byte[src.Length];
        Bands.For(h, maxThreads, (y0, y1) =>
        {
            for (int i = y0 * w; i < y1 * w; i++)
            {
                int o = i * 4;
                dst[o + 3] = 255;
                if (weight[i] == 0)
                {
                    dst[o] = src[o]; dst[o + 1] = src[o + 1]; dst[o + 2] = src[o + 2];
                    continue;
                }
                double local = norm[i] > 0 ? blurred[i] / norm[i] : mean;
                double f = local > 1 ? Math.Clamp(mean / local, 1 / MaxGain, MaxGain) : 1;
                dst[o] = Byte(src[o] * kr * f);
                dst[o + 1] = Byte(src[o + 1] * kg * f);
                dst[o + 2] = Byte(src[o + 2] * kb * f);
            }
        });
        return new RasterImage(w, h, dst);
    }

    private static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>Three box passes of radius <paramref name="r"/> along x then y — ≈ a Gaussian, O(n) whatever the radius.
    /// Each row (column) is summed left to right, so the result does not depend on the thread count.</summary>
    internal static float[] Blur(float[] v, int w, int h, int r, int maxThreads)
    {
        var a = (float[])v.Clone();
        var b = new float[v.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            Bands.For(h, maxThreads, (y0, y1) => { for (int y = y0; y < y1; y++) Box(a, b, y * w, 1, w, r); });
            Bands.For(w, maxThreads, (x0, x1) => { for (int x = x0; x < x1; x++) Box(b, a, x, w, h, r); });
        }
        return a;
    }

    /// <summary>One line of a box blur: the sum over [i − r, i + r] clipped to the line, over 2r + 1 (zero beyond the
    /// ends, so a normalising mask blurred the same way divides the end effect back out).</summary>
    private static void Box(float[] src, float[] dst, int start, int stride, int count, int r)
    {
        double s = 0;
        for (int i = 0; i <= Math.Min(r, count - 1); i++) s += src[start + i * stride];
        double k = 1.0 / (2 * r + 1);
        for (int i = 0; i < count; i++)
        {
            dst[start + i * stride] = (float)(s * k);
            int add = i + r + 1, drop = i - r;
            if (add < count) s += src[start + add * stride];
            if (drop >= 0) s -= src[start + drop * stride];
        }
    }
}
