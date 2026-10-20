using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using Xunit;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// A tuner on a net no nonlinear device touches — a composed FET's terminals always sit behind its own linear
/// network — is load-pulled, not refused: the net joins the HB interface and Pout and the bias currents come from
/// the tuners' own branch currents. Gate: the same FET with a 1 µΩ resistor between each terminal and its tuner
/// reports what the canonical circuit (tuners on the device terminals) reports.
/// </summary>
public class LoadpullOffDeviceTunerTests
{
    private static string BuildCnl(bool behindNetwork)
    {
        string g = behindNetwork ? "tg" : "n1", d = behindNetwork ? "td" : "n2";
        string net = behindNetwork ? "R:RG n1 tg R=1e-6 Ohm\nR:RD n2 td R=1e-6 Ohm\n" : "";
        return
            "define MyFET (gate drain)\n" +
            "  Sv = -0.837\n  Sc = 0.71\n  TV0 = 4.268\n  TC = 1.507\n  th = 0.001\n" +
            "  a = 0.176\n  g = 0.089\n  lam = 0.0012\n  B = 1130\n" +
            "  SDD:X1  gate  0  drain  0  I[1,0]=_v1/5000  " +
            "I[2,0]=(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1)))+1))*" +
            "ln(exp(-(2*TV0 - 2*_v1 +2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1) * (_v2*lam + 1))/2\n" +
            "end MyFET\n\n" +
            "RFfreq = 2.2 GHz\nVgs = -3.05\nVDD = 48\n\n" +
            "MyFET:X1  n1  n2\n" + net +
            $"Tuner:SourceTuner1  {g}  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=Vgs  Z[1]=50\n" +
            $"Tuner:LoadTuner1  {d}  0  Zdefault=1e-6  Z0=50  BiasTee=on  Vbias=VDD  Z[1]=50\n" +
            "analysis LPP1 type=loadpull_pursuit Tone=\"RFfreq\" ToneUnit=GHz MaxHarm=3 " +
            "LoadTuner=LoadTuner1 SourceTuner=SourceTuner1 Sweep=Load TuneHarm=1 Compression=3 " +
            "GainType=Gt PinStart=-5 PinStep=2.5 PinMax=10 MaxIter=100 Tol=1e-7 CreateLoadpullResult=false\n";
    }

    private static GridPointResult RunAt50(string cnl, out LoadpullEngine.PursuitContext ctx)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var netlist = new Elaborator(lib).Elaborate(tb);
        var lpa = tb.Analyses.OfType<CircuitRF.Core.Design.LoadpullPursuitAnalysis>().First();
        var lpp = LoadpullPursuitEngine.Resolve(lpa, netlist.ResolvedGlobals).LpParams;
        var eng = new LoadpullEngine(netlist, tb);
        ctx = eng.PrepareContext(lpp);
        return eng.RunOneTermination(lpp, ctx, new Complex(50, 0), gridIndex: 0);
    }

    [Fact]
    public void TunersBehindALinearNetwork_ReportWhatTunersOnTheTerminalsReport()
    {
        var canonical = RunAt50(BuildCnl(behindNetwork: false), out var c0);
        var behind = RunAt50(BuildCnl(behindNetwork: true), out var c1);
        Assert.False(c0.LoadOffDevice || c0.SrcOffDevice);
        Assert.True(c1.LoadOffDevice && c1.SrcOffDevice);

        var a = canonical.PinSteps.Where(s => s.Converged && !s.IsTickle).ToList();
        var b = behind.PinSteps.Where(s => s.Converged && !s.IsTickle).ToList();
        Assert.NotEmpty(a);
        Assert.Equal(a.Select(s => s.PavlDbm), b.Select(s => s.PavlDbm));
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].PoutW, b[i].PoutW, 1e-6 * Math.Abs(a[i].PoutW));
            Assert.Equal(a[i].PinDeliveredW, b[i].PinDeliveredW, 1e-6 * Math.Abs(a[i].PinDeliveredW));
            Assert.Equal(a[i].BiasCurrentLoadA, b[i].BiasCurrentLoadA, 1e-6 * Math.Abs(a[i].BiasCurrentLoadA));
            Assert.Equal(a[i].BiasCurrentSrcA, b[i].BiasCurrentSrcA, 1e-6 * Math.Abs(a[i].BiasCurrentSrcA) + 1e-12);
            Assert.Equal(a[i].Pae, b[i].Pae, 1e-6);
        }
    }
}
