using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-11's document: multi-bias S of <see cref="ComposedTestModels"/>' SDD truth inside the GaN network
/// (<see cref="SyntheticFetData.BiasedS"/>), written as a Touchstone set at the device plane; the stock GaN network at its
/// stock values, which are the truth's; N0 = 4; and the truth's own equations recorded as the current and charge steps,
/// stamped as their fits stamp them — a composed model whose every part is known, so an export can be held to the truth.
/// </summary>
public static class ExportFixture
{
    public static readonly double[] Freqs = [0.5e9, 2e9, 5e9, 10e9];
    public static readonly double[] Vgs = [-1.5, -0.6, 0.0];
    public static readonly double[] Vds = [2, 6];
    private static readonly string[] Inputs = [ComposedCoreProgram.Vgs, ComposedCoreProgram.Vds];

    /// <summary>The <c>.cmc</c>'s path in a fresh folder. <paramref name="periphery"/> false leaves N0 unset;
    /// <paramref name="charge"/> false leaves the charge step unfitted.</summary>
    public static string Document(bool periphery = true, bool charge = true)
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc11-").FullName;
        string cmc = Path.Combine(dir, "fet.cmc");
        var s = SyntheticFetData.BiasedS(SyntheticFetData.NetworkDevice, Vgs, Vds, Freqs);
        var sources = new List<string>();
        for (int p = 0; p < s.Count; p++)
        {
            string name = $"s/Vgs{F(s.Points[p].Vgs)}_Vds{F(s.Points[p].Vds)}_Id{(1e3 * s.Points[p].Id).ToString("F9", CultureInfo.InvariantCulture)}.s2p";
            Directory.CreateDirectory(Path.Combine(dir, "s"));
            File.WriteAllText(Path.Combine(dir, name), Touchstone(s, p));
            sources.Add(name);
        }
        var set = new CmcDataSet
        {
            Name = "bias", Kind = DataSetKind.SParams, Sources = sources,
            Conditions = new DataConditions { ReferencePlane = ReferencePlane.Device, AmbientC = 25 },
            Import = new ImportSettings { Format = ImportSettings.Touchstone, Pattern = "Vgs{vgs}_Vds{vds}_Id{id:mA}", PatternSource = "name" },
        };
        var doc = StockNetworks.Choose(new CmcDocument
        {
            Kind = CmcKind.Active, DataSets = [set], BasePeriphery = periphery ? new BasePeriphery(4, "125 um") : null,
        }, cmc, "gan-two-shell");

        var current = new CurrentFitRecord
        {
            Id = Constituent.Equation(ComposedTestModels.IdEq, Inputs, ComposedTestModels.IdParams),
            Ig = Constituent.GateEquations(ComposedTestModels.IgGsEq, ComposedTestModels.IgGdEq, Inputs, ComposedTestModels.IgParams),
        };
        doc = CurrentFit.Apply(doc, new CurrentFitResult(CurrentFitSettings.Default, current, new Dictionary<string, DataCube>()));
        if (charge)
        {
            var record = new ChargeFitRecord
            {
                Qg = Constituent.Equation(ComposedTestModels.QgEq, Inputs, ComposedTestModels.QgParams),
                Qd = Constituent.Equation(ComposedTestModels.QdEq, Inputs, ComposedTestModels.QdParams),
            };
            doc = ChargeFit.Apply(doc, new ChargeFitResult(ChargeFitSettings.Default, record, new Dictionary<string, DataCube>()));
        }
        CmcFile.Write(cmc, doc);
        string digest = CmcStamps.DataDigest(cmc, doc);
        doc = CmcStamps.Stamp(doc, CurrentFit.StepName, digest);
        if (charge) doc = CmcStamps.Stamp(doc, ChargeFit.StepName, digest);
        CmcFile.Write(cmc, doc);
        return cmc;
    }

    /// <summary>The SDD truth inside its network, as a <c>.cnl</c> instance on <c>g d 0</c> named <c>X1</c>.</summary>
    public static string Truth(string g = "g", string d = "d") =>
        ComposedTestModels.SddGlobals + ComposedTestModels.ReferenceCell("ref") + $"ref:X1 {g} {d} 0\n";

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    internal static string Touchstone(NormalisedData s, int p)
    {
        var sb = new StringBuilder("# Hz S RI R 50\n");
        for (int k = 0; k < s.FrequenciesHz!.Length; k++)
        {
            sb.Append(s.FrequenciesHz[k].ToString("R", CultureInfo.InvariantCulture));
            foreach (var (i, j) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            {
                Complex v = s.SAt(p, k, i, j);
                sb.Append(' ').Append(v.Real.ToString("R", CultureInfo.InvariantCulture))
                  .Append(' ').Append(v.Imaginary.ToString("R", CultureInfo.InvariantCulture));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
