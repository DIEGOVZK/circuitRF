// ================================================================
//  SarReferenceSetTests.cs — the gate for brief-sar-1: testdata/sar/openems-ref, what openEMS 0.37.0's own SAR and
//  MRI tutorials computed, run unmodified and committed as fixed data. Nothing here runs openEMS and nothing here is
//  circuitRF code under test: it holds the reference set itself to what its README says — every file listed exists
//  and loads, the arrays agree with their mesh lines, and the dipole's power budget is what the tutorial prints.
//  The data is kept on the developer machine only, never in the repository; where it is absent every test here
//  skips with a reason (SarReferenceFact).
// ================================================================

using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Sar;

public sealed class SarReferenceSetTests
{
    private static readonly string Root = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "openems-ref");

    [SarReferenceFact]
    public void EveryFileTheReadmeListsExists_AndLoads()
    {
        string readme = File.ReadAllText(Path.Combine(Root, "README.md"));
        var ignored = File.ReadAllLines(Path.Combine(Root, ".gitignore"))
                          .Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();
        var listed = Regex.Matches(readme, @"`((?:Dipole_SAR|MRI_Loop_Coil|MRI_LP_Birdcage|analytic)/[^`\s]+)`")
                          .Select(m => m.Groups[1].Value).Where(p => !ignored.Contains(p)).Distinct().ToList();
        Assert.True(listed.Count >= 40, $"only {listed.Count} files listed");

        foreach (string rel in listed)
        {
            string path = Path.Combine(Root, rel);
            Assert.True(File.Exists(path), $"README lists {rel}, which is not there");
            switch (Path.GetExtension(rel))
            {
                case ".npy":  Assert.True(Npy.Read(path).Count > 0, rel); break;
                case ".json": using (JsonDocument.Parse(File.ReadAllText(path))) { } break;
                case ".xml":  Assert.NotNull(XDocument.Load(path).Root); break;
                default:      Assert.True(new FileInfo(path).Length > 0, rel); break;
            }
        }
    }

    [SarReferenceFact]
    public void Dipole_ArraysAgreeWithTheirMesh_AndThePowerBudgetIsWhatTheTutorialPrints()
    {
        string d = Path.Combine(Root, "Dipole_SAR");
        var sar = Npy.Read(Path.Combine(d, "sar_10g.npy"));
        Assert.Equal([Lines(d, "sar_10g_mesh_x_m"), Lines(d, "sar_10g_mesh_y_m"), Lines(d, "sar_10g_mesh_z_m")], sar.Shape);
        int nf = Npy.Read(Path.Combine(d, "freq_hz.npy")).Count;
        foreach (string port in new[] { "s11", "zin_ohm", "p_acc_w" })
            Assert.Equal([nf], Npy.Read(Path.Combine(d, port + ".npy")).Shape);

        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(d, "summary.json")));
        var s = summary.RootElement;
        double budget = s.GetProperty("power_budget_percent").GetDouble();
        Assert.InRange(budget, 95.0, 100.0);
        double accepted = s.GetProperty("accepted_power_w").GetDouble();
        double expected = (s.GetProperty("radiated_power_w").GetDouble() + s.GetProperty("absorbed_power_w").GetDouble()) / accepted * 100;
        Assert.Equal(expected, budget, 9);
        // psSAR10g per watt is the averaged file's own maximum over the accepted power (float32 data).
        Assert.Equal(sar.Real.Max() / accepted, s.GetProperty("psSAR10g_w_per_kg_per_w").GetDouble(), 1e-6 * 2.5);

        AssertGridIsTheOneThatRan(Path.Combine(d, "sar_dipole.xml"), Path.Combine(d, "run.log"));
    }

    [SarReferenceFact]
    public void LoopCoil_PlanesAgreeWithTheirMesh()
    {
        string d = Path.Combine(Root, "MRI_Loop_Coil");
        foreach (string plane in new[] { "xy", "xz" })
        {
            Assert.Equal([Lines(d, $"sar_{plane}_mesh_x_m"), Lines(d, $"sar_{plane}_mesh_y_m"), Lines(d, $"sar_{plane}_mesh_z_m")],
                         Npy.Read(Path.Combine(d, $"sar_{plane}.npy")).Shape);
            int[] b1 = [Lines(d, $"b1_{plane}_mesh_x_m"), Lines(d, $"b1_{plane}_mesh_{plane[1]}_m")];
            Assert.Equal(b1, Npy.Read(Path.Combine(d, $"b1p_{plane}_t_per_sqrtw.npy")).Shape);
            Assert.Equal(b1, Npy.Read(Path.Combine(d, $"b1m_{plane}_t_per_sqrtw.npy")).Shape);
        }
        int nf = Npy.Read(Path.Combine(d, "freq_hz.npy")).Count;
        Assert.Equal([nf], Npy.Read(Path.Combine(d, "s11.npy")).Shape);
        AssertGridIsTheOneThatRan(Path.Combine(d, "MRI_Loop_Coil.xml"), Path.Combine(d, "run.log"));
    }

    /// <summary>The birdcage's planes are (r, α) on its cylindrical grid. Its XML closes the α circle with a line 2π after
    /// the first, which openEMS does not count, so the run reports one α line fewer than the XML holds.</summary>
    [SarReferenceFact]
    public void Birdcage_PlanesAgreeWithTheirMesh_AndItsGridIsTheOneThatRan()
    {
        string d = Path.Combine(Root, "MRI_LP_Birdcage");
        Assert.Equal([Lines(d, "sar_z0_mesh_1"), Lines(d, "sar_z0_mesh_2")], Npy.Read(Path.Combine(d, "sar_z0.npy")).Shape);
        int[] b1 = [Lines(d, "b1_z0_mesh_1"), Lines(d, "b1_z0_mesh_2")];
        Assert.Equal(b1, Npy.Read(Path.Combine(d, "b1p_z0_t.npy")).Shape);
        Assert.Equal(b1, Npy.Read(Path.Combine(d, "b1m_z0_t.npy")).Shape);
        int nf = Npy.Read(Path.Combine(d, "freq_hz.npy")).Count;
        foreach (string a in new[] { "s11", "s22", "port1_uf_inc", "port1_p_acc_w", "port2_uf_inc", "port2_p_acc_w" })
            Assert.Equal([nf], Npy.Read(Path.Combine(d, a + ".npy")).Shape);

        var grid = XDocument.Load(Path.Combine(d, "BirdCage.xml")).Descendants("RectilinearGrid").Single();
        double[][] lines = new[] { "XLines", "YLines", "ZLines" }
            .Select(a => grid.Element(a)!.Value.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray())
            .ToArray();
        Assert.Equal(2 * Math.PI, lines[1][^1] - lines[1][0], 9);
        var m = Regex.Match(File.ReadAllText(Path.Combine(d, "run.log")), @"FDTD simulation size: (\d+)x(\d+)x(\d+)");
        Assert.True(m.Success);
        int[] ran = [int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)];
        int[] xml = [lines[0].Length, lines[1].Length - 1, lines[2].Length];
        Assert.Equal(ran, xml);
    }

    /// <summary>test_sar_analytic.py's uniform phantom through the installed sar_calc: σ|E|²/(2ρ) in every cell.</summary>
    [SarReferenceFact]
    public void Analytic_UniformPhantom_IsSigmaESquaredOverTwoRho()
    {
        var sar = Npy.Read(Path.Combine(Root, "analytic", "uniform_out_sar.npy"));
        Assert.Equal([4, 4, 4], sar.Shape);
        Assert.All(sar.Real, v => Assert.Equal(0.5 * 1.0 / (2 * 1000.0), v, 2.5e-4 * 1e-5));
    }

    private static int Lines(string dir, string name) => Npy.Read(Path.Combine(dir, name + ".npy")).Count;

    /// <summary>The XML's grid has the line counts the run reported (<c>FDTD simulation size: AxBxC</c>).</summary>
    private static void AssertGridIsTheOneThatRan(string xml, string log)
    {
        var grid = XDocument.Load(xml).Descendants("RectilinearGrid").Single();
        int[] qty = new[] { "XLines", "YLines", "ZLines" }.Select(a =>
        {
            var e = grid.Element(a)!;
            int n = int.Parse(e.Attribute("Qty")!.Value);
            Assert.Equal(n, e.Value.Split(',').Length);
            return n;
        }).ToArray();
        var m = Regex.Match(File.ReadAllText(log), @"FDTD simulation size: (\d+)x(\d+)x(\d+)");
        Assert.True(m.Success);
        int[] ran = [int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)];
        Assert.Equal(ran, qty);
    }

    /// <summary>A plain NumPy .npy (version 1 or 2, C order) of float32/64 or complex64/128 — what the reference set holds.</summary>
    private sealed record Npy(int[] Shape, double[] Real, Complex[]? Values)
    {
        public int Count => Real.Length;

        public static Npy Read(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            Assert.True(b.Length > 10 && b[0] == 0x93 && Encoding.ASCII.GetString(b, 1, 5) == "NUMPY", path);
            int headerLen = b[6] == 1 ? BitConverter.ToUInt16(b, 8) : BitConverter.ToInt32(b, 8);
            int start = (b[6] == 1 ? 10 : 12) + headerLen;
            string header = Encoding.ASCII.GetString(b, start - headerLen, headerLen);
            Assert.Contains("'fortran_order': False", header);
            string descr = Regex.Match(header, @"'descr': '([^']+)'").Groups[1].Value;
            int[] shape = Regex.Match(header, @"'shape': \(([^)]*)\)").Groups[1].Value
                               .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                               .Select(int.Parse).ToArray();
            int n = shape.Aggregate(1, (a, x) => a * x);
            (int size, bool complex) = descr switch
            {
                "<f4" => (4, false), "<f8" => (8, false), "<c8" => (4, true), "<c16" => (8, true),
                _ => throw new InvalidDataException($"{path}: dtype {descr}"),
            };
            int width = size * (complex ? 2 : 1);
            Assert.Equal(start + (long)n * width, b.Length);
            double At(int offset) => size == 4 ? BitConverter.ToSingle(b, offset) : BitConverter.ToDouble(b, offset);
            var real = new double[n];
            Complex[]? values = complex ? new Complex[n] : null;
            for (int i = 0; i < n; i++)
            {
                int o = start + i * width;
                real[i] = At(o);
                if (values is not null) values[i] = new Complex(real[i], At(o + size));
            }
            return new Npy(shape, real, values);
        }
    }
}

/// <summary>Skips, naming the README, unless testdata/sar/openems-ref holds the reference data — kept on the developer
/// machine only (owner, 2026-10-18), and regenerated by the commands its README gives.</summary>
public sealed class SarReferenceFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
    {
        string root = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "sar", "openems-ref");
        return new[] { "Dipole_SAR", "MRI_Loop_Coil", "MRI_LP_Birdcage", "analytic" }.All(d => Directory.Exists(Path.Combine(root, d)))
            ? null
            : "needs the SAR-1 reference data, which is not in the repository: testdata/sar/openems-ref/README.md says how to make it";
    });

    public SarReferenceFactAttribute()
    {
        if (Reason.Value is { } why) Skip = why;
    }
}
