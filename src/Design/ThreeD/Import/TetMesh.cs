// brief-sar-11 R-sar11-4 — a tetrahedral volume mesh (.node / .ele, the plain-text pair tetrahedral meshers write) VOXELISED to
// a label volume at a chosen size, which is then a voxel body like any other. openEMS has no tetrahedral material, so the
// mesh's cells are sampled exactly as CSXCAD samples a DiscMaterial: each voxel takes the tetrahedron holding its CENTRE.
//
// FORMAT. .node: "<points> <dimension 3> <attributes> <boundary marker 0|1>", then "<index> x y z [attributes] [marker]".
// .ele: "<tetrahedra> <nodes per tetrahedron 4|10> <region attribute 0|1>", then "<index> n1 n2 n3 n4 [mid-edge nodes] [region]".
// '#' starts a comment. The first index in each file sets its base (0 or 1). A 10-node (quadratic) element is voxelised by its
// four corners. The region attribute is the label; an element without one is label 1. Region 0 is air unless the label table
// maps it, the same rule as every other label volume.
//
// THE LATTICE has a corner on the mesh's lowest corner and steps of the chosen size along the world's axes, out to cover the
// mesh. A voxel whose centre lies in no tetrahedron is air (label 0).

using System.Globalization;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD.Import;

/// <summary>A tetrahedral mesh in its files' units: nodes, and each tetrahedron's four corners and region label.</summary>
public sealed record TetMesh(Point3[] Nodes, (int A, int B, int C, int D, int Region)[] Tets)
{
    public static bool IsTetMesh(string path) => Path.GetExtension(path).ToLowerInvariant() is ".node" or ".ele";

    /// <summary>The pair named by either of its files (<c>body.node</c> or <c>body.ele</c>).</summary>
    public static TetMesh Read(string path)
    {
        string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path));
        string node = stem + ".node", ele = stem + ".ele";
        foreach (var f in new[] { node, ele })
            if (!File.Exists(f)) throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"A tetrahedral mesh is a .node and an .ele file side by side; '{Path.GetFileName(f)}' is not there."));
        var nodeRows = Rows(node);
        if (nodeRows.Count == 0) throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"'{Path.GetFileName(node)}' is empty."));
        int points = I(nodeRows[0][0]), dimension = nodeRows[0].Length > 1 ? I(nodeRows[0][1]) : 3;
        if (dimension != 3) throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"'{Path.GetFileName(node)}' is a {dimension}D mesh; a volume mesh is 3D."));
        if (nodeRows.Count - 1 < points) throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"'{Path.GetFileName(node)}' says {points} points and lists {nodeRows.Count - 1}."));
        int nodeBase = points > 0 ? I(nodeRows[1][0]) : 0;
        var nodes = new Point3[points];
        for (int k = 0; k < points; k++)
        {
            var r = nodeRows[k + 1];
            int at = I(r[0]) - nodeBase;
            if (at < 0 || at >= points) throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"'{Path.GetFileName(node)}' numbers a point {r[0]}, outside its {points}."));
            nodes[at] = new Point3(D(r[1]), D(r[2]), D(r[3]));
        }

        var eleRows = Rows(ele);
        if (eleRows.Count == 0) throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"'{Path.GetFileName(ele)}' is empty."));
        int tets = I(eleRows[0][0]), per = eleRows[0].Length > 1 ? I(eleRows[0][1]) : 4;
        bool region = eleRows[0].Length > 2 && I(eleRows[0][2]) != 0;
        if (per is not (4 or 10)) throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"'{Path.GetFileName(ele)}' has {per} nodes per element; a tetrahedron has 4 (or 10)."));
        if (eleRows.Count - 1 < tets) throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"'{Path.GetFileName(ele)}' says {tets} tetrahedra and lists {eleRows.Count - 1}."));
        var list = new (int, int, int, int, int)[tets];
        for (int k = 0; k < tets; k++)
        {
            var r = eleRows[k + 1];
            int N(int c)
            {
                int i = I(r[c]) - nodeBase;
                return i >= 0 && i < points ? i : throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"'{Path.GetFileName(ele)}' element {r[0]} names point {r[c]}, which is not in the .node file."));
            }
            int label = region && r.Length > 1 + per ? (int)Math.Round(D(r[1 + per])) : 1;
            if (label is < 0 or > ushort.MaxValue) throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"'{Path.GetFileName(ele)}' element {r[0]} is region {label}, outside 0 to 65,535."));
            list[k] = (N(1), N(2), N(3), N(4), label);
        }
        return new TetMesh(nodes, list);
    }

    /// <summary>The mesh voxelised at <paramref name="voxelM"/> (its coordinates times <paramref name="unitM"/> are metres): each
    /// voxel the region of the tetrahedron holding its centre, air where none does.</summary>
    public LabelVolume Voxelise(double voxelM, double unitM)
    {
        if (!(voxelM > 0)) throw new LabelVolumeException(AnatomyDiagnostics.Option("The voxel size a tetrahedral mesh is voxelised at must be positive."));
        var p = Nodes.Select(q => new Point3(q.X * unitM, q.Y * unitM, q.Z * unitM)).ToArray();
        double x0 = p.Min(q => q.X), y0 = p.Min(q => q.Y), z0 = p.Min(q => q.Z);
        int Count(double lo, double hi) => Math.Max(1, (int)Math.Ceiling((hi - lo) / voxelM - 1e-9));
        int nx = Count(x0, p.Max(q => q.X)), ny = Count(y0, p.Max(q => q.Y)), nz = Count(z0, p.Max(q => q.Z));
        if ((long)nx * ny * nz > int.MaxValue)
            throw new LabelVolumeException(AnatomyDiagnostics.Option($"The mesh would be {(long)nx * ny * nz:N0} voxels at {voxelM * 1e3:0.###} mm; choose a larger voxel."));
        double[] Lines(double lo, int n) => [.. Enumerable.Range(0, n + 1).Select(k => lo + k * voxelM)];
        double[] X = Lines(x0, nx), Y = Lines(y0, ny), Z = Lines(z0, nz);
        var labels = new ushort[(long)nx * ny * nz];
        foreach (var (a, b, c, d, region) in Tets)
        {
            Point3 A = p[a], B = p[b], C = p[c], Dd = p[d];
            int Lo(double v, double lo) => Math.Max(0, (int)Math.Floor((v - lo) / voxelM - 0.5));
            int Hi(double v, double lo, int n) => Math.Min(n - 1, (int)Math.Ceiling((v - lo) / voxelM - 0.5));
            int i0 = Lo(Math.Min(Math.Min(A.X, B.X), Math.Min(C.X, Dd.X)), x0), i1 = Hi(Math.Max(Math.Max(A.X, B.X), Math.Max(C.X, Dd.X)), x0, nx);
            int j0 = Lo(Math.Min(Math.Min(A.Y, B.Y), Math.Min(C.Y, Dd.Y)), y0), j1 = Hi(Math.Max(Math.Max(A.Y, B.Y), Math.Max(C.Y, Dd.Y)), y0, ny);
            int k0 = Lo(Math.Min(Math.Min(A.Z, B.Z), Math.Min(C.Z, Dd.Z)), z0), k1 = Hi(Math.Max(Math.Max(A.Z, B.Z), Math.Max(C.Z, Dd.Z)), z0, nz);
            double vol = Det(B, C, Dd, A);
            if (vol == 0) continue;
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                    for (int k = k0; k <= k1; k++)
                    {
                        var q = new Point3((X[i] + X[i + 1]) / 2, (Y[j] + Y[j + 1]) / 2, (Z[k] + Z[k + 1]) / 2);
                        // barycentric signs: inside (or on a face) when every sub-volume has the tetrahedron's sign
                        double s0 = Det(B, C, Dd, q) / vol, s1 = Det(A, Dd, C, q) / vol, s2 = Det(A, B, Dd, q) / vol, s3 = Det(A, C, B, q) / vol;
                        if (s0 >= 0 && s1 >= 0 && s2 >= 0 && s3 >= 0) labels[((long)i * ny + j) * nz + k] = (ushort)region;
                    }
        }
        return new LabelVolume(X, Y, Z, labels);
    }

    /// <summary>Six times the signed volume of the tetrahedron (a, b, c, q) — positive when q is on the side a × b × c faces.</summary>
    private static double Det(Point3 a, Point3 b, Point3 c, Point3 q)
    {
        double ax = a.X - q.X, ay = a.Y - q.Y, az = a.Z - q.Z, bx = b.X - q.X, by = b.Y - q.Y, bz = b.Z - q.Z, cx = c.X - q.X, cy = c.Y - q.Y, cz = c.Z - q.Z;
        return ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx);
    }

    private static List<string[]> Rows(string path)
        => [.. File.ReadLines(path).Select(l => { int h = l.IndexOf('#'); return (h >= 0 ? l[..h] : l).Trim(); })
                                   .Where(l => l.Length > 0).Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))];

    private static int I(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
