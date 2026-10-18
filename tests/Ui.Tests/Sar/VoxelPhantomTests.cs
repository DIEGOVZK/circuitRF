// ================================================================
//  VoxelPhantomTests.cs — brief-sar-8 R-sar8-1's resolution: a C3dPhantom with a Voxel size is given to openEMS as ONE CSXCAD
//  DiscMaterial of its own voxels (CsxcadWriter.Voxels), each voxel the innermost layer holding its centre — the rule openEMS's
//  bundled phantoms are made by. Writer output only: nothing here runs openEMS (LoopPhantomVoxelTests has openEMS load the
//  replication's file and holds it to the tutorial's material grid, cell for cell).
// ================================================================

using CircuitRF.Design.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Tests.Em3d;
using PureHDF;
using System.Xml.Linq;
using Xunit;

namespace CircuitRF.Ui.Tests.Sar;

public sealed class VoxelPhantomTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar8-voxel-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>
    /// The head phantom (semi-axes 100, 80, 100 mm, shells 9 and 9 mm) at 2.5 mm voxels, 120 mm along x: one DiscMaterial under
    /// every other object and no layer solid; its file's voxels are exactly the nested-ellipsoid test at each voxel's centre,
    /// evaluated here from the semi-axes alone, on a lattice with a corner at the centre; each entry carries its layer's εr and
    /// σ. The key round-trips, and Flatten is refused, since its layers would be smooth solids.
    /// </summary>
    [Fact]
    public void AVoxelizedHead_IsOneDiscMaterial_OfTheNestedEllipsoidsAtEachVoxelsCentre()
    {
        var (path, cws) = SarFixtures.Workspace(_root);
        long mm = SarFixtures.Mm;
        var doc = SarFixtures.Dipole();
        var head = C3dPhantom.New(C3dPhantomShape.Ellipsoid3, "head", new C3dPoint3(120 * mm, 0, 0), 1000);
        head.Voxel = 5 * mm / 2;
        doc.Objects.Add(head);
        var (problem, low) = SarFixtures.Lower(doc, path, cws);

        var xml = XDocument.Parse(low.Model!);
        Assert.DoesNotContain(xml.Descendants("Material"), m => m.Attribute("Name")!.Value.StartsWith("head/", StringComparison.Ordinal));
        var disc = Assert.Single(xml.Descendants("DiscMaterial"));
        Assert.Equal("0", disc.Descendants("Box").Single().Attribute("Priority")!.Value);
        var file = disc.Element("DiscFile")!;
        Assert.Equal((CsxcadWriter.VoxelFile("head"), "1", "1"), (file.Attribute("File")!.Value, file.Attribute("UseDBBackground")!.Value, file.Attribute("Scale")!.Value));
        var kernel = Assert.Single(low.KernelFiles!, f => f.FileName == CsxcadWriter.VoxelFile("head"));
        Assert.All(problem.Solids.Where(s => s.Name.StartsWith("head/", StringComparison.Ordinal)), s => Assert.Equal(0.0025, s.Voxel!.PitchM, 15));

        Directory.CreateDirectory(_root);
        string h5 = Path.Combine(_root, kernel.FileName);
        File.WriteAllBytes(h5, kernel.Bytes);
        using var f = H5File.OpenRead(h5);
        var data = f.Dataset("DiscData");
        byte[] voxels = data.Read<byte[]>();
        float[] x = f.Dataset("mesh/x").Read<float[]>(), y = f.Dataset("mesh/y").Read<float[]>(), z = f.Dataset("mesh/z").Read<float[]>();
        Assert.Equal((81, 65, 81), (x.Length, y.Length, z.Length));                   // ±100, ±80, ±100 mm at 2.5 mm
        Assert.Equal(0.12 - 0.1, x[0], 7);
        Assert.Equal(0.12 + 0.1, x[^1], 7);
        Assert.Equal([1f, 38.9f, 11.4f, 52.7f], data.Attribute("epsR").Read<float[]>());
        Assert.Equal([0f, 0.84f, 0.29f, 0.95f], data.Attribute("kappa").Read<float[]>());

        int nx = x.Length - 1, ny = y.Length - 1, nz = z.Length - 1, inside = 0;
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    double cx = (x[i] + x[i + 1]) / 2 - 0.12, cy = (y[j] + y[j + 1]) / 2, cz = (z[k] + z[k + 1]) / 2;
                    double R(double a, double b, double c) => cx * cx / (a * a) + cy * cy / (b * b) + cz * cz / (c * c);
                    byte want = R(0.082, 0.062, 0.082) <= 1 ? (byte)3 : R(0.091, 0.071, 0.091) <= 1 ? (byte)2 : R(0.1, 0.08, 0.1) <= 1 ? (byte)1 : (byte)0;
                    Assert.Equal(want, voxels[(k * ny + j) * nx + i]);                // CSXCAD's order: x fastest
                    if (want > 0) inside++;
                }
        Assert.True(inside > 100_000, $"{inside} tissue voxels");

        var back = C3dPersistence.Deserialize(C3dPersistence.Serialize(doc));
        Assert.Equal(5 * mm / 2, back.Objects.OfType<C3dPhantom>().Single().Voxel);
        Assert.DoesNotContain("\"Voxel\"", C3dPersistence.Serialize(SarFixtures.Dipole()), StringComparison.Ordinal);
        var (made, refusal) = C3dPhantoms.Flatten(back, back.Objects.FindIndex(o => o is C3dPhantom));
        Assert.Empty(made);
        Assert.Contains("Voxel to 0", refusal);
    }
}
