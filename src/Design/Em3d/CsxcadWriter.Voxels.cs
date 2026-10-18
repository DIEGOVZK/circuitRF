// brief-sar-8 — a voxelized phantom (C3dPhantom.Voxel) as ONE CSXCAD DiscMaterial, the way openEMS's own MRI phantoms are given
// to it: a uint8 tissue index per voxel in an HDF5 file (SAR-4's DiscMaterialWriter), read by CSXCAD at each FDTD cell's
// centre under CellConstantMaterial. docs/design/sar.md §10.2.
//
//   * THE LATTICE has a corner on the phantom's centre and runs along the world's axes (Em3dVoxelLattice); the file covers the
//     layers' bounds out to the lattice's planes. A voxel is the innermost layer — the highest construction order — whose shape
//     holds the voxel's CENTRE: create_phantoms.py's rule, so a phantom of the same dimensions and pitch is the same voxels.
//   * ENTRY 0 IS AIR, and the box claims every cell inside it (CSXCAD gives a voxel holding 0 the property's own material, and
//     UseDBBackground makes that entry 0's values: εr 1, σ 0, ρ 0). So the box is written at priority 0, under every other
//     object: anything that overlaps the phantom's box takes the volume they share, which the run says.
//   * A VOXEL CARRIES εr, σ AND ρ ONLY (the file's three tables): a layer with a tensor permittivity or μr ≠ 1 is refused.
//   * The file goes to every port's directory as a kernel file does, and is checked by hash before openEMS starts, since CSXCAD
//     reads a missing or damaged DiscMaterial file as nothing at all.

using System.Text;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using RfCore.Hdf5;

namespace CircuitRF.Design.Em3d;

public static partial class CsxcadWriter
{
    /// <summary>The DiscMaterial file a voxelized phantom is written to, beside model.xml in each port's directory.</summary>
    public static string VoxelFile(string phantom)
        => "voxels_" + new string([.. phantom.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]) + ".h5";

    /// <summary>
    /// The DiscMaterial property for <paramref name="layers"/> — one voxelized phantom's solids — and its file; or the refusal.
    /// <paramref name="densities"/> are the SAR plan's (null without one): a layer is written with its density only when it is
    /// SAR's tissue, as an ordinary solid is.
    /// </summary>
    private static (string? Xml, Em3dKernelFile? File, string? Refusal) VoxelPhantom(
        Em3dVoxelLattice lattice, IReadOnlyList<Em3dSolid> layers, IReadOnlyDictionary<string, Em3dMaterial> materials, double fitHz,
        IReadOnlyDictionary<string, double>? densities, int id)
    {
        var tissues = new List<DiscTissue> { new("air", 1, 0, 0) };
        foreach (var s in layers)
        {
            var m = materials[s.Material];
            if (m.EpsrTensor is not null || m.Mur != 1)
                return (null, null, $"The phantom '{lattice.Phantom}' is voxelized, and its layer '{s.Name}' is made of '{m.Name}', whose " +
                                    $"{(m.EpsrTensor is not null ? "permittivity is a tensor" : $"μr is {G(m.Mur)}")}: a voxel carries εr, σ " +
                                    "and density only. Set the phantom's Voxel to 0 to give openEMS its smooth layers.");
            tissues.Add(new DiscTissue(s.Name.Replace(',', ' '), (float)m.Epsr, (float)Kappa(m, m.Epsr, fitHz),
                                       (float)(densities?.GetValueOrDefault(s.Name) ?? 0)));
        }

        // the layers' bounds, out to the lattice's planes
        double p = lattice.PitchM;
        var bounds = layers.Select(s => Em3dProblem.Bounds(s.Primitive)).ToList();
        double[] Lines(double lo, double hi, double anchor)
        {
            long k0 = (long)Math.Floor((lo - anchor) / p + 1e-9), k1 = (long)Math.Ceiling((hi - anchor) / p - 1e-9);
            return [.. Enumerable.Range(0, (int)(k1 - k0 + 1)).Select(k => anchor + (k0 + k) * p)];
        }
        double[] x = Lines(bounds.Min(b => b.X0), bounds.Max(b => b.X1), lattice.Anchor.X);
        double[] y = Lines(bounds.Min(b => b.Y0), bounds.Max(b => b.Y1), lattice.Anchor.Y);
        double[] z = Lines(bounds.Min(b => b.Z0), bounds.Max(b => b.Z1), lattice.Anchor.Z);
        int nx = x.Length - 1, ny = y.Length - 1, nz = z.Length - 1;
        if ((long)nx * ny * nz > int.MaxValue)
            return (null, null, $"The phantom '{lattice.Phantom}' would be {(long)nx * ny * nz:N0} voxels at {FdtdGrid.FormatLength(p)}, more than one " +
                                "file holds. A larger Voxel reduces it.");

        // each voxel: the innermost layer holding its centre (the highest construction order wins, as everywhere in the problem)
        var byOrder = layers.Select((s, i) => (s, Index: (byte)(i + 1))).OrderByDescending(t => t.s.Order).ToList();
        var indices = new byte[nx * ny * nz];
        Parallel.For(0, nx, i =>
        {
            double cx = (x[i] + x[i + 1]) / 2;
            for (int j = 0; j < ny; j++)
            {
                double cy = (y[j] + y[j + 1]) / 2;
                for (int k = 0; k < nz; k++)
                {
                    var c = new Point3(cx, cy, (z[k] + z[k + 1]) / 2);
                    foreach (var (s, index) in byOrder)
                        if (C3dThermal.Inside(s.Primitive, c, 0)) { indices[(i * ny + j) * nz + k] = index; break; }
                }
            }
        });

        string file = VoxelFile(lattice.Phantom);
        byte[] bytes = DiscMaterialWriter.ToBytes(x, y, z, indices, tissues);
        var sb = new StringBuilder();
        Open(sb, "DiscMaterial", id, lattice.Phantom, Colors.Dielectric, " Isotropy=\"1\"");
        AppendPrimitives(sb, Box(0, new Point3(x[0], y[0], z[0]), new Point3(x[^1], y[^1], z[^1])));
        sb.Append("                <Property Epsilon=\"1,1,1\" Mue=\"1,1,1\" Kappa=\"0,0,0\" Sigma=\"0,0,0\" Density=\"0\" />\n");
        sb.Append("                <Weight Epsilon=\"1,1,1\" Mue=\"1,1,1\" Kappa=\"1,1,1\" Sigma=\"1,1,1\" Density=\"1\" />\n");
        sb.Append($"                <DiscFile Type=\"0\" File=\"{Esc(file)}\" UseDBBackground=\"1\" Scale=\"1\" />\n");
        sb.Append("            </DiscMaterial>\n");
        return (sb.ToString(), new Em3dKernelFile(file, bytes, lattice.Phantom), null);
    }

    /// <summary>
    /// brief-sar-11 R-sar11-3 — the DiscMaterial property for one voxel body and its file: the body's own boundaries (already
    /// placed in the world, so no CSXCAD transform is needed and the file's lines are the lines the grid put on its voxels) and one
    /// entry per tissue, εr, σ and ρ evaluated at the SAR frequency as an ordinary solid's are. Entry 0 is air and the box is
    /// written at priority 0, exactly as a voxelized phantom's. Or the refusal (a voxel carries εr, σ and ρ only).
    /// </summary>
    private static (string? Xml, Em3dKernelFile? File, string? Refusal) VoxelBody(
        Em3dLabelVolume volume, IReadOnlyList<Em3dSolid> tissues, IReadOnlyDictionary<string, Em3dMaterial> materials, double fitHz,
        IReadOnlyDictionary<string, double>? densities, int id)
    {
        var entries = new List<DiscTissue> { new("air", 1, 0, 0) };
        var byEntry = tissues.ToDictionary(t => t.Label);
        for (int e = 1; e <= volume.Tissues.Count; e++)
        {
            if (!byEntry.TryGetValue(e, out var s))
            {
                // a tissue whose material was refused upstream never reaches here; one left out otherwise is air, said by the run
                entries.Add(new DiscTissue(volume.Tissues[e - 1].Replace(',', ' '), 1, 0, 0));
                continue;
            }
            var m = materials[s.Material];
            if (m.EpsrTensor is not null || m.Mur != 1)
                return (null, null, $"The voxel body '{volume.Body}' has the tissue '{s.Name}' made of '{m.Name}', whose " +
                                    $"{(m.EpsrTensor is not null ? "permittivity is a tensor" : $"μr is {G(m.Mur)}")}: a voxel carries εr, σ and density only.");
            entries.Add(new DiscTissue(s.Name.Replace(',', ' '), (float)m.Epsr, (float)Kappa(m, m.Epsr, fitHz),
                                       (float)(densities?.GetValueOrDefault(s.Name) ?? 0)));
        }
        string file = VoxelFile(volume.Body);
        byte[] bytes = DiscMaterialWriter.ToBytes(volume.X, volume.Y, volume.Z, volume.Entries, entries);
        var sb = new StringBuilder();
        Open(sb, "DiscMaterial", id, volume.Body, Colors.Dielectric, " Isotropy=\"1\"");
        AppendPrimitives(sb, Box(0, new Point3(volume.X[0], volume.Y[0], volume.Z[0]), new Point3(volume.X[^1], volume.Y[^1], volume.Z[^1])));
        sb.Append("                <Property Epsilon=\"1,1,1\" Mue=\"1,1,1\" Kappa=\"0,0,0\" Sigma=\"0,0,0\" Density=\"0\" />\n");
        sb.Append("                <Weight Epsilon=\"1,1,1\" Mue=\"1,1,1\" Kappa=\"1,1,1\" Sigma=\"1,1,1\" Density=\"1\" />\n");
        sb.Append($"                <DiscFile Type=\"0\" File=\"{Esc(file)}\" UseDBBackground=\"1\" Scale=\"1\" />\n");
        sb.Append("            </DiscMaterial>\n");
        return (sb.ToString(), new Em3dKernelFile(file, bytes, volume.Body), null);
    }
}
