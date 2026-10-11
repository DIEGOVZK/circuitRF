// The drawing grid a schematic picture shows — the faint lines an editor's canvas draws behind a schematic, and the
// grid its wires run on — read as a pitch and a phase in source pixels.
//
// A screenshot of a schematic editor carries its own scale: one grid pitch is one grid step of the drawing it came
// from. Reading it lets the drawing be made at that scale, every wire on a grid line, and the picture laid under it
// lines up with it. A picture with no grid (a datasheet's figure) has none, and the frame falls back on the symbols.
//
// The column and row brightness profiles are high-passed against a running median, so a thin line of either polarity
// is a bump and a filled area is not, and the bumps' periodicity is measured directly: a comb of period p has equal
// strength at p, p/2, p/3 …, and none at 2p, so the pitch is the LONGEST period with at least half the strongest.

using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>A picture's drawing grid: lines at <c>PhaseX + n·Pitch</c> and <c>PhaseY + n·Pitch</c>, source pixels.</summary>
public readonly record struct PictureGrid(double Pitch, double PhaseX, double PhaseY);

public static class PictureGrids
{
    /// <summary>The shortest pitch looked for, pixels: below it a grid is texture, not a drawing grid.</summary>
    private const double MinPitch = 8;

    /// <summary>How far the strongest period must stand above the typical one to be a grid at all.</summary>
    private const double Prominence = 6;

    /// <summary>A pixel further than this from the paper's brightness is ink, not grid.</summary>
    private const int InkContrast = 48;

    /// <summary>The picture's grid, or null when it shows none.</summary>
    public static PictureGrid? Detect(RasterImage raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        int W = raster.Width, H = raster.Height;
        if (W < 4 * MinPitch || H < 4 * MinPitch) return null;
        // Only the paper is profiled: ink — wires, symbols, words — differs from it by far more than a grid line does,
        // and left in it swamps the grid's comb with the drawing's own texture.
        var rgba = raster.Rgba;
        var lum = new byte[W * H];
        var histogram = new int[256];
        for (int i = 0; i < lum.Length; i++)
        {
            lum[i] = (byte)Math.Round(0.299 * rgba[4 * i] + 0.587 * rgba[4 * i + 1] + 0.114 * rgba[4 * i + 2]);
            histogram[lum[i]]++;
        }
        int paper = 0;
        for (int v = 0, seen = 0; v < 256; v++) if ((seen += histogram[v]) * 2 >= lum.Length) { paper = v; break; }
        var cols = new double[W];
        var rows = new double[H];
        var colN = new int[W];
        var rowN = new int[H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int d = Math.Abs(lum[y * W + x] - paper);
                if (d > InkContrast) continue;
                cols[x] += d; colN[x]++;
                rows[y] += d; rowN[y]++;
            }
        for (int x = 0; x < W; x++) cols[x] = colN[x] > 0 ? cols[x] / colN[x] : 0;
        for (int y = 0; y < H; y++) rows[y] = rowN[y] > 0 ? rows[y] / rowN[y] : 0;
        var hx = HighPass(cols);
        var hy = HighPass(rows);

        double maxP = Math.Min(W, H) / 4.0;
        var periods = new List<double>();
        for (double p = MinPitch; p <= maxP; p += 0.25) periods.Add(p);
        if (periods.Count < 8) return null;
        var strength = periods.Select(p => Strength(hx, p).Abs + Strength(hy, p).Abs).ToArray();
        double strongest = strength.Max();
        var sorted = strength.Order().ToArray();
        double typical = sorted[sorted.Length / 2];
        if (!(strongest > Prominence * typical) || strongest <= 1e-9) return null;

        int k = Array.FindLastIndex(strength, s => s >= 0.5 * strongest);
        // That is a point on the fundamental's lobe, which is several pixels wide: climb to its top.
        while (true)
        {
            if (k + 1 < strength.Length && strength[k + 1] > strength[k]) k++;
            else if (k > 0 && strength[k - 1] > strength[k]) k--;
            else break;
        }
        // The coarse step found the peak's neighbourhood; the pitch is refined to a hundredth of a pixel, because its
        // error is multiplied by the number of lines across the picture.
        double best = periods[k], bestS = strength[k];
        for (double p = Math.Max(MinPitch, periods[k] - 0.5); p <= periods[k] + 0.5; p += 0.01)
            if (Strength(hx, p).Abs + Strength(hy, p).Abs is var s && s > bestS) { bestS = s; best = p; }
        return new PictureGrid(best, Phase(hx, best), Phase(hy, best));
    }

    /// <summary>|profile − its running median|: a thin line of either polarity a bump, an area flat.</summary>
    private static double[] HighPass(double[] v)
    {
        const int half = 6;
        var r = new double[v.Length];
        var window = new List<double>(2 * half + 1);
        for (int i = 0; i < v.Length; i++)
        {
            window.Clear();
            for (int j = Math.Max(0, i - half); j <= Math.Min(v.Length - 1, i + half); j++) window.Add(v[j]);
            window.Sort();
            r[i] = Math.Abs(v[i] - window[window.Count / 2]);
        }
        return r;
    }

    private static (double Re, double Im, double Abs) Strength(double[] h, double p)
    {
        double re = 0, im = 0, w = 2 * Math.PI / p;
        for (int i = 0; i < h.Length; i++)
        {
            re += h[i] * Math.Cos(w * (i + 0.5));
            im -= h[i] * Math.Sin(w * (i + 0.5));
        }
        return (re, im, Math.Sqrt(re * re + im * im));
    }

    /// <summary>Where the lines are: the comb's phase, in pixels from 0, within one pitch.</summary>
    private static double Phase(double[] h, double p)
    {
        var (re, im, _) = Strength(h, p);
        double phase = -Math.Atan2(im, re) / (2 * Math.PI) * p;
        return ((phase % p) + p) % p;
    }
}
