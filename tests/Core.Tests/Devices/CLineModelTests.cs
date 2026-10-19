using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Tests.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// CLIN, the ideal coupled pair: the even/odd decomposition must reduce exactly to two independent
/// TLINs when the pair is uncoupled, and reproduce the textbook backward-wave coupler (perfect match
/// and isolation at every frequency when Ze·Zo = Z0², coupling (Ze − Zo)/(Ze + Zo) at a quarter wave,
/// quadrature between through and coupled ports).
/// </summary>
public class CLineModelTests
{
    private const double F0 = 3.5e9, Z0 = 50.0;

    private static Dictionary<(int Row, int Col), Complex> Stamp(ComponentModel m, int[] nodes, double fHz)
    {
        var mna = new CapturingMnaContext();
        m.Stamp(mna, new ElaboratedComponent("CLIN", "X1", nodes, new Dictionary<string, Value>(), m), 2 * Math.PI * fHz);
        return mna.Entries;
    }

    /// <summary>4×4 S (reference Z0) from the captured Y on nodes 1..4.</summary>
    private static Complex[,] S(CLineModel m, double fHz)
    {
        var e = Stamp(m, [1, 2, 3, 4], fHz);
        var y = new Complex[4, 4];
        foreach (var ((r, c), v) in e) y[r - 1, c - 1] += v;
        // S = (I − Z0·Y)(I + Z0·Y)^-1, solved column by column with a small dense Gauss-Jordan.
        var a = new Complex[4, 8];
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++) a[i, j] = (i == j ? 1 : 0) + Z0 * y[i, j];
            a[i, 4 + i] = 1;
        }
        for (int p = 0; p < 4; p++)
        {
            int best = p;
            for (int r = p + 1; r < 4; r++) if (a[r, p].Magnitude > a[best, p].Magnitude) best = r;
            for (int k = 0; k < 8; k++) (a[p, k], a[best, k]) = (a[best, k], a[p, k]);
            Complex d = a[p, p];
            for (int k = 0; k < 8; k++) a[p, k] /= d;
            for (int r = 0; r < 4; r++)
            {
                if (r == p) continue;
                Complex f = a[r, p];
                for (int k = 0; k < 8; k++) a[r, k] -= f * a[p, k];
            }
        }
        var s = new Complex[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                Complex sum = 0;
                for (int k = 0; k < 4; k++) sum += ((i == k ? 1 : 0) - Z0 * y[i, k]) * a[k, 4 + j];
                s[i, j] = sum;
            }
        return s;
    }

    [Theory]
    [InlineData(1e9)]
    [InlineData(3.5e9)]
    [InlineData(6e9)]
    public void Uncoupled_IsExactlyTwoTlins(double fHz)
    {
        var pair = new CLineModel(50, 50, Math.PI / 2, Math.PI / 2, F0);
        var line = new TLineModel(50, Math.PI / 2, F0, "TLIN:X1");
        var p = Stamp(pair, [1, 2, 3, 4], fHz);
        var la = new CapturingMnaContext(); line.Stamp(la, new ElaboratedComponent("TLIN", "A", [1, 2], new Dictionary<string, Value>(), line), 2 * Math.PI * fHz);
        foreach (var ((r, c), v) in la.Entries)
        {
            Assert.True((p[(r, c)] - v).Magnitude < 1e-15 * v.Magnitude + 1e-18, $"line A ({r},{c})");
            Assert.True((p[(r + 2, c + 2)] - v).Magnitude < 1e-15 * v.Magnitude + 1e-18, $"line B ({r},{c})");
            Assert.True(p[(r, c + 2)].Magnitude < 1e-18, $"cross ({r},{c})");
        }
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(3.0103)]
    public void MatchedPair_IsTheTextbookCoupler(double couplingDb)
    {
        double c = Math.Pow(10, -couplingDb / 20);
        double ze = Z0 * Math.Sqrt((1 + c) / (1 - c)), zo = Z0 * Math.Sqrt((1 - c) / (1 + c));
        var m = new CLineModel(ze, zo, Math.PI / 2, Math.PI / 2, F0);
        Assert.Equal(c, m.CouplingCoefficient, 12);
        foreach (double f in new[] { 1e9, 2.2e9, 3.5e9, 4.7e9, 6e9 })
        {
            var s = S(m, f);
            Assert.True(s[0, 0].Magnitude < 1e-9, $"S11 at {f}");
            Assert.True(s[3, 0].Magnitude < 1e-9, $"S41 (isolated) at {f}");
            double p = s[1, 0].Magnitude * s[1, 0].Magnitude + s[2, 0].Magnitude * s[2, 0].Magnitude;
            Assert.Equal(1.0, p, 9);                                       // lossless
        }
        var s0 = S(m, F0);
        Assert.Equal(c, s0[2, 0].Magnitude, 9);                             // coupled port, quarter wave
        double dphi = (s0[2, 0] / s0[1, 0]).Phase * 180 / Math.PI;
        Assert.Equal(90.0, Math.Abs(dphi), 6);                              // quadrature
        Assert.True((s0[0, 2] - s0[2, 0]).Magnitude < 1e-12);              // reciprocal
    }

    [Fact]
    public void UnequalModalLengths_CostIsolation()
    {
        double c = Math.Pow(10, -10.0 / 20);
        double ze = Z0 * Math.Sqrt((1 + c) / (1 - c)), zo = Z0 * Math.Sqrt((1 - c) / (1 + c));
        var homo = new CLineModel(ze, zo, Math.PI / 2, Math.PI / 2, F0);
        var inhomo = new CLineModel(ze, zo, Math.PI / 2, Math.PI / 2 * 0.92, F0);
        Assert.True(S(inhomo, F0)[3, 0].Magnitude > 100 * S(homo, F0)[3, 0].Magnitude + 1e-3);
    }

    [Theory]
    [InlineData(40.0, 60.0, "clin.ze.below-zo")]
    [InlineData(60.0, 0.0, "clin.zo.not-positive")]
    public void InvalidImpedances_AreRefused(double ze, double zo, string code)
    {
        var why = CLineModel.Refusal(ze, zo, 1.0, 1.0, F0);
        Assert.NotNull(why);
        Assert.Equal(code, why!.Id);
        Assert.Throws<ArgumentException>(() => new CLineModel(ze, zo, 1.0, 1.0, F0));
    }
}
