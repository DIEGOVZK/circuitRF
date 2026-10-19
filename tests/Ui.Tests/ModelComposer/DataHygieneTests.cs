using System.Numerics;
using CircuitRF.Design.Modeling;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>brief-modelcomposer-3 R-mc3-5: an oscillating point, a compliance plateau and a drifted repeat are
/// each flagged, and none of them is excluded — the user decides.</summary>
public sealed class DataHygieneTests
{
    private static CmcDataSet Set(string name, DataSetKind kind) => new()
    {
        Name = name, Kind = kind, Sources = ["x"],
        Import = new ImportSettings { Format = ImportSettings.Touchstone, Pattern = "{vgs}_{vds}", PatternSource = "name" },
    };

    /// <summary>A two-port at 1 and 5 GHz: S11, S21, S12, S22 as given, S12·S21 small enough that K is large.</summary>
    private static Complex[] Block(Complex s11, Complex s22, double s21 = 2) =>
        [s11, 0.01, s21, s22, s11, 0.01, s21, s22];

    [Fact]
    public void Oscillation_Plateau_AndDrift_AreFlagged_AndNothingIsExcluded()
    {
        var sp = new NormalisedData
        {
            Points =
            [
                new BiasPoint("start", -2, 10, double.NaN, double.NaN),
                new BiasPoint("osc",   -1, 10, double.NaN, double.NaN),
                new BiasPoint("end",   -2, 10, double.NaN, double.NaN),       // the reference bias, measured again
            ],
            FrequenciesHz = [1e9, 5e9],
            S = [.. Block(0.5, 0.4), .. Block(new Complex(1.1, 0.2), 0.4), .. Block(0.5, 0.4, s21: 2.2)],
        };
        var iv = new NormalisedData
        {
            Points =
            [
                new BiasPoint("a", -2, 5, 0, 0.1), new BiasPoint("b", -1, 5, 0, 0.25),
                new BiasPoint("c", -1, 10, 0, 0.30), new BiasPoint("d", 0, 5, 0, 0.30), new BiasPoint("e", 0, 10, 0, 0.30),
            ],
        };

        var flags = DataHygiene.Check([(Set("SP", DataSetKind.SParams), sp), (Set("IV", DataSetKind.DcIv), iv)]);

        var osc = Assert.Single(flags, f => f.Kind == HygieneKind.Oscillation);
        Assert.Equal(("SP", "osc"), (osc.DataSet, osc.Point));
        Assert.Equal(["c", "d", "e"], flags.Where(f => f.Kind == HygieneKind.Compliance).Select(f => f.Point));
        var drift = Assert.Single(flags, f => f.Kind == HygieneKind.Drift);
        Assert.Equal("end", drift.Point);
        Assert.Contains("against start", drift.Detail);

        Assert.DoesNotContain(sp.Points.Concat(iv.Points), p => p.Excluded);
    }

    [Fact]
    public void Coverage_IsTheConvexHull()
    {
        var d = new NormalisedData
        {
            Points = [.. new[] { (-3.0, 0.0), (0, 0), (0, 20), (-3, 20), (-1.5, 10), (-2, 5) }
                         .Select((p, k) => new BiasPoint($"{k}", p.Item1, p.Item2, 0, 0))],
        };
        Assert.Equal([(-3.0, 0.0), (0, 0), (0, 20), (-3, 20)], DataHygiene.Coverage([d]));
    }
}
