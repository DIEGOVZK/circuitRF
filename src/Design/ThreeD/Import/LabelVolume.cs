// brief-sar-11 R-sar11-1/-2 — a segmented label map as circuitRF holds it once a reader is done with it: one integer label per
// voxel, voxel boundaries along the WORLD's axes in metres, and whatever names the source gave its labels.
//
// EVERY READER LANDS HERE, ALIGNED. A NIfTI file's affine may permute and flip its axes (the commonest orientations do both); a
// raw volume and a DiscMaterial file are aligned already. VoxelAxes is the one place that turns a voxel-index frame into this
// world-aligned form, so a body placed by a quarter turn and a file stored in another orientation go through the same code.
//
// LABELS ARE ushort. 0 … 65,535 covers every atlas in use, and a whole body at 1 mm (~270 M voxels) is half a gigabyte in it
// where int would be a whole one. A label outside that range is a refusal naming it, never a wrap.
//
// C ORDER OVER (x, y, z), z fastest — circuitRF's order everywhere (OpenEmsArrayOrder), so the DiscMaterial written from it is
// one permutation away and the SAR engine's arrays line up with it.

using System.Globalization;

namespace CircuitRF.Design.ThreeD.Import;

/// <summary>A label map on world-aligned voxel boundaries (metres). <see cref="Labels"/> is C order over (x, y, z).</summary>
public sealed class LabelVolume
{
    public LabelVolume(double[] x, double[] y, double[] z, ushort[] labels, IReadOnlyDictionary<int, string>? names = null)
    {
        foreach (var (lines, axis) in new[] { (x, "x"), (y, "y"), (z, "z") })
        {
            if (lines.Length < 2) throw new LabelVolumeException(AnatomyDiagnostics.Volume($"The volume has no voxel along {axis}."));
            for (int i = 1; i < lines.Length; i++)
                if (!(lines[i] > lines[i - 1])) throw new LabelVolumeException(AnatomyDiagnostics.Volume($"The volume's voxel boundaries along {axis} do not increase at line {i}."));
        }
        if ((long)(x.Length - 1) * (y.Length - 1) * (z.Length - 1) != labels.LongLength)
            throw new LabelVolumeException(AnatomyDiagnostics.Volume($"The volume holds {labels.LongLength:N0} labels for {x.Length - 1} × {y.Length - 1} × {z.Length - 1} voxels."));
        X = x; Y = y; Z = z; Labels = labels;
        Names = names ?? new Dictionary<int, string>();
    }

    public double[] X { get; }
    public double[] Y { get; }
    public double[] Z { get; }
    public ushort[] Labels { get; }

    /// <summary>The names the source gave its labels (a DiscMaterial file's entries, a raw volume's CSV); empty when it gave none.</summary>
    public IReadOnlyDictionary<int, string> Names { get; }

    public int Nx => X.Length - 1;
    public int Ny => Y.Length - 1;
    public int Nz => Z.Length - 1;
    public long Count => Labels.LongLength;

    public long Index(int i, int j, int k) => ((long)i * Ny + j) * Nz + k;

    /// <summary>The voxel size along each axis when it is uniform (to 1e-6), metres; null on an axis whose voxels differ.</summary>
    public (double? X, double? Y, double? Z) Spacing => (Uniform(X), Uniform(Y), Uniform(Z));

    private static double? Uniform(double[] lines)
    {
        double d = (lines[^1] - lines[0]) / (lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
            if (Math.Abs(lines[i] - lines[i - 1] - d) > 1e-6 * d) return null;
        return d;
    }

    /// <summary>How many voxels carry each label, over the voxel range [lo, hi) (the whole volume when null).</summary>
    public SortedDictionary<int, long> Histogram((int I0, int J0, int K0, int I1, int J1, int K1)? range = null)
    {
        var (i0, j0, k0, i1, j1, k1) = range ?? (0, 0, 0, Nx, Ny, Nz);
        var counts = new long[65536];
        for (int i = i0; i < i1; i++)
            for (int j = j0; j < j1; j++)
            {
                long row = Index(i, j, 0);
                for (int k = k0; k < k1; k++) counts[Labels[row + k]]++;
            }
        var h = new SortedDictionary<int, long>();
        for (int l = 0; l < counts.Length; l++) if (counts[l] > 0) h[l] = counts[l];
        return h;
    }

    /// <summary>The smallest voxel range holding every voxel whose label is not in <paramref name="air"/>, inside
    /// [lo, hi); null when there is none.</summary>
    public (int I0, int J0, int K0, int I1, int J1, int K1)? Occupied(Func<int, bool> air, (int I0, int J0, int K0, int I1, int J1, int K1) within)
    {
        int i0 = int.MaxValue, j0 = int.MaxValue, k0 = int.MaxValue, i1 = -1, j1 = -1, k1 = -1;
        for (int i = within.I0; i < within.I1; i++)
            for (int j = within.J0; j < within.J1; j++)
            {
                long row = Index(i, j, 0);
                for (int k = within.K0; k < within.K1; k++)
                {
                    if (air(Labels[row + k])) continue;
                    if (i < i0) i0 = i; if (i > i1) i1 = i;
                    if (j < j0) j0 = j; if (j > j1) j1 = j;
                    if (k < k0) k0 = k; if (k > k1) k1 = k;
                }
            }
        return i1 < 0 ? null : (i0, j0, k0, i1 + 1, j1 + 1, k1 + 1);
    }

    /// <summary>A size as "a × b × c".</summary>
    public string Dimensions => string.Create(CultureInfo.InvariantCulture, $"{Nx} × {Ny} × {Nz}");
}
