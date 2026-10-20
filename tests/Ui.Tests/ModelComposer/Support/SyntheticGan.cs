using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Engine;
using RfCore.Data;
using CircuitRF.Design.Modeling;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// The series acceptance truth (brief-modelcomposer-16 R-mc16-1, R-mc16-4; overview §4): one synthetic GaN device written
/// as an SDD — independent of the composer's code — with everything the composer models: <see cref="ComposedTestModels"/>'
/// currents and non-separable charges, a drain current that falls with the junction temperature, self-heating through
/// <see cref="Rth"/> ∥ Cth, and a drain-lag trap state whose capture and emission follow the CaptureEmission form at its
/// defaults. It sits inside the GaN two-shell network (its inductances away from the stock values, so the network fit has
/// something to find) and behind a probe pad on each side (shunt C, series L).
///
/// <para>Every "measurement" is circuitRF simulating that SDD: DC at the dynamic state (the trap at <c>vds</c>, the
/// junction heated), a pulse at the chuck temperature with the trap state the pulse leaves (emission and capture
/// integrated over the measurement's own timing), and S at the DC state, where the thermal and trap nodes are frozen.</para>
/// </summary>
public static class SyntheticGan
{
    // ── The truth ─────────────────────────────────────────────────────────────

    /// <summary>The measured device's finger count; its unit width is 125 µm.</summary>
    public const int N0 = 4;
    /// <summary>Thermal resistance (K/W at N0), thermal time constant (s), and the drain current's fall per kelvin.</summary>
    public const double Rth = 25, TauTh = 10e-6, Kt = 0.004;
    /// <summary>Drain lag: the threshold shifts by <c>At·tanh((phi2 − vds)/Ct)</c>.</summary>
    public const double At = 0.3, Ct = 5;
    /// <summary>
    /// The CaptureEmission defaults, which the truth's trap follows. Capture slows as the gap closes, so a pulse from a 0 V
    /// quiescent drain leaves the state about half a volt short of the pulse's drain voltage: such points lie beyond their
    /// trap-setting bias and are validation only.
    /// </summary>
    public const double TauCapt = 1e-8, TauEmit = 1e-3, Xref = 1, Vcap = 0.05;
    /// <summary>The trap node's charge per volt: slow enough states without stalling harmonic balance.</summary>
    private const double Cn = 1e-3;
    /// <summary>The probe pads: shunt capacitance and series inductance per side.</summary>
    public const double Cpad = 30e-15, Lpad = 15e-12;
    /// <summary>The network's inductances, away from the stock 39.5, 46.4 and 5.25 pH.</summary>
    public const double Lg = 45e-12, Ld = 52e-12, Ls = 6.5e-12;

    /// <summary>The pulse timing every pulsed set states, seconds.</summary>
    public const double PulseWidth = 0.5e-6, SampleDelay = 0.4e-6, Period = 1e-3;
    /// <summary>The double pulse's pre-pulse: to 20 V at pinch-off for 50 µs, 1 µs before the measuring pulse.</summary>
    public const double PreVds = 20, PreWidth = 50e-6, PreGap = 1e-6;
    /// <summary>Every pulsed set holds its quiescent point at this pinched-off gate voltage, where it dissipates nothing.</summary>
    public const double QuiescentVgs = -6;

    public const string Note = "Synthetic: simulated by circuitRF from a known device (an SDD with self-heating and drain lag), not measured.";

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Sdd(string eq) => Regex.Replace(Regex.Replace(eq, @"\bvgs\b", "_v1"), @"\bvds\b", "_v2");

    /// <summary>The truth's constants as globals, with the ambient <c>temp</c> unless a bench states its own.</summary>
    public static string Globals(double? ambientC) =>
        ComposedTestModels.SddGlobals +
        $"kT = {R(Kt)}\nAt = {R(At)}\nCt = {R(Ct)}\nTauC = {R(TauCapt)}\nTauE = {R(TauEmit)}\nXref = {R(Xref)}\nVcap = {R(Vcap)}\n" +
        (ambientC is { } t ? $"temp = {R(t)}\n" : "");

    /// <summary>
    /// The intrinsic core on (g d s), the thermal node <c>t</c> (its voltage the rise in kelvin) and the trap node <c>p</c>
    /// (its voltage phi2), its currents and charges times <paramref name="k"/> = N/N0. <paramref name="held"/> leaves the
    /// trap node's dynamics out, for a pulse that holds it: a capture rate across a held gap is a current of no meaning, and
    /// across a gap of volts it is large enough to stall the DC solve.
    /// </summary>
    private static string Core(string name, double k, bool held)
    {
        string vg = $"(_v1-At*tanh((_v4-_v2)/Ct))";
        string id = $"beta*(1-kT*(temp+_v3-25))*(ln(1+exp(({vg}-Vto)/Vs))*Vs)^2*(1+lambda*_v2)*tanh(alpha*_v2)";
        string igs = Sdd(ComposedTestModels.IgGsEq), igd = Sdd(ComposedTestModels.IgGdEq);
        string ig = $"({igs})+({igd})", idt = $"({id})-({igd})";
        string u = "((_v2-_v4-Xref)/Vcap)";
        string rate = $"(_v2-_v4)/TauE+(Xref/TauC)*(if({u}<20,exp({u}),exp(20)*({u}-19))-exp(-Xref/Vcap))";
        string kk = R(k);
        return $"define {name} (g d s t p)\n" +
               $"SDD:core g s d s t 0 p 0 I[1,0]={kk}*({ig}) I[2,0]={kk}*({idt}) I[3,0]=-{kk}*(({ig})*_v1+({idt})*_v2) " +
               (held ? "I[4,0]=0 " : $"I[4,0]=-{R(Cn)}*({rate}) ") + $"I[1,1]={kk}*({Sdd(ComposedTestModels.QgEq)}) I[2,1]={kk}*({Sdd(ComposedTestModels.QdEq)}) " +
               $"I[4,1]={R(Cn)}*_v4\nend\n";
    }

    /// <summary>The GaN two-shell network at <paramref name="n"/> fingers, the default scaling rules applied.</summary>
    private static string Network(string name, int n)
    {
        double s = (double)n / N0;
        return $"define {name} (P1 P2 P3 P4 P5 P6)\n" +
               $"C:C1 P1 P3 C=7e-15\nC:C4 P1 P2 C=0.5e-15\nC:C6 P2 P3 C=7e-15\n" +
               $"L:L1 P1 n1 L={R(Lg)}\nL:L2 P2 n3 L={R(Ld)}\nL:L3 P3 n2 L={R(Ls)}\n" +
               $"R:rg n1 P4 R={R(5.2 / s)}\nR:rd n3 P5 R={R(9.2 / s)}\nR:rs n2 P6 R={R(6.6 / s)}\n" +
               $"C:C2 n1 n2 C={R(4.9e-15 * s)}\nC:C3 n1 n3 C={R(1e-15 * s)}\nC:C5 n3 n2 C={R(21e-15 * s)}\n" +
               "Mutual:M1 Inductor1=\"L1\" Inductor2=\"L2\" M=1e-12\nend\n";
    }

    /// <summary>
    /// The device as a cell <c>gan (G D S)</c> at <paramref name="n"/> fingers: the network, the core, and either the thermal
    /// network with the trap node free (<paramref name="pulsePhi"/> null: DC, S and harmonic balance), or the junction held
    /// at the chuck temperature and the trap state held at <paramref name="pulsePhi"/> (a pulse).
    /// </summary>
    public static string Device(int n = N0, double? pulsePhi = null)
    {
        double s = (double)n / N0;
        var sb = new StringBuilder(Core("gancore", s, pulsePhi is not null)).Append(Network("gannet", n));
        sb.Append("define gan (G D S)\ngannet:NW G D S gi di si\ngancore:CORE gi di si t p\n");
        if (pulsePhi is { } phi) sb.Append($"Vdc:VT t 0 V=0\nVdc:VP p 0 V={R(phi)}\n");
        else sb.Append($"R:RTH t 0 R={R(Rth / s)}\nC:CTH t 0 C={R(TauTh / Rth * s)}\n");
        return sb.Append("end\n").ToString();
    }

    /// <summary>The probe pads between probe nets <c>pg</c>/<c>pd</c> and the device's <c>g</c>/<c>d</c>.</summary>
    private static string Pads =>
        $"C:CPG pg 0 C={R(Cpad)}\nL:LPG pg g L={R(Lpad)}\nC:CPD pd 0 C={R(Cpad)}\nL:LPD pd d L={R(Lpad)}\n";

    // ── The trap state a pulse leaves ─────────────────────────────────────────

    /// <summary><c>dx/dt = −g(x)</c> for the gap <c>x = vds − phi2</c>: emission and the capture term, tangent-continued as
    /// the SDD continues it.</summary>
    private static double Rate(double x)
    {
        double u = (x - Xref) / Vcap;
        double e = u < 20 ? Math.Exp(u) : Math.Exp(20) * (u - 19);
        return x / TauEmit + Xref / TauCapt * (e - Math.Exp(-Xref / Vcap));
    }

    /// <summary>phi2 after <paramref name="duration"/> at a fixed <paramref name="vds"/>, from <paramref name="phi"/>: implicit
    /// Euler on log-spaced steps, each solved by bisection (g is increasing with g(0) = 0).</summary>
    public static double Evolve(double phi, double vds, double duration)
    {
        double x = vds - phi, t = 0, h = 1e-20;
        while (t < duration)
        {
            h = Math.Min(h, duration - t);
            double lo = Math.Min(0, x), hi = Math.Max(0, x), x0 = x;
            for (int k = 0; k < 200; k++)
            {
                double mid = 0.5 * (lo + hi);
                if (mid + h * Rate(mid) > x0) hi = mid; else lo = mid;
            }
            x = 0.5 * (lo + hi);
            t += h;
            h *= 1.05;
        }
        return vds - x;
    }

    /// <summary>The trap state a pulsed set's sample sees: set by its quiescent point (held long, so phi2 sits at it) or by a
    /// pre-pulse, then moved over the gap and the sample delay at the point's own intrinsic drain voltage.</summary>
    private static double PulsePhi(double settingVds, bool doublePulse, double vdsIntrinsic)
    {
        double phi = settingVds;
        if (doublePulse)
        {
            phi = Evolve(0, PreVds, PreWidth);    // the quiescent drain is 0 V
            phi = Evolve(phi, 0, PreGap);
        }
        return Evolve(phi, vdsIntrinsic, SampleDelay);
    }

    // ── Solving the truth ─────────────────────────────────────────────────────

    private sealed record Solved(double Ig, double Id, double Vgsi, double Vdsi, double Tj);

    private static Solved SolveDc(string cnl, double ambientC)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);
        Assert.True(dc.Converged, $"the truth's DC solve did not converge: {dc.Iterations} it, residual {dc.FinalResidual}\n{cnl}");
        var (rows, names) = nl.Nodes.ResultRows(excludeInternal: false);
        double V(string node)
        {
            int k = Array.IndexOf(names, node);
            return k < 0 ? 0 : dc.NodeVoltages[rows[k] - 1];
        }
        return new Solved(dc.ProbeCurrents["IPG"], dc.ProbeCurrents["IPD"],
                          V("X1.gi") - V("X1.si"), V("X1.di") - V("X1.si"), ambientC + V("X1.t"));
    }

    private static string DcBench(int n, double? phi, double ambientC, double vgs, double vds) =>
        Globals(ambientC) + Device(n, phi) +
        $"Vdc:VG vg 0 V={R(vgs)}\nIProbe:IPG vg g\ngan:X1 g d 0\nIProbe:IPD vd d\nVdc:VD vd 0 V={R(vds)}\n";

    /// <summary>One measured point and its intrinsic truth.</summary>
    public sealed record Point(double Vgs, double Vds, double Ig, double Id, double Vgsi, double Vdsi, double Phi, double Tj);

    /// <summary>DC I–V at the dynamic state: phi2 = vds and the junction heated.</summary>
    public static IReadOnlyList<Point> Dc(double[] vgsList, double[] vdsList, double ambientC, int n = N0) =>
        [.. from g in vgsList from d in vdsList let s = SolveDc(DcBench(n, null, ambientC, g, d), ambientC)
            select new Point(g, d, s.Ig, s.Id, s.Vgsi, s.Vdsi, s.Vdsi, s.Tj)];

    /// <summary>
    /// Pulsed I–V at the chuck temperature from quiescent drain voltage <paramref name="quiescentVds"/>, or from a pre-pulse
    /// to <see cref="PreVds"/>: the trap state the pulse leaves found by iterating it with the point's intrinsic drain
    /// voltage.
    /// </summary>
    public static IReadOnlyList<Point> Pulsed(double[] vgsList, double[] vdsList, double ambientC, double quiescentVds, bool doublePulse)
    {
        var points = new List<Point>();
        foreach (double g in vgsList)
            foreach (double d in vdsList)
            {
                double phi = doublePulse ? PreVds : quiescentVds;
                Solved s = SolveDc(DcBench(N0, phi, ambientC, g, d), ambientC);
                for (int it = 0; it < 4; it++)
                {
                    double next = PulsePhi(quiescentVds, doublePulse, s.Vdsi);
                    if (Math.Abs(next - phi) < 1e-9) break;
                    phi = next;
                    s = SolveDc(DcBench(N0, phi, ambientC, g, d), ambientC);
                }
                points.Add(new Point(g, d, s.Ig, s.Id, s.Vgsi, s.Vdsi, phi, ambientC));
            }
        return points;
    }

    /// <summary>S (50 Ω) at the probe plane — pads included — at each DC bias, through ideal tees.</summary>
    public static (IReadOnlyList<Point> Points, Complex[][,] S) SParams(double[] vgsList, double[] vdsList, double ambientC, double[] freqs)
    {
        var points = new List<Point>();
        var s = new List<Complex[,]>();
        foreach (double g in vgsList)
            foreach (double d in vdsList)
            {
                string cnl = Globals(ambientC) + Device() + Pads + "gan:X1 g d 0\n" +
                    $"Vdc:VG vg 0 V={R(g)}\nIProbe:IPG vg lg\nL:LG lg pg L=1\nC:CG pa pg C=1\nPort:P1 pa 0 Num=1 Z=50 Ohm\n" +
                    $"Vdc:VD vd 0 V={R(d)}\nIProbe:IPD vd ld\nL:LD ld pd L=1\nC:CD pb pd C=1\nPort:P2 pb 0 Num=2 Z=50 Ohm\n";
                var dc = SolveDc(cnl, ambientC);
                points.Add(new Point(g, d, dc.Ig, dc.Id, dc.Vgsi, dc.Vdsi, dc.Vdsi, dc.Tj));
                var (lib, tb) = new CnlReader().Read(cnl);
                using var nl = new Elaborator(lib).Elaborate(tb);
                var sp = SParameterEngine.Run(nl, freqs)["S"];
                var m = new Complex[freqs.Length, 4];
                for (int k = 0; k < freqs.Length; k++)
                    for (int i = 0; i < 2; i++)
                        for (int j = 0; j < 2; j++)
                            m[k, 2 * i + j] = (Complex)sp[k, i, j];
                s.Add(m);
            }
        return (points, [.. s]);
    }

    /// <summary>The open and short dummies: the pads alone, and the pads with their device ends grounded.</summary>
    public static (Complex[,] Open, Complex[,] Short) PadDummies(double[] freqs)
    {
        Complex[,] Run(string body)
        {
            var (lib, tb) = new CnlReader().Read(body + "Port:P1 pg 0 Num=1 Z=50 Ohm\nPort:P2 pd 0 Num=2 Z=50 Ohm\n");
            using var nl = new Elaborator(lib).Elaborate(tb);
            var sp = SParameterEngine.Run(nl, freqs)["S"];
            var m = new Complex[freqs.Length, 4];
            for (int k = 0; k < freqs.Length; k++)
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                        m[k, 2 * i + j] = (Complex)sp[k, i, j];
            return m;
        }
        return (Run(Pads + "R:RG g 0 R=1e12\nR:RD d 0 R=1e12\n"), Run(Pads + "R:RG g 0 R=1e-6\nR:RD d 0 R=1e-6\n"));
    }

    // ── The raw files ─────────────────────────────────────────────────────────

    public static readonly double[] DcVgs = [-5.0, -4.0, -3.0, -2.0, -1.6, -1.2, -0.9, -0.6, -0.3, 0.0, 0.3, 0.6, 0.8, 1.0, 1.4];
    public static readonly double[] DcVds = [0, 0.5, 1, 2, 3, 5, 8, 12, 16, 20];
    public static readonly double[] PulsedVgs = [-5.0, -4.0, -3.0, -2.0, -1.6, -1.2, -0.8, -0.4, 0.0, 0.4, 0.8, 1.0, 1.4];
    public static readonly double[] PulsedVds = [0, 1, 2, 4, 6, 8, 10, 14, 20];
    public static readonly double[] SVgs = [-2.0, -1.6, -1.2, -0.8, -0.4, 0.0, 0.3];
    public static readonly double[] SVds = [0.5, 1, 2, 4, 7, 10, 15];
    public static readonly double[] Freqs = [0.5e9, 1e9, 2e9, 3e9, 4e9, 6e9, 8e9, 10e9, 12e9, 15e9, 18e9, 20e9];

    /// <summary>The pulsed sets: name, chuck temperature, quiescent drain voltage, double pulse.</summary>
    public static readonly (string Name, double AmbientC, double Vq, bool DoublePulse)[] PulsedSets =
    [
        ("pulsed_q0_25C", 25, 0, false), ("pulsed_q0_125C", 125, 0, false),
        ("pulsed_q10_25C", 25, 10, false), ("pulsed_q20_25C", 25, 20, false), ("pulsed_dp20_25C", 25, 0, true),
    ];

    public static readonly double[] STemperatures = [25, 85];

    /// <summary>
    /// Writes the raw files of <c>synthetic-gan</c> into <paramref name="dir"/>: <c>pads/open.s2p</c> and <c>short.s2p</c>,
    /// <c>dc/dc_25C.csv</c>, <c>pulsed/*.csv</c>, and one Touchstone file per bias in <c>s_25C/</c> and <c>s_85C/</c>, named
    /// by bias and drain current. The same bytes every call.
    /// </summary>
    public static void WriteRaw(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "pads"));
        var (open, shrt) = PadDummies(Freqs);
        File.WriteAllText(Path.Combine(dir, "pads", "open.s2p"), Touchstone(Freqs, open, "Open dummy: the probe pads alone (synthetic)."));
        File.WriteAllText(Path.Combine(dir, "pads", "short.s2p"), Touchstone(Freqs, shrt, "Short dummy: the probe pads, device ends grounded (synthetic)."));

        Directory.CreateDirectory(Path.Combine(dir, "dc"));
        File.WriteAllText(Path.Combine(dir, "dc", "dc_25C.csv"), Csv(Dc(DcVgs, DcVds, 25)));

        Directory.CreateDirectory(Path.Combine(dir, "pulsed"));
        foreach (var (name, t, vq, dp) in PulsedSets)
            File.WriteAllText(Path.Combine(dir, "pulsed", name + ".csv"), Csv(Pulsed(PulsedVgs, PulsedVds, t, vq, dp)));

        foreach (double t in STemperatures)
        {
            string sub = Path.Combine(dir, $"s_{R(t)}C");
            Directory.CreateDirectory(sub);
            var (points, s) = SParams(SVgs, SVds, t, Freqs);
            for (int p = 0; p < points.Count; p++)
            {
                var b = points[p];
                string name = $"Vgs{F(b.Vgs)}_Vds{F(b.Vds)}_Id{(1e3 * b.Id).ToString("F6", CultureInfo.InvariantCulture)}.s2p";
                File.WriteAllText(Path.Combine(sub, name), Touchstone(Freqs, s[p], null));
            }
        }
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Csv(IEnumerable<Point> points)
    {
        var sb = new StringBuilder("Vgs,Vds,Ig,Id\n");
        foreach (var p in points)
            sb.Append(string.Join(',', new[] { p.Vgs, p.Vds, p.Ig, p.Id }.Select(v => v.ToString("G10", CultureInfo.InvariantCulture)))).Append('\n');
        return sb.ToString();
    }

    private static string Touchstone(double[] f, Complex[,] s, string? comment)
    {
        var sb = new StringBuilder();
        if (comment is not null) sb.Append("! ").Append(comment).Append('\n');
        sb.Append("# Hz S RI R 50\n");
        for (int k = 0; k < f.Length; k++)
        {
            sb.Append(f[k].ToString("R", CultureInfo.InvariantCulture));
            foreach (int ij in new[] { 0, 2, 1, 3 })     // S11 S21 S12 S22
                sb.Append(' ').Append(s[k, ij].Real.ToString("G12", CultureInfo.InvariantCulture))
                  .Append(' ').Append(s[k, ij].Imaginary.ToString("G12", CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ── simulated-tables ──────────────────────────────────────────────────────

    public static readonly double[] TableVgs = [-2.0, -1.6, -1.2, -0.8, -0.4, 0.0, 0.4];
    public static readonly double[] TableVds = [0, 0.5, 1, 2, 4, 6, 8, 10];
    /// <summary>The low frequency the tables' Y is at.</summary>
    public const double TableFrequency = 1e9;

    /// <summary>
    /// The tables another simulator writes for <see cref="ComposedTestModels"/>' device inside the GaN network at its stock
    /// values: per bias the terminal voltages and currents, the intrinsic voltages its internal nodes carry, and Y at
    /// <see cref="TableFrequency"/> — one circuitRF grouped <c>.npy</c>.
    /// </summary>
    public static void WriteTables(string path)
    {
        int n = TableVgs.Length * TableVds.Length;
        var s = SyntheticFetData.BiasedS(SyntheticFetData.NetworkDevice, TableVgs, TableVds, [TableFrequency], ("X1.gi", "X1.di", "X1.si"));
        var y = new Complex[n * 4];
        for (int p = 0; p < n; p++)
        {
            Complex s11 = s.SAt(p, 0, 0, 0), s12 = s.SAt(p, 0, 0, 1), s21 = s.SAt(p, 0, 1, 0), s22 = s.SAt(p, 0, 1, 1);
            Complex den = (1 + s11) * (1 + s22) - s12 * s21;
            y[4 * p] = ((1 - s11) * (1 + s22) + s12 * s21) / den / 50;
            y[4 * p + 1] = -2 * s12 / den / 50;
            y[4 * p + 2] = -2 * s21 / den / 50;
            y[4 * p + 3] = ((1 + s11) * (1 - s22) + s12 * s21) / den / 50;
        }
        var point = new Axis("point", [.. Enumerable.Range(0, n).Select(i => (double)i)]);
        var ds = new DataSet();
        foreach (var (name, get) in new (string, Func<BiasPoint, double>)[]
                 {
                     ("vgs", p => p.Vgs), ("vds", p => p.Vds), ("ig", p => p.Ig), ("id", p => p.Id),
                     ("vgsi", p => p.VgsIntrinsic), ("vdsi", p => p.VdsIntrinsic),
                 })
            ds.Add(name, new DataCube([point], [.. s.Points.Select(get)]));
        var two = new Axis("port", [1, 2]);
        ds.Add("Y", new DataCube([point, new Axis("freq", [TableFrequency], "Hz"), two, two], y));
        ds.Add("freq", new DataCube([new Axis("index", [0])], [TableFrequency]));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        DataSetExporter.ExportNpy(ds, stream);
    }
}
