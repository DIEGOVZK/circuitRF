using System.Globalization;
using System.Text;
using CircuitRF.Design.Modeling;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>brief-modelcomposer-3 R-mc3-3: a six-file Touchstone set with its biases in the file names and the
/// same set with its biases in comment lines import to the same normalised data.</summary>
public sealed class TouchstoneSetImportTests
{
    private static readonly (double Vgs, double Vds, double IdMa)[] Biases =
        [(-3, 5, 2), (-3, 20, 3), (-2.5, 5, 40), (-2.5, 20, 55.5), (-2, 5, 120), (-2, 20, 180)];

    [Fact]
    public void BiasesInNames_AndBiasesInComments_ImportTheSame()
    {
        string root = Directory.CreateTempSubdirectory("crf-mc3-ts-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "named"));
        Directory.CreateDirectory(Path.Combine(root, "commented"));
        var named = new List<string>();
        var commented = new List<string>();
        for (int k = 0; k < Biases.Length; k++)
        {
            var (vgs, vds, id) = Biases[k];
            string n = $"named/fet_Vgs{F(vgs)}_Vds{F(vds)}_Id{F(id)}mA.s2p";
            string c = $"commented/point{k + 1}.s2p";
            File.WriteAllText(Path.Combine(root, n), Touchstone(k, header: ""));
            File.WriteAllText(Path.Combine(root, c), Touchstone(k,
                header: $"! measured on station 2\n! Vgs = {F(vgs)} V\n! Vds = {F(vds)} V\n! Id = {F(id)} mA\n"));
            named.Add(n);
            commented.Add(c);
        }

        var a = DataImport.Import(Set(named, "fet_Vgs{vgs}_Vds{vds}_Id{id:mA}mA", "name"), root);
        var b = DataImport.Import(Set(commented, "Vgs = {vgs} V Vds = {vds} V Id = {id:mA} mA", "comment"), root);

        Assert.Equal(6, a.Data.Count);
        Assert.Equal(a.Data.Points.Select(p => (p.Vgs, p.Vds, p.Id)), b.Data.Points.Select(p => (p.Vgs, p.Vds, p.Id)));
        Assert.Equal(a.Data.FrequenciesHz, b.Data.FrequenciesHz);
        Assert.Equal(a.Data.S, b.Data.S);
        Assert.Equal(a.Data.Z0, b.Data.Z0);

        Assert.Equal((-2.5, 20.0), (a.Data.Points[3].Vgs, a.Data.Points[3].Vds));
        Assert.Equal(0.0555, a.Data.Points[3].Id, 12);
        Assert.Equal(new System.Numerics.Complex(0.1 * 3, 3 + 0.01 * 3), a.Data.SAt(3, 2, 1, 0));    // S21 of point 3 at 3 GHz
        Assert.Equal("6 point(s), Vgs -3 to -2 V, Vds 5 to 20 V, 1 to 3 GHz in 3 frequencies.", a.Summary);
    }

    private static CmcDataSet Set(IReadOnlyList<string> sources, string pattern, string from) => new()
    {
        Name = from, Kind = DataSetKind.SParams, Sources = sources,
        Import = new ImportSettings { Format = ImportSettings.Touchstone, Pattern = pattern, PatternSource = from },
    };

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>S11 = 0.1·f, S21 = 0.1·k + j(f + 0.01·k), S12 = 0.01, S22 = 0.2 — distinct per point and frequency.</summary>
    private static string Touchstone(int k, string header)
    {
        var sb = new StringBuilder(header);
        sb.Append("# GHz S RI R 50\n");
        for (int f = 1; f <= 3; f++)
            sb.Append(FormattableString.Invariant(
                $"{f} {0.1 * f} 0 {0.1 * k} {f + 0.01 * k} 0.01 0 0.2 0\n"));
        return sb.ToString();
    }
}
