// brief-sar-11 R-sar11-2 — a voxel-index frame onto the world's axes: the ONE place a NIfTI affine (and a body's own quarter
// turns) becomes a world-aligned LabelVolume.
//
// THE FDTD GRID IS AXIS-ALIGNED, so a voxel axis must lie along a world axis. A permutation and a flip are both fine — they are
// how the common orientations are stored — and are applied by moving voxels, exactly. An axis that is turned off every world
// axis is REFUSED with the angle, never resampled: resampling a label map onto a turned grid is a decision about anatomy that
// belongs to whoever made the segmentation. The tolerance is 1e-6 on the direction cosine (about 0.08°), which a file written
// with a float32 affine of an axis-aligned volume clears and a deliberate oblique acquisition does not.
//
// THE AFFINE MAPS A VOXEL INDEX TO ITS CENTRE (NIfTI's convention), so the boundaries are the centres ± half a voxel.

using System.Globalization;

namespace CircuitRF.Design.ThreeD.Import;

public static class VoxelAxes
{
    /// <summary>The largest departure of a voxel axis from a world axis that is still that world axis: 1 − cos θ.</summary>
    public const double AxisTolerance = 1e-6;

    private static readonly string[] Names = ["x", "y", "z"];

    /// <summary>
    /// For each voxel axis (the affine's columns 0, 1, 2), the world axis it lies along and its sign — or the refusal, naming the
    /// axis and its angle off the nearest world axis. <paramref name="affine"/> is 3 × 4: voxel index → world (its last column
    /// the centre of voxel (0, 0, 0)).
    /// </summary>
    public static ((int World, int Sign, double Size)[]? Axes, string? Refusal) Of(double[,] affine, string what)
    {
        var axes = new (int World, int Sign, double Size)[3];
        var taken = new bool[3];
        for (int c = 0; c < 3; c++)
        {
            double vx = affine[0, c], vy = affine[1, c], vz = affine[2, c];
            double len = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            if (!(len > 0) || double.IsInfinity(len))
                return (null, $"{what}'s voxel axis {c + 1} has no length in its affine, so its voxel size is unknown.");
            double[] a = [Math.Abs(vx), Math.Abs(vy), Math.Abs(vz)];
            int w = a[0] >= a[1] && a[0] >= a[2] ? 0 : a[1] >= a[2] ? 1 : 2;
            double cos = a[w] / len;
            if (cos < 1 - AxisTolerance)
            {
                double deg = Math.Acos(Math.Min(1, cos)) * 180 / Math.PI;
                return (null, string.Create(CultureInfo.InvariantCulture,
                    $"{what}'s voxel axis {c + 1} is turned {deg:0.###}° off the world's {Names[w]} axis, and the FDTD grid is axis-aligned.") +
                    " Resample the volume onto the world's axes first; circuitRF does not turn a label map.");
            }
            if (taken[w]) return (null, $"{what}'s affine puts two voxel axes along the world's {Names[w]} axis.");
            taken[w] = true;
            axes[c] = (w, (w == 0 ? vx : w == 1 ? vy : vz) > 0 ? 1 : -1, len);
        }
        return (axes, null);
    }

    /// <summary>
    /// <paramref name="data"/> (voxel index i fastest, then j, then k: NIfTI's and a raw file's order) of size
    /// <paramref name="n"/> placed by <paramref name="affine"/>, as a world-aligned volume — or the refusal of <see cref="Of"/>.
    /// </summary>
    public static LabelVolume Align(int[] n, ushort[] data, double[,] affine, string what, IReadOnlyDictionary<int, string>? names = null)
    {
        var (axes, refusal) = Of(affine, what);
        if (refusal is not null) throw new LabelVolumeException(AnatomyDiagnostics.Volume(refusal));
        var size = new int[3];
        var lines = new double[3][];
        for (int c = 0; c < 3; c++)
        {
            var (w, sign, len) = axes![c];
            size[w] = n[c];
            double centre0 = affine[w, 3];
            double lowestCentre = sign > 0 ? centre0 : centre0 - len * (n[c] - 1);
            lines[w] = [.. Enumerable.Range(0, n[c] + 1).Select(m => lowestCentre - len / 2 + len * m)];
        }
        int ny = size[1], nz = size[2];
        var labels = new ushort[data.LongLength];
        // the world index of each voxel index along each voxel axis
        var at = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            var (_, sign, _) = axes![c];
            at[c] = [.. Enumerable.Range(0, n[c]).Select(m => sign > 0 ? m : n[c] - 1 - m)];
        }
        int wi = axes![0].World, wj = axes[1].World, wk = axes[2].World;
        Parallel.For(0, n[2], k =>
        {
            var w = new int[3];
            w[wk] = at[2][k];
            for (int j = 0; j < n[1]; j++)
            {
                w[wj] = at[1][j];
                long src = ((long)k * n[1] + j) * n[0];
                for (int i = 0; i < n[0]; i++)
                {
                    w[wi] = at[0][i];
                    labels[((long)w[0] * ny + w[1]) * nz + w[2]] = data[src + i];
                }
            }
        });
        return new LabelVolume(lines[0], lines[1], lines[2], labels, names);
    }
}
