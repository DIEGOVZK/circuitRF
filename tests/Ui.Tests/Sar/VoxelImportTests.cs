// ================================================================
//  VoxelImportTests.cs — the gates of brief-sar-11: segmented voxel models and per-tissue surface meshes in a .c3d, lowered to
//  openEMS. Every volume and surface here is SYNTHETIC, written by the test (R-sar11-6): nested ellipsoids, a 3-label slab, two
//  nested cubes, a tetrahedral cube. No anatomical model enters the repository. The one openEMS run is opt-in (Benchmark).
// ================================================================

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Import;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using RfCore.Hdf5;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Sar;

internal static class VoxelFixtures
{
    /// <summary>A NIfTI-1 file written byte by byte from the published header layout — independently of NiftiFile's writer:
    /// uint8 voxels in NIfTI's order (i fastest), the sform rows in millimetres, gzipped.</summary>
    public static byte[] Nifti1(int[] n, byte[] data, float[][] sform)
    {
        var b = new byte[352 + data.Length];
        var s = b.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, 348);
        short[] dim = [3, (short)n[0], (short)n[1], (short)n[2], 1, 1, 1, 1];
        for (int k = 0; k < 8; k++) BinaryPrimitives.WriteInt16LittleEndian(s[(40 + 2 * k)..], dim[k]);
        BinaryPrimitives.WriteInt16LittleEndian(s[70..], 2);              // uint8
        BinaryPrimitives.WriteInt16LittleEndian(s[72..], 8);
        for (int k = 0; k < 8; k++) BinaryPrimitives.WriteSingleLittleEndian(s[(76 + 4 * k)..], 1);
        BinaryPrimitives.WriteSingleLittleEndian(s[108..], 352);           // vox_offset
        b[123] = 2;                                                        // mm
        BinaryPrimitives.WriteInt16LittleEndian(s[254..], 1);              // sform_code
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++) BinaryPrimitives.WriteSingleLittleEndian(s[(280 + 16 * r + 4 * c)..], sform[r][c]);
        Encoding.ASCII.GetBytes("n+1\0").CopyTo(s[344..]);
        data.CopyTo(b, 352);
        using var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(b);
        return ms.ToArray();
    }

    /// <summary>A 3-label slab, 6 × 4 × 3 voxels of 2 mm along x, y, z from (0, 0, 0): label = 1 + (i / 2) (three slabs along x),
    /// except voxel (5, 3, 2), which carries label 7.</summary>
    public static (int[] N, byte[] Data) Slab(bool withStray)
    {
        int[] n = [6, 4, 3];
        var data = new byte[6 * 4 * 3];
        for (int k = 0; k < 3; k++)
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 6; i++) data[i + 6 * (j + 4 * k)] = (byte)(1 + i / 2);
        if (withStray) data[5 + 6 * (3 + 4 * 2)] = 7;
        return (n, data);
    }

    public static float[][] Axes(float d, float ox, float oy, float oz) => [[d, 0, 0, ox], [0, d, 0, oy], [0, 0, d, oz]];
}

public sealed class NiftiReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar11-nifti-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>R-sar11-2: a synthetic .nii.gz round-trips its dimensions, affine and labels — here stored with x FLIPPED (a
    /// negative sform column, as the common orientations store it), which the reader undoes by moving voxels; NiftiFile's own
    /// writer reads back to the same volume; and an affine turned 10° about z is refused, naming the angle.</summary>
    [Fact]
    public void ASyntheticNifti_RoundTripsDimensionsAffineAndLabels()
    {
        Directory.CreateDirectory(_root);
        var (n, data) = VoxelFixtures.Slab(withStray: true);
        string path = Path.Combine(_root, "slab.nii.gz");
        // x flipped: voxel i's centre at 11 − 2i mm, so the world's lowest x boundary is at 0 mm
        File.WriteAllBytes(path, VoxelFixtures.Nifti1(n, data, [[-2, 0, 0, 11], [0, 2, 0, 1], [0, 0, 2, 1]]));
        var read = NiftiFile.Read(path);
        var v = read.Volume;
        Assert.Equal((6, 4, 3), (v.Nx, v.Ny, v.Nz));
        Assert.Equal(-0.002, read.Affine[0, 0], 12);
        Assert.Equal(0.011, read.Affine[0, 3], 12);
        Assert.Equal([0, 0.002, 0.004, 0.006, 0.008, 0.010, 0.012], v.X.Select(x => Math.Round(x, 12)));
        Assert.Equal([0, 0.002, 0.004, 0.006, 0.008], v.Y.Select(y => Math.Round(y, 12)));
        for (int k = 0; k < 3; k++)
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 6; i++)
                    Assert.Equal(data[(5 - i) + 6 * (j + 4 * k)], v.Labels[v.Index(i, j, k)]);   // world i is file voxel 5 − i
        Assert.Equal(new SortedDictionary<int, long> { [1] = 24, [2] = 24, [3] = 23, [7] = 1 }, v.Histogram());

        string again = Path.Combine(_root, "again.nii.gz");
        NiftiFile.Write(again, v);
        var back = NiftiFile.Read(again).Volume;
        Assert.Equal(v.Labels, back.Labels);
        Assert.Equal(v.X.Select(x => Math.Round(x, 9)), back.X.Select(x => Math.Round(x, 9)));

        float c = MathF.Cos(MathF.PI / 18), s = MathF.Sin(MathF.PI / 18);
        string turned = Path.Combine(_root, "turned.nii.gz");
        File.WriteAllBytes(turned, VoxelFixtures.Nifti1(n, data, [[2 * c, -2 * s, 0, 0], [2 * s, 2 * c, 0, 0], [0, 0, 2, 0]]));
        var e = Assert.Throws<LabelVolumeException>(() => NiftiFile.Read(turned));
        Assert.Contains("turned 10° off the world's x axis", e.Message, StringComparison.Ordinal);
    }
}

public sealed class VoxelBodyCsxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar11-body-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>
    /// R-sar11-1/-3: a 3-label slab imported beside the dipole writes ONE DiscMaterial whose file SAR-4's reader reads back to
    /// exactly the slab — every voxel's entry, the boundaries (placed 40 mm along x), each entry's εr and σ — with a grid line on
    /// every voxel boundary, and the tissues named body/tissue. A label the table does not map is refused, naming it and its
    /// voxel count; mapping it as Air is accepted.
    /// </summary>
    [Fact]
    public void ASlab_WritesADiscMaterialThatReadsBackExactly_AndAnUnmappedLabelIsRefused()
    {
        var (path, cws) = SarFixtures.Workspace(_root);
        long mm = SarFixtures.Mm;
        var (n, data) = VoxelFixtures.Slab(withStray: true);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, "slab.nii.gz"), VoxelFixtures.Nifti1(n, data, VoxelFixtures.Axes(2, 1, 1, 1)));
        var doc = SarFixtures.Dipole();
        var body = new C3dVoxelBody
        {
            Name = "slab", File = "slab.nii.gz",
            Placement = new C3dPlacement { Origin = new C3dPoint3(40 * mm, 0, 0) },
            Labels =
            [
                new() { Label = 1, Name = "skin", Material = SarFixtures.HeadTissues[0] },
                new() { Label = 2, Name = "skull", Material = SarFixtures.HeadTissues[1] },
                new() { Label = 3, Name = "brain", Material = SarFixtures.HeadTissues[2] },
            ],
        };
        doc.Objects.Add(body);

        // label 7 is in no row: refused, with its count
        C3dPersistence.SaveToFile(path, doc);
        var refused = C3dProblemAssembly.Assemble(SarFixtures.Setup(), doc, path, cws, new C3dElaborator());
        Assert.False(refused.Ok);
        Assert.Contains("1 label its table maps to no material: 7 (1 voxel)", refused.Refusal, StringComparison.Ordinal);

        body.Labels.Add(new C3dVoxelLabel { Label = 7, Name = "stray", Air = true });
        var (problem, low) = SarFixtures.Lower(doc, path, cws);
        Assert.Equal(["slab/skin", "slab/skull", "slab/brain"], problem.Solids.Where(s => s.Labels is not null).Select(s => s.Name));

        var xml = XDocument.Parse(low.Model!);
        var disc = Assert.Single(xml.Descendants("DiscMaterial"));
        Assert.Equal("slab", disc.Attribute("Name")!.Value);
        var kernel = Assert.Single(low.KernelFiles!, f => f.FileName == CsxcadWriter.VoxelFile("slab"));
        Directory.CreateDirectory(_root);
        string h5 = Path.Combine(_root, kernel.FileName);
        File.WriteAllBytes(h5, kernel.Bytes);
        var file = OpenEmsHdf5.ReadDiscMaterial(h5);
        Assert.Equal((6, 4, 3), (file.Nx, file.Ny, file.Nz));
        Assert.Equal(Enumerable.Range(0, 7).Select(i => Math.Round(0.040 + 0.002 * i, 7)), file.X.Select(x => Math.Round(x, 7)));
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 3; k++)
                {
                    int want = i == 5 && j == 3 && k == 2 ? 0 : 1 + i / 2;
                    Assert.Equal(want, file.Indices[OpenEmsArrayOrder.Index(i, j, k, 4, 3)]);
                }
        Assert.Equal([1f, 38.9f, 11.4f, 52.7f], file.Tissues.Select(t => t.EpsR));
        Assert.Equal([0f, 0.84f, 0.29f, 0.95f], file.Tissues.Select(t => t.Kappa));

        // R-sar11-3: a grid line on every voxel boundary inside the body
        var grid = FdtdGrid.Build(problem, CemOpenEms.ResolveGrid(SarFixtures.Setup().OpenEms, path), long.MaxValue);
        var volume = problem.Solids.First(s => s.Labels is not null).Labels!;
        Assert.All(volume.X, x => Assert.Contains(grid.X.Lines, l => Math.Abs(l - x) < 1e-12));
        Assert.All(volume.Z, z => Assert.Contains(grid.Z.Lines, l => Math.Abs(l - z) < 1e-12));
    }
}

public sealed class VoxelVsPrimitivePhantomTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar11-phantom-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>The head phantom's three ellipsoids (semi-axes 100/80/100 mm, shells 9 and 9 mm) evaluated at the centre of each
    /// 2.5 mm voxel of a lattice cornered on their centre — the rule create_phantoms.py and C3dPhantom.Voxel both use — as a
    /// label map from −100 to 100, −80 to 80, −100 to 100 mm.</summary>
    private static byte[] Ellipsoids(out int[] n)
    {
        n = [80, 64, 80];
        var data = new byte[80 * 64 * 80];
        for (int k = 0; k < 80; k++)
            for (int j = 0; j < 64; j++)
                for (int i = 0; i < 80; i++)
                {
                    double x = -0.1 + 0.0025 * (i + 0.5), y = -0.08 + 0.0025 * (j + 0.5), z = -0.1 + 0.0025 * (k + 0.5);
                    double R(double a, double b, double c) => x * x / (a * a) + y * y / (b * b) + z * z / (c * c);
                    data[i + 80 * (j + 64 * k)] = R(0.082, 0.062, 0.082) <= 1 ? (byte)3 : R(0.091, 0.071, 0.091) <= 1 ? (byte)2 : R(0.1, 0.08, 0.1) <= 1 ? (byte)1 : (byte)0;
                }
        return data;
    }

    private static (C3dDocument Phantom, C3dDocument Imported) Pair(string dir)
    {
        long mm = SarFixtures.Mm;
        var data = Ellipsoids(out var n);
        File.WriteAllBytes(Path.Combine(dir, "head.nii.gz"), VoxelFixtures.Nifti1(n, data, VoxelFixtures.Axes(2.5f, -98.75f, -78.75f, -98.75f)));
        var phantom = SarFixtures.Dipole();
        var head = C3dPhantom.New(C3dPhantomShape.Ellipsoid3, "head", new C3dPoint3(120 * mm, 0, 0), 1000);
        head.Voxel = 5 * mm / 2;
        phantom.Objects.Add(head);
        var imported = SarFixtures.Dipole();
        imported.Objects.Add(new C3dVoxelBody
        {
            Name = "head", File = "head.nii.gz", Placement = new C3dPlacement { Origin = new C3dPoint3(120 * mm, 0, 0) },
            Labels = [.. head.Layers.Select((l, i) => new C3dVoxelLabel { Label = i + 1, Name = l.Name, Material = l.Material })],
        });
        return (phantom, imported);
    }

    /// <summary>
    /// R-sar11-3, routine on fixed data: the Ellipsoid3 phantom voxelised to 2.5 mm, and the same ellipsoids imported as a label
    /// volume, write the SAME DiscMaterial — the same boundaries and every voxel the same entry, with the same εr and σ per
    /// entry — so openEMS, sampling each cell at its centre, gives every cell of any grid the same tissue.
    /// </summary>
    [Fact]
    public void TheVoxelisedPhantom_AndItsImportedLabelVolume_WriteTheSameVoxels()
    {
        var (path, cws) = SarFixtures.Workspace(_root);
        var (phantom, imported) = Pair(Path.GetDirectoryName(path)!);
        var (_, a) = SarFixtures.Lower(phantom, path, cws);
        var (_, b) = SarFixtures.Lower(imported, path, cws);
        DiscMaterialFile Read(CsxcadLowering low)
        {
            var f = low.KernelFiles!.Single(k => k.FileName == CsxcadWriter.VoxelFile("head"));
            string h5 = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".h5");
            File.WriteAllBytes(h5, f.Bytes);
            return OpenEmsHdf5.ReadDiscMaterial(h5);
        }
        var p = Read(a);
        var v = Read(b);
        Assert.Equal((p.Nx, p.Ny, p.Nz), (v.Nx, v.Ny, v.Nz));
        Assert.Equal(p.X, v.X, (x, y) => Math.Abs(x - y) < 1e-7);
        Assert.Equal(p.Y, v.Y, (x, y) => Math.Abs(x - y) < 1e-7);
        Assert.Equal(p.Z, v.Z, (x, y) => Math.Abs(x - y) < 1e-7);
        Assert.Equal(p.Indices, v.Indices);
        Assert.Equal(p.Tissues.Select(t => (t.EpsR, t.Kappa)), v.Tissues.Select(t => (t.EpsR, t.Kappa)));
        Assert.True(v.Indices.Count(i => i > 0) > 100_000);
    }

    /// <summary>
    /// The same comparison through openEMS itself, opt-in: both setups lowered on ONE grid (the imported body's, given to the
    /// phantom's setup as explicit lines) and loaded by the installed openEMS for 20 steps with a raw SAR dump; the two dumps
    /// agree cell by cell in conductivity, density and E.
    /// </summary>
    [OpenEmsFact]
    [Trait("Category", "Benchmark")]
    public void OpenEms_GivesBothTheSameRawSarDump_CellByCell()
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(Path.Combine(ws, "Cell", "3d"));
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech",
            Materials =
            [
                new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 },
                new TechMaterial { Name = SarFixtures.HeadTissues[0], Epsr = 38.9, Sigma20 = 0.84, DensityKgM3 = 1010 },
                new TechMaterial { Name = SarFixtures.HeadTissues[1], Epsr = 11.4, Sigma20 = 0.29, DensityKgM3 = 1850 },
                new TechMaterial { Name = SarFixtures.HeadTissues[2], Epsr = 52.7, Sigma20 = 0.95, DensityKgM3 = 1040 },
            ],
        });
        string cws = Path.Combine(ws, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = "tech.ctech" });
        string path = Path.Combine(ws, "Cell", "3d", "Cell.c3d");
        var (phantom, imported) = Pair(Path.GetDirectoryName(path)!);
        var setup = SarSixFixtures.Setup(new CemSar { FrequenciesGHz = [1.0], Region = ["head"] });

        CsxcadLowering Lower(C3dDocument doc, EmSetup s)
        {
            C3dPersistence.SaveToFile(path, doc);
            var g = C3dProblemAssembly.Assemble(s, doc, path, cws, new C3dElaborator());
            Assert.True(g.Ok, g.Refusal);
            var (plan, refusal) = Em3dSar.Resolve(SarSettings.Resolve(s.Sar!), g.Problem!, g.Phantoms);
            Assert.True(plan is not null, refusal);
            var gs = CemOpenEms.ResolveGrid(s.OpenEms, path);
            var grid = FdtdGrid.Build(g.Problem!, gs, long.MaxValue);
            Assert.Null(grid.Refusal);
            var low = CsxcadWriter.Write(g.Problem!, grid, gs, CemOpenEms.ResolveRun(s.OpenEms, true) with { Sar = plan });
            Assert.True(low.Ok, low.Refusal);
            return low;
        }
        var b = Lower(imported, setup);
        var lines = XDocument.Parse(b.Model!).Descendants("RectilinearGrid").Single();
        List<double> Mm(string tag) => [.. lines.Element(tag)!.Value.Split(',').Select(t => SarFixtures.D(t) * 1e3)];
        var given = setup.Clone();
        given.OpenEms = new CemOpenEms { GridLines = new CemGridLines { Unit = "mm", X = Mm("XLines"), Y = Mm("YLines"), Z = Mm("ZLines") } };
        var a = Lower(phantom, given);

        OpenEmsSarRaw Run(CsxcadLowering low, string name)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "model.xml"), Regex.Replace(low.Model!, "NumberOfTimesteps=\"\\d+\"", "NumberOfTimesteps=\"20\""));
            foreach (var f in low.KernelFiles ?? []) f.WriteInto(dir);
            var psi = new ProcessStartInfo(SolverDiscovery.OpenEms.Find(out _)!.Path)
            {
                WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add("model.xml");
            using var proc = Process.Start(psi)!;
            var err = proc.StandardError.ReadToEndAsync();
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, err.Result);
            return OpenEmsHdf5.ReadSarRaw(Path.Combine(dir, Em3dSar.RawDumpFile(0)));
        }
        var ra = Run(a, "phantom");
        var rb = Run(b, "imported");
        // the given lines went through millimetres and back: the same lines to the last bit or two
        Assert.Equal(ra.Mesh.X, rb.Mesh.X, (x, y) => Math.Abs(x - y) < 1e-12);
        Assert.Equal(ra.Mesh.Y, rb.Mesh.Y, (x, y) => Math.Abs(x - y) < 1e-12);
        Assert.Equal(ra.Mesh.Z, rb.Mesh.Z, (x, y) => Math.Abs(x - y) < 1e-12);
        Assert.Equal(ra.Density, rb.Density);
        Assert.Equal(ra.Conductivity, rb.Conductivity);
        foreach (var (ea, eb) in new[] { (ra.Ex, rb.Ex), (ra.Ey, rb.Ey), (ra.Ez, rb.Ez) })
            Assert.Equal(ea.Select(e => (e.Re, e.Im)), eb.Select(e => (e.Re, e.Im)));
        Assert.True(rb.Density.Count(d => d > 0) > 100_000);
    }
}

public sealed class SurfaceAndTetImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar11-mesh-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>An axis-aligned cube's 12 triangles as ASCII STL, outward, from <paramref name="lo"/> to <paramref name="hi"/> mm.</summary>
    private static string Cube(double lo, double hi, bool open = false)
    {
        double[][] v = [[lo, lo, lo], [hi, lo, lo], [hi, hi, lo], [lo, hi, lo], [lo, lo, hi], [hi, lo, hi], [hi, hi, hi], [lo, hi, hi]];
        int[][] t = [[0, 2, 1], [0, 3, 2], [4, 5, 6], [4, 6, 7], [0, 1, 5], [0, 5, 4], [1, 2, 6], [1, 6, 5], [2, 3, 7], [2, 7, 6], [3, 0, 4], [3, 4, 7]];
        var sb = new StringBuilder("solid cube\n");
        foreach (var tri in open ? t[..^1] : t)
        {
            sb.Append("facet normal 0 0 0\nouter loop\n");
            foreach (int i in tri) sb.Append(FormattableString.Invariant($"vertex {v[i][0]} {v[i][1]} {v[i][2]}\n"));
            sb.Append("endloop\nendfacet\n");
        }
        return sb.Append("endsolid cube\n").ToString();
    }

    /// <summary>
    /// R-sar11-4: two nested closed cubes import outermost first (so the inner wins), each a Mesh whose material is matched by
    /// its file's name, and lower to openEMS through the PolyhedronReader PLY path; a surface with a hole is reported for its
    /// file and nothing is imported; and a tetrahedral cube (five tetrahedra, region 2) voxelises at 1 mm to a full 10 × 10 × 10
    /// block of label 2.
    /// </summary>
    [Fact]
    public void NestedSurfaces_ImportOutermostFirst_AndATetCubeVoxelises()
    {
        var (path, cws) = SarFixtures.Workspace(_root);
        string src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        string inner = Path.Combine(src, "brain.stl"), outer = Path.Combine(src, "skin.stl"), open = Path.Combine(src, "holed.stl");
        File.WriteAllText(inner, Cube(80, 120));
        File.WriteAllText(outer, Cube(60, 140));
        File.WriteAllText(open, Cube(0, 10, open: true));
        var materials = new Dictionary<string, string> { ["skin"] = SarFixtures.HeadTissues[0], ["brain"] = SarFixtures.HeadTissues[2] };
        var doc = SarFixtures.Dipole();
        C3dPersistence.SaveToFile(path, doc);

        var holed = AnatomyImport.PlanSurfaces([open], "mm", null, null, doc);
        var e = Assert.Throws<LabelVolumeException>(() => AnatomyImport.ApplySurfaces(holed, doc, path));
        Assert.Contains("'holed.stl' is not a closed surface: 3 open edges (a hole)", e.Message, StringComparison.Ordinal);

        var plan = AnatomyImport.PlanSurfaces([inner, outer], "mm", materials, null, doc);
        Assert.Equal(["skin", "brain"], plan.Rows.Select(r => r.Name));
        var result = AnatomyImport.ApplySurfaces(plan, doc, path);
        Assert.All(result.Files, f => Assert.True(File.Exists(f.Path)));
        var (problem, low) = SarFixtures.Lower(doc, path, cws);
        var skin = problem.Solids.Single(s => s.Name == "skin");
        var brain = problem.Solids.Single(s => s.Name == "brain");
        Assert.True(brain.Order > skin.Order);
        var readers = XDocument.Parse(low.Model!).Descendants("PolyhedronReader").ToList();
        Assert.Equal(2, readers.Count);
        Assert.All(readers, r => Assert.Equal("PLY", r.Attribute("FileType")!.Value));
        Assert.True(C3dThermal.Inside(brain.Primitive, new Point3(0.1, 0.1, 0.1), 0));
        Assert.False(C3dThermal.Inside(brain.Primitive, new Point3(0.07, 0.1, 0.1), 0));

        // a tetrahedral cube: 8 corners, 5 tetrahedra, region 2, coordinates in mm
        File.WriteAllText(Path.Combine(src, "cube.node"),
            "8 3 0 0\n1 0 0 0\n2 10 0 0\n3 10 10 0\n4 0 10 0\n5 0 0 10\n6 10 0 10\n7 10 10 10\n8 0 10 10\n");
        File.WriteAllText(Path.Combine(src, "cube.ele"),
            "5 4 1\n1 1 2 4 5 2\n2 2 3 4 7 2\n3 2 5 6 7 2\n4 4 5 7 8 2\n5 2 4 5 7 2\n");
        var cube = TetMesh.Read(Path.Combine(src, "cube.ele")).Voxelise(1e-3, 1e-3);
        Assert.Equal((10, 10, 10), (cube.Nx, cube.Ny, cube.Nz));
        Assert.Equal(new SortedDictionary<int, long> { [2] = 1000 }, cube.Histogram());
    }
}

/// <summary>R-sar11-6 — skips, naming the folder, unless testdata/sar/local-models holds a model a developer obtained
/// themselves (git-ignored; nothing of it is ever committed).</summary>
public sealed class LocalModelFactAttribute : FactAttribute
{
    public static string Folder => Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "local-models");

    public static IReadOnlyList<string> Models()
        => Directory.Exists(Folder)
            ? [.. Directory.GetFiles(Folder).Where(f => NiftiFile.IsNifti(f) || f.EndsWith(".h5", StringComparison.OrdinalIgnoreCase)).Order()]
            : [];

    public LocalModelFactAttribute()
    {
        if (Models().Count == 0)
            Skip = "needs a segmented model in testdata/sar/local-models (git-ignored, never committed; its README says what goes there)";
    }
}

public sealed class LocalModelImportTests(ITestOutputHelper output)
{
    /// <summary>R-sar11-5/-6: every local model reads, its label histogram covers every voxel, and its size report is said.</summary>
    [LocalModelFact]
    public void EveryLocalModel_Reads_AndItsSizeIsReported()
    {
        foreach (string model in LocalModelFactAttribute.Models())
        {
            string stem = model.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase) ? model[..^7] : Path.ChangeExtension(model, null);
            var plan = AnatomyImport.PlanVoxels(model, new VoxelImportOptions
            {
                LabelsCsv = File.Exists(stem + ".csv") ? stem + ".csv" : null, AllowUnmapped = true,
            }, null, new C3dDocument(), 1000);
            Assert.Equal(plan.Volume.Count, plan.Rows.Sum(r => r.Voxels));
            output.WriteLine(Path.GetFileName(model));
            foreach (string line in plan.Report.Lines(plan.Rows)) output.WriteLine("  " + line);
        }
    }
}
