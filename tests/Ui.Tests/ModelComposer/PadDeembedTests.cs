using CircuitRF.Design.Modeling;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-4 R-mc4-1: synthetic pads around the known linear core, simulated by circuitRF and written as
/// Touchstone files, are removed back to the core's own S; a pad that does not cover the data's frequencies is refused
/// naming both ranges.
/// </summary>
public sealed class PadDeembedTests
{
    private static readonly double[] Freqs = [0.5e9, 2e9, 8e9, 20e9, 40e9];

    // A shunt pad capacitance, then a series feed, on each side.
    private const string Shunts = "C:CPG a 0 C=30e-15\nC:CPD b 0 C=25e-15\n";
    private const string Series = "R:RSG a xg R=3\nL:LSG xg {G} L=50e-12\nR:RSD b xd R=2\nL:LSD xd {D} L=60e-12\n";

    [Fact]
    public void OpenShort_AroundAKnownTwoPort_IsRemoved()
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc4-pads-").FullName;
        var measured = SyntheticFetData.TwoPortS(Shunts + Series.Replace("{G}", "g").Replace("{D}", "d") + "core:XC g d 0\n", Freqs);
        SyntheticFetData.WriteS2p(Path.Combine(dir, "open.s2p"), SyntheticFetData.TwoPortS(Shunts, Freqs));
        SyntheticFetData.WriteS2p(Path.Combine(dir, "short.s2p"),
            SyntheticFetData.TwoPortS(Shunts + Series.Replace("{G}", "0").Replace("{D}", "0"), Freqs));

        var pads = PadDeembedding.Load(new PadsSettings { Kind = PadsKind.OpenShort, Open = "open.s2p", Short = "short.s2p" }, dir);
        AssertIsTheCore(pads.Remove(measured, "bias"), measured);
    }

    [Fact]
    public void TwoPorts_AroundAKnownTwoPort_AreRemovedByCascadeInversion()
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc4-pads2-").FullName;
        // Each pad file: port 1 (net a) at the probe, port 2 (net b) facing the device; deliberately asymmetric.
        const string gate = "C:C1 a 0 C=30e-15\nR:R1 a x R=3\nL:L1 x b L=50e-12\nC:C2 b 0 C=10e-15\n";
        const string drain = "C:C1 a 0 C=25e-15\nR:R1 a x R=2\nL:L1 x b L=60e-12\nC:C2 b 0 C=8e-15\n";
        SyntheticFetData.WriteS2p(Path.Combine(dir, "gate.s2p"), SyntheticFetData.TwoPortS(gate, Freqs));
        SyntheticFetData.WriteS2p(Path.Combine(dir, "drain.s2p"), SyntheticFetData.TwoPortS(drain, Freqs));
        // Measured: the same two pads, gate pad from probe a to g, drain pad from probe b to d, the core between.
        const string measuredBody =
            "C:CG1 a 0 C=30e-15\nR:RG1 a xg R=3\nL:LG1 xg g L=50e-12\nC:CG2 g 0 C=10e-15\n" +
            "C:CD1 b 0 C=25e-15\nR:RD1 b xd R=2\nL:LD1 xd d L=60e-12\nC:CD2 d 0 C=8e-15\n" +
            "core:XC g d 0\n";
        var measured = SyntheticFetData.TwoPortS(measuredBody, Freqs);

        var pads = PadDeembedding.Load(new PadsSettings { Kind = PadsKind.TwoPorts, Gate = "gate.s2p", Drain = "drain.s2p" }, dir);
        AssertIsTheCore(pads.Remove(measured, "bias"), measured);
    }

    [Fact]
    public void PadShorterThanTheData_IsRefused_NamingBothRanges()
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc4-padrange-").FullName;
        double[] narrow = [1e9, 5e9, 10e9];
        SyntheticFetData.WriteS2p(Path.Combine(dir, "open.s2p"), SyntheticFetData.TwoPortS(Shunts, narrow));
        SyntheticFetData.WriteS2p(Path.Combine(dir, "short.s2p"), SyntheticFetData.TwoPortS(Shunts, narrow));
        var pads = PadDeembedding.Load(new PadsSettings { Kind = PadsKind.OpenShort, Open = "open.s2p", Short = "short.s2p" }, dir);

        var ex = Assert.Throws<ModelingException>(() => pads.Remove(SyntheticFetData.CoreS(null, null, Freqs), "bias"));
        Assert.Equal("open.s2p: it spans 1 to 10 GHz and bias spans 0.5 to 40 GHz; a pad is never extrapolated.", ex.Message);
    }

    private static void AssertIsTheCore(NormalisedData removed, NormalisedData measured)
    {
        var core = SyntheticFetData.CoreS(null, null, Freqs);
        for (int k = 0; k < Freqs.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    var err = (removed.SAt(0, k, i, j) - core.SAt(0, k, i, j)).Magnitude;
                    Assert.True(err < 1e-9, $"S{i + 1}{j + 1} at {Freqs[k] / 1e9} GHz: {err:E3}");
                }
        // Not vacuous: the pads moved the measured S.
        Assert.True((measured.SAt(0, 4, 1, 0) - core.SAt(0, 4, 1, 0)).Magnitude > 1e-2);
    }
}
