using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using RfCore.Data;
using RfCore.Export;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-20: a simulator's capacitance table feeds the charges. A <c>.mat</c> of currents and the matrix
/// <c>C</c> at the intrinsic plane fits the charges to the table; the circuit set lands as the same matrix; a table away
/// from the intrinsic plane is refused, and one beside Y is cross-checked against <c>Im Y/ω</c>; a matrix with positive
/// <c>C12</c> is flagged.
/// </summary>
public sealed class CapacitanceTableTests(ITestOutputHelper output)
{
    // The depletion truth of ChargeMethodsTests: [P1] eq. 6.11's junctions, continued flat above Fc·φ.
    private const double Cgs0 = 0.3e-12, Cgd0 = 0.05e-12, Cds = 0.08e-12, Phi = 0.8, Eta = -0.4, Fc = 0.8;

    private static double J(double v, double c0) => c0 * Math.Pow(1 - Math.Min(v, Fc * Phi) / Phi, Eta);

    private static readonly (double Vgs, double Vds)[] Grid =
        [.. from g in Enumerable.Range(0, 11) from d in Enumerable.Range(0, 9) select (-2.0 + 2.5 * g / 10, 8.0 * d / 8)];

    /// <summary>The truth's matrix at every grid point, <c>[point, i, j]</c>.</summary>
    private static double[] Matrix() =>
    [
        .. Grid.SelectMany(p =>
        {
            double cgd = J(p.Vgs - p.Vds, Cgd0);
            return new[] { J(p.Vgs, Cgs0) + cgd, -cgd, -cgd, cgd + Cds };
        }),
    ];

    private static readonly Axis Point = new("point", [.. Enumerable.Range(0, Grid.Length).Select(i => (double)i)]);
    private static readonly Axis Port = new("port", [1, 2]);

    /// <summary>A <c>.mat</c> of the grid's bias and currents plus <paramref name="extra"/>; its arrays sit under <c>dataset/</c>.</summary>
    private static string WriteMat(string dir, string name, Action<DataSet> extra)
    {
        var ds = new DataSet();
        ds.Add("vgs", new DataCube([Point], [.. Grid.Select(p => p.Vgs)]));
        ds.Add("vds", new DataCube([Point], [.. Grid.Select(p => p.Vds)]));
        ds.Add("id", new DataCube([Point], [.. Grid.Select(p => 0.1 * Math.Max(0, p.Vgs + 2) * Math.Tanh(p.Vds))]));
        ds.Add("ig", new DataCube([Point], [.. Grid.Select(_ => -1e-9)]));
        extra(ds);
        string path = Path.Combine(dir, name);
        DataSetExporter.Export(ds, path, ExportFormat.Mat);
        return name;
    }

    private static CmcDataSet Set(string name, string source, ReferencePlane plane, params string[] quantities) => new()
    {
        Name = name, Kind = DataSetKind.SimulatedTables, Sources = [source],
        Conditions = new DataConditions { ReferencePlane = plane },
        Import = new ImportSettings
        {
            Format = ImportSettings.Mat,
            Arrays = new[] { "vgs", "vds", "id", "ig" }.Concat(quantities).ToDictionary(q => q, q => $"dataset/{q}", StringComparer.Ordinal),
        },
    };

    private static DataSet Intrinsic(CmcDataSet set, NormalisedData data, List<string> notes) =>
        IntrinsicExtraction.Compute(set, data, PadDeembedding.Load(PadsSettings.None, "."), null, null, null, notes);

    [Fact]
    public void AMatrixTable_AtTheIntrinsicPlane_FitsTheCharges_ToWithinOnePercentOfTheTable()
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc20-").FullName;
        try
        {
            var table = Matrix();
            string src = WriteMat(dir, "tables.mat", ds => ds.Add("C", new DataCube([Point, Port, Port], table)));
            var depletion = new ChargeConstituentSettings { Method = ChargeMethod.Equation, Template = ChargeTemplates.Depletion };
            var doc = new CmcDocument { Kind = CmcKind.Active, DataSets = [Set("tables", src, ReferencePlane.Intrinsic, "C")] };
            string cmc = Path.Combine(dir, "fet.cmc");
            CmcFile.Write(cmc, ChargeFit.WithSettings(doc, ChargeFitSettings.Default with { Qg = depletion, Qd = depletion }));

            var fit = ChargeFit.FitFile(cmc);
            foreach (string line in fit.Record.Report) output.WriteLine(line);
            Assert.Contains("tables: the capacitances are its table.", fit.Record.Report);
            Assert.Contains(ModelCommands.Explain(cmc), l => l.Text.StartsWith("tables: SimulatedTables, intrinsic plane, capacitances: table.", StringComparison.Ordinal));

            var program = ComposedCoreProgram.Compile(ChargeFit.Model(fit, null), "table");
            double worst = 0;
            for (int p = 0; p < Grid.Length; p++)
            {
                var (c11, c12, c21, c22) = ChargeFit.Capacitances(program, Grid[p].Vgs, Grid[p].Vds);
                double[] model = [c11, c12, c21, c22];
                for (int i = 0; i < 4; i++) worst = Math.Max(worst, Math.Abs(model[i] / table[4 * p + i] - 1));
            }
            output.WriteLine($"worst |ΔC|/C {worst:P4}");
            Assert.True(worst <= 0.01, $"worst |ΔC|/C {worst:P3}");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void TheCircuitSet_GivesTheMatrixSpellingsCapacitances()
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc20-").FullName;
        try
        {
            // A transcapacitance, so all four relations are exercised.
            var table = Matrix();
            for (int p = 0; p < Grid.Length; p++) table[4 * p + 2] -= 0.02e-12 * (1 + Grid[p].Vds / 8);
            string matrix = WriteMat(dir, "matrix.mat", ds => ds.Add("C", new DataCube([Point, Port, Port], table)));
            double[] Per(Func<int, double> f) => [.. Enumerable.Range(0, Grid.Length).Select(f)];
            string circuit = WriteMat(dir, "circuit.mat", ds =>
            {
                ds.Add("cgs", new DataCube([Point], Per(p => table[4 * p] + table[4 * p + 1])));
                ds.Add("cgd", new DataCube([Point], Per(p => -table[4 * p + 1])));
                ds.Add("cds", new DataCube([Point], Per(p => table[4 * p + 3] + table[4 * p + 1])));
                ds.Add("cm", new DataCube([Point], Per(p => table[4 * p + 2] - table[4 * p + 1])));
            });

            var a = Set("matrix", matrix, ReferencePlane.Intrinsic, "C");
            var b = Set("circuit", circuit, ReferencePlane.Intrinsic, "cgs", "cgd", "cds", "cm");
            Assert.Equal("circuit set", DataImport.CapacitanceSource(b));
            var x = Intrinsic(a, DataImport.Import(a, dir).Data, []);
            var y = Intrinsic(b, DataImport.Import(b, dir).Data, []);
            foreach (string q in (string[])["C11", "C12", "C21", "C22"])
                for (int p = 0; p < Grid.Length; p++)
                {
                    double u = x[q].RealValues[p], v = y[q].RealValues[p];
                    Assert.True(Math.Abs(u - v) <= 1e-12 * Math.Abs(u), $"{q} at {p}: {u:R} against {v:R}");
                }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void ATableAtTheDevicePlane_IsRefused_AndOneBesideY_IsCrossChecked()
    {
        var refused = Set("tables", "tables.mat", ReferencePlane.Device, "C");
        var ex = Assert.Throws<CmcFormatException>(() => CmcFile.ToBytes(new CmcDocument { Kind = CmcKind.Active, DataSets = [refused] }));
        Assert.Equal($"DataSets[0].Conditions.ReferencePlane: {CmcFile.CapacitancePlaneSentence}", ex.Message);

        string dir = Directory.CreateTempSubdirectory("crf-mc20-").FullName;
        try
        {
            var table = Matrix();
            double[] f = [0.5e9, 1e9, 2e9, 5e9];
            var yv = new Complex[Grid.Length * f.Length * 4];
            for (int p = 0; p < Grid.Length; p++)
                for (int k = 0; k < f.Length; k++)
                {
                    double w = 2 * Math.PI * f[k];
                    double[] g = [1e-4, 0, 0.1, 5e-3];   // the conductances beside jωC
                    for (int i = 0; i < 4; i++) yv[(p * f.Length + k) * 4 + i] = new Complex(g[i], w * table[4 * p + i]);
                }
            var freq = new Axis("freq", f, "Hz");
            string src = WriteMat(dir, "both.mat", ds =>
            {
                ds.Add("C", new DataCube([Point, Port, Port], table));
                ds.Add("Y", new DataCube([Point, freq, Port, Port], yv));
                ds.Add("freq", new DataCube([freq], f));
            });
            var set = Set("both", src, ReferencePlane.Intrinsic, "C", "Y", "freq");
            Assert.Equal("Y", DataImport.CapacitanceSource(set));
            var notes = new List<string>();
            var ds = Intrinsic(set, DataImport.Import(set, dir).Data, notes);
            Assert.True(ds.Contains("Y"));
            string line = Assert.Single(notes, n => n.Contains("|ΔC|", StringComparison.Ordinal));
            output.WriteLine(line);
            var m = Regex.Match(line, @"^both: max \|ΔC\| (\S+) % between its capacitance table and Im Y/ω\.$");
            Assert.True(m.Success, line);
            Assert.True(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100 < 1e-6, line);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void AMatrixWithPositiveC12_IsFlagged()
    {
        var table = Matrix();
        for (int p = 0; p < Grid.Length; p++) table[4 * p + 1] = -table[4 * p + 1];
        var set = Set("tables", "tables.mat", ReferencePlane.Intrinsic, "C");
        var data = new NormalisedData { Points = [.. Grid.Select((p, k) => new BiasPoint($"#{k}", p.Vgs, p.Vds, double.NaN, double.NaN))], C = table };
        var flag = Assert.Single(DataHygiene.Check([(set, data)]));
        Assert.Equal(HygieneKind.CapacitanceSign, flag.Kind);
        Assert.Equal($"tables: C12 > 0 at {Grid.Length} of {Grid.Length} points; the matrix convention has C12 = −Cgd — was the circuit set (cgd) meant?",
                     Assert.Single(DataHygiene.SetWarnings([flag])));
    }
}
