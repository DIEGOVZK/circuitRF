using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// The op-amp and the ideal transformer against their closed forms: the ideal op-amp's virtual
/// short, a finite gain and output resistance, the five-pin tile, and the transformer's N:1 scattering
/// matrix and DC ratio. Every expected value is written from the circuit by hand, not from a run.
/// </summary>
public class OpAmpTransformerTests
{
    private static DataSet Sp(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [1e9]);
    }

    private static Complex S(DataSet ds, int r, int c) => (Complex)ds["S"][0, r, c];

    private static void Near(double expected, Complex got, string what, double tol = 1e-6) =>
        Assert.True(Math.Abs(got.Real - expected) < tol && Math.Abs(got.Imaginary) < tol,
                    $"{what} = {got:G6}, expected {expected:G6}");

    // ── Ideal inverting amplifier: Rin = 1 k, Rf = 10 k, between two 50 Ω ports ──────────────────
    //   The inverting input is a virtual ground, so port 1 sees Rin: S11 = (1000 − 50)/(1000 + 50).
    //   V(out) = −10·V1 with V1 = Vs·1000/1050, and the output is an ideal source, so b2 = V(out)/√50
    //   and S21 = 2·(−10)·1000/1050; S22 = −1, a short.
    [Fact]
    public void IdealInvertingAmp_IsTheFeedbackRatio_WithAShortAtItsOutput()
    {
        var ds = Sp(@"
Port:P1  in  0  Num=1 Z=50 Ohm
Port:P2  out 0  Num=2 Z=50 Ohm
R:Rin    in  m  R=1 kOhm
R:Rf     m  out R=10 kOhm
OpAmp:U1 0 m out
");
        Near(950.0 / 1050, S(ds, 0, 0), "S11");
        Near(-20.0 * 1000 / 1050, S(ds, 1, 0), "S21");
        Near(-1, S(ds, 1, 1), "S22");
    }

    // ── A follower with a finite gain and an output resistance ──────────────────────────────────
    //   The input draws nothing, so S11 = +1 and V(in+) = Vs. Loaded by port 2's 50 Ω,
    //   V(out) = A·(Vs − V(out)) − Rout·V(out)/50, so S21 = 2A/(1 + A + Rout/50). Feedback divides
    //   Rout by the loop gain: Zout = Rout/(1 + A), S22 = (Zout − 50)/(Zout + 50).
    [Fact]
    public void FiniteGainFollower_GainAndOutputImpedance_AreTheLoopGainForms()
    {
        const double A = 1000, Rout = 2000;
        var ds = Sp(@"
Port:P1  in  0  Num=1 Z=50 Ohm
Port:P2  out 0  Num=2 Z=50 Ohm
OpAmp:U1 in out out A=1000 Rout=2000 Ohm
");
        double zout = Rout / (1 + A);
        Near(1, S(ds, 0, 0), "S11");
        Near(2 * A / (1 + A + Rout / 50), S(ds, 1, 0), "S21");
        Near((zout - 50) / (zout + 50), S(ds, 1, 1), "S22");
    }

    // ── The five-pin tile: supplies wired, nothing changes ──────────────────────────────────────
    [Fact]
    public void FivePins_SupplyPinsCarryNothing_SameAmplifier()
    {
        var ds = Sp(@"
Port:P1  in  0  Num=1 Z=50 Ohm
Port:P2  out 0  Num=2 Z=50 Ohm
R:Rin    in  m  R=1 kOhm
R:Rf     m  out R=10 kOhm
Vdc:V1   vp 0   Vdc=12 V
Vdc:V2   0  vn  Vdc=12 V
OpAmp:U1 0 m out vp vn Pins=5
");
        Near(-20.0 * 1000 / 1050, S(ds, 1, 0), "S21");
    }

    // ── Ideal transformer N = 2 between two 50 Ω ports ──────────────────────────────────────────
    //   S11 = (N² − 1)/(N² + 1) = 3/5, S21 = 2N/(N² + 1) = 4/5 (dotted ends together: no inversion),
    //   S22 = −3/5.
    [Fact]
    public void IdealTransformer_TwoToOne_IsTheTextbookScatteringMatrix()
    {
        var ds = Sp(@"
Port:P1  a 0  Num=1 Z=50 Ohm
Port:P2  b 0  Num=2 Z=50 Ohm
Transformer:T1 a 0 b 0 N=2
");
        Near(0.6, S(ds, 0, 0), "S11");
        Near(0.8, S(ds, 1, 0), "S21");
        Near(0.8, S(ds, 0, 1), "S12");
        Near(-0.6, S(ds, 1, 1), "S22");
    }

    // ── Ideal means DC too: 1 V on a 2:1 primary is 0.5 V across the secondary's load ───────────
    [Fact]
    public void IdealTransformer_PassesDc_AtItsTurnsRatio()
    {
        var (lib, tb) = new CnlReader().Read(@"
Vdc:V1   a 0  Vdc=1 V
Transformer:T1 a 0 b 0 N=2
R:RL     b 0  R=100 Ohm
");
        var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);
        Assert.Equal(0.5, dc.NodeVoltages[nl.Nodes.IndexOf("b") - 1], 6);
    }
}
