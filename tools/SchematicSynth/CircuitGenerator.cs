using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;

namespace CircuitRF.SchematicSynth;

/// <summary>A generated circuit: its netlist text (the truth written beside the picture) and what reading it gave.</summary>
public sealed record GeneratedCircuit(string Netlist, Library Library, TestBench TestBench, IReadOnlyList<string> Recipes);

/// <summary>
/// Random circuits that look designed (brief-iml-2 R-iml2-1): a weighted set of topology recipes, each one what RF
/// schematics actually contain, chained between a source and a load. Values come from realistic ranges in circuitRF's
/// own spellings; designators are numbered as the editor numbers them (the palette prefix and the next free number).
///
/// <para>The circuit is written as <c>.cnl</c> text and READ, so the truth is exactly what the netlist reader makes of
/// it — the same object every other path in circuitRF draws and extracts from.</para>
/// </summary>
public static class CircuitGenerator
{
    /// <summary>The recipes and their weights. A mixture joins two to four of the others.</summary>
    public static readonly (string Name, double Weight)[] Recipes =
    [
        ("ladder", 14), ("pad", 6), ("l-section", 7), ("match", 12), ("tline-net", 12), ("amp-stage", 13),
        ("block-chain", 15), ("snp", 7), ("detector", 4), ("mixture", 16),
    ];

    public static GeneratedCircuit Next(Random r)
    {
        var b = new Builder(r);
        string first = Pick(r, Recipes);
        var used = new List<string>();

        string net = b.Source();
        if (first == "mixture")
        {
            int n = r.Next(2, 5);
            var simple = Recipes.Where(x => x.Name != "mixture").ToArray();
            for (int k = 0; k < n; k++)
            {
                string recipe = Pick(r, simple);
                used.Add(recipe);
                net = b.Run(recipe, net, compact: true);
            }
        }
        else
        {
            used.Add(first);
            net = b.Run(first, net, compact: false);
        }
        b.Load(net);
        if (r.NextDouble() < 0.06) b.Variables();

        string text = b.Text();
        var (lib, tb) = new CnlReader().Read(text, "tb");
        return new GeneratedCircuit(text, lib, tb, used);
    }

    private static string Pick(Random r, (string Name, double Weight)[] table)
    {
        double total = table.Sum(t => t.Weight), x = r.NextDouble() * total;
        foreach (var (name, w) in table)
        {
            if (x < w) return name;
            x -= w;
        }
        return table[^1].Name;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private sealed class Builder(Random r)
    {
        private readonly List<string> _lines = [];
        private readonly List<string> _vars = [];
        private readonly Dictionary<string, int> _numbers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _nets = new(StringComparer.Ordinal);
        private int _port;
        private int _auto;

        private static readonly string[] SignalNames =
            ["in", "out", "rf", "rfin", "rfout", "ant", "a", "b", "mid", "x", "match", "lo", "if", "tx", "rx", "drv"];

        /// <summary>A fresh net name: one a designer would type sometimes, otherwise an automatic nN.</summary>
        public string Net(string? hint = null)
        {
            if (hint is not null && _nets.Add(hint)) return hint;
            if (r.NextDouble() < 0.25)
            {
                string n = SignalNames[r.Next(SignalNames.Length)];
                if (_nets.Add(n)) return n;
            }
            string auto;
            do auto = "n" + (++_auto).ToString(CultureInfo.InvariantCulture);
            while (!_nets.Add(auto));
            return auto;
        }

        /// <summary>The editor's numbering: the palette prefix and the next free number for it.</summary>
        private string Name(SymbolKind kind)
        {
            string prefix = ComponentTypeRegistry.InstancePrefix(kind);
            _numbers[prefix] = _numbers.GetValueOrDefault(prefix) + 1;
            return prefix + _numbers[prefix].ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The system blocks bind each port as a (+, −) pair; drawn, each port's minus is on ground.</summary>
        private static readonly HashSet<SymbolKind> PairedPorts =
        [
            SymbolKind.Amp, SymbolKind.Filter, SymbolKind.Atten, SymbolKind.Switch, SymbolKind.SwitchD,
            SymbolKind.Circulator, SymbolKind.Coupler, SymbolKind.Balun, SymbolKind.Mixer,
        ];

        private void Add(SymbolKind kind, string[] nets, params string[] parameters)
        {
            if (PairedPorts.Contains(kind)) nets = [.. nets.SelectMany(n => new[] { n, "0" })];
            string type = ComponentTypeRegistry.EngineReference(kind);
            var sb = new StringBuilder();
            sb.Append(type).Append(':').Append(Name(kind));
            foreach (var n in nets) sb.Append(' ').Append(n);
            foreach (var p in parameters) if (p.Length > 0) sb.Append(' ').Append(p);
            _lines.Add(sb.ToString());
        }

        public string Text()
        {
            var sb = new StringBuilder();
            foreach (var v in _vars) sb.Append(v).Append('\n');
            foreach (var l in _lines) sb.Append(l).Append('\n');
            return sb.ToString();
        }

        // ── ends ─────────────────────────────────────────────────────────────

        public string Source()
        {
            string n = Net(r.NextDouble() < 0.6 ? "in" : null);
            double x = r.NextDouble();
            if (x < 0.80) Port(n);
            else if (x < 0.95)
                Add(SymbolKind.P1Tone, [n, "0"], $"Num={++_port}", Maybe(0.7, $"Pavl={Pick([-20, -10, 0, 5, 10, 13, 20])} dBm"),
                    $"Z=50 {Ohm()}", Maybe(0.8, $"Freq={V.Freq(r)}"));
            else
                Add(SymbolKind.CurrentToneSource, [n, "0"], $"I={Pick([1, 2, 5, 10, 20])} mA", Maybe(0.7, $"Freq={V.Freq(r)}"));
            return n;
        }

        public void Load(string net)
        {
            if (r.NextDouble() < 0.88) Port(net);
            else Add(SymbolKind.Resistor, [net, "0"], $"R=50 {Ohm()}");
        }

        private void Port(string net) =>
            Add(SymbolKind.Term, [net, "0"], $"Num={++_port}", $"Z={Pick([50, 50, 50, 50, 75, 25, 100])} {Ohm()}");

        public void Variables()
        {
            int n = r.Next(1, 4);
            string[] names = ["f0", "Z0", "Vcc", "Pin", "Lm", "Cm", "Rb"];
            foreach (var name in names.OrderBy(_ => r.Next()).Take(n))
                _vars.Add(name switch
                {
                    "f0" => $"f0 = {V.Num(r, 0.5, 10)} GHz",
                    "Z0" => "Z0 = 50",
                    "Vcc" => $"Vcc = {Pick([3.3, 5, 12, 2.8])}",
                    "Pin" => $"Pin = {Pick([-10, 0, 5])}",
                    "Lm" => $"Lm = {V.Num(r, 1, 30)}",
                    "Cm" => $"Cm = {V.Num(r, 0.5, 10)}",
                    _ => $"Rb = {V.Num(r, 100, 10000)}",
                });
        }

        public string Run(string recipe, string a, bool compact) => recipe switch
        {
            "ladder" => Ladder(a, compact ? r.Next(2, 4) : SectionCount()),
            "pad" => Pad(a),
            "l-section" => LSection(a),
            "match" => Match(a),
            "tline-net" => TlineNet(a),
            "amp-stage" => AmpStage(a),
            "block-chain" => BlockChain(a, compact ? r.Next(1, 3) : r.Next(2, 6)),
            "snp" => SnpBlock(a),
            "detector" => Detector(a),
            _ => throw new ArgumentException(recipe),
        };

        private int SectionCount() => Pick([2, 2, 3, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9]);

        // ── recipes ──────────────────────────────────────────────────────────

        private string Ladder(string a, int sections)
        {
            int form = r.Next(3);   // 0 low-pass, 1 high-pass, 2 band-pass
            string net = a;
            bool seriesFirst = r.NextDouble() < 0.5;
            for (int k = 0; k < sections; k++)
            {
                bool series = (k % 2 == 0) == seriesFirst;
                if (series)
                {
                    string next = Net();
                    if (form == 0) Ind(net, next);
                    else if (form == 1) Cap(net, next);
                    else
                    {
                        string m = Net();
                        Ind(net, m);
                        Cap(m, next);
                    }
                    net = next;
                }
                else
                {
                    if (form == 0) Cap(net, "0");
                    else if (form == 1) Ind(net, "0");
                    else
                    {
                        Ind(net, "0");
                        Cap(net, "0");
                    }
                }
            }
            return net;
        }

        private string Pad(string a)
        {
            string b = Net();
            double rs = V.Resistance(r, 5, 300), rp = V.Resistance(r, 30, 1000);
            if (r.NextDouble() < 0.5)
            {
                Res(a, "0", rp);
                Res(a, b, rs);
                Res(b, "0", rp);
            }
            else
            {
                string m = Net();
                Res(a, m, rs);
                Res(m, "0", rp);
                Res(m, b, rs);
            }
            return b;
        }

        private string LSection(string a)
        {
            string b = Net();
            bool shuntFirst = r.NextDouble() < 0.5;
            if (shuntFirst) Shunt(a);
            if (r.NextDouble() < 0.5) Ind(a, b); else Cap(a, b);
            if (!shuntFirst) Shunt(b);
            return b;
        }

        private void Shunt(string net)
        {
            if (r.NextDouble() < 0.5) Cap(net, "0"); else Ind(net, "0");
        }

        private string Match(string a)
        {
            string net = a;
            int n = r.Next(1, 5);
            for (int k = 0; k < n; k++)
            {
                switch (r.Next(7))
                {
                    case 0: { string b = Net(); Ind(net, b); net = b; break; }
                    case 1: { string b = Net(); Cap(net, b); net = b; break; }
                    case 2: Ind(net, "0"); break;
                    case 3: Cap(net, "0"); break;
                    case 4: Tlin(net, Net("nc" + (++_auto).ToString(CultureInfo.InvariantCulture))); break;   // open stub
                    case 5: Tlin(net, "0"); break;                                                            // shorted stub
                    default: { string b = Net(); Tlin(net, b); net = b; break; }                               // quarter-wave
                }
            }
            return net;
        }

        private string TlineNet(string a)
        {
            string net = a;
            int n = r.Next(2, 6);
            for (int k = 0; k < n; k++)
            {
                switch (r.Next(6))
                {
                    case 0:
                    {
                        string b = Net(), c = Net(), stub = Net();
                        Add(SymbolKind.MTee, [net, b, stub], $"W1={V.Width(r)}", $"W2={V.Width(r)}", $"W3={V.Width(r)}");
                        Stub(stub);
                        Mlin(b, c);
                        net = c;
                        break;
                    }
                    case 1:
                    {
                        string b = Net(), s1 = Net(), s2 = Net();
                        Add(SymbolKind.MCross, [b, s1, net, s2], $"W1={V.Width(r)}", $"W2={V.Width(r)}", $"W3={V.Width(r)}", $"W4={V.Width(r)}");
                        Stub(s1);
                        Stub(s2);
                        net = b;
                        break;
                    }
                    case 2:
                    {
                        string b = Net();
                        Add(SymbolKind.MBend, [net, b], $"W={V.Width(r)}", Maybe(0.5, "Angle=90 deg"));
                        net = b;
                        break;
                    }
                    case 3:
                    {
                        string b = Net();
                        Add(SymbolKind.Mtaper, [net, b], $"W1={V.Width(r)}", $"W2={V.Width(r)}", $"L={V.Length(r)}");
                        net = b;
                        break;
                    }
                    default:
                    {
                        string b = Net();
                        Mlin(net, b);
                        net = b;
                        break;
                    }
                }
            }
            return net;
        }

        /// <summary>A microstrip stub: open, through a via to ground, or ending on a capacitor to ground.</summary>
        private void Stub(string at)
        {
            double x = r.NextDouble();
            if (x < 0.4) { Mlin(at, Net("nc" + (++_auto).ToString(CultureInfo.InvariantCulture))); return; }
            string end = Net();
            Mlin(at, end);
            if (x < 0.8) Add(SymbolKind.ViaGnd, [end, "0"], Maybe(0.7, $"Drill={Pick([0.2, 0.25, 0.3, 0.4])} mm"));
            else Cap(end, "0");
        }

        private string AmpStage(string a)
        {
            string vdd = Net(Pick(["vdd", "vcc", "vd", "vbias", "vs"]));
            string ctl = Net(Pick(["gate", "base", "g", "bias", "vg"])), drain = Net(Pick(["drain", "collector", "d", "c1"])), outNet = Net();
            Cap(a, ctl);                                                    // DC block in
            int dev = r.Next(4);
            var kind = dev switch { 0 => SymbolKind.BjtNpn, 1 => SymbolKind.BjtPnp, 2 => SymbolKind.FetCurtice, _ => SymbolKind.PFetCurtice };
            bool bjt = dev < 2;
            string common = r.NextDouble() < 0.65 ? "0" : Net(Pick(["e", "src", "emitter", "s"]));
            // pins: BJT c,b,e — FET g,d,s
            if (bjt) Add(kind, [drain, ctl, common], DeviceParams(kind));
            else Add(kind, [ctl, drain, common], DeviceParams(kind));
            if (common != "0")
            {
                Res(common, "0", V.Resistance(r, 1, 100));
                if (r.NextDouble() < 0.6) Cap(common, "0");
            }

            // bias: a divider (BJT) or a gate resistor from a bias supply (FET)
            if (bjt)
            {
                Res(vdd, ctl, V.Resistance(r, 1000, 100000));
                if (r.NextDouble() < 0.7) Res(ctl, "0", V.Resistance(r, 1000, 47000));
            }
            else
            {
                string vg = Net(Pick(["vgg", "vneg", "vg1"]));
                Res(vg, ctl, V.Resistance(r, 100, 10000));
                Add(SymbolKind.Vdc, [vg, "0"], $"Vdc={Pick(["-0.5", "-1", "-1.2", "0.6", "-2"])} V");
                if (r.NextDouble() < 0.5) Cap(vg, "0");
            }

            Ind(vdd, drain, choke: true);                                   // RF choke
            Cap(drain, outNet);                                             // DC block out
            Add(SymbolKind.Vdc, [vdd, "0"], $"Vdc={Pick(["3.3", "5", "12", "2.8", "28", "8"])} V");
            int decoupling = r.Next(1, 3);
            for (int k = 0; k < decoupling; k++) Cap(vdd, "0");
            if (r.NextDouble() < 0.15) Add(SymbolKind.Diode, [ctl, "0"], DiodeParams());
            return outNet;
        }

        private string Detector(string a)
        {
            string b = Net(Pick(["det", "vout", "video", "dc"]));
            if (r.NextDouble() < 0.6)
            {
                Add(SymbolKind.Diode, [a, b], DiodeParams());
                Cap(b, "0");
                Res(b, "0", V.Resistance(r, 1000, 100000));
            }
            else
            {
                // a limiter: back-to-back shunt diodes
                Add(SymbolKind.Diode, [a, "0"], DiodeParams());
                Add(SymbolKind.Diode, ["0", a], DiodeParams());
                Cap(a, b);
            }
            return b;
        }

        private string BlockChain(string a, int n)
        {
            string net = a;
            for (int k = 0; k < n; k++)
            {
                string b = Net();
                switch (r.Next(9))
                {
                    case 0:
                    case 1:
                        Add(SymbolKind.Amp, [net, b], $"Gain={Pick([10, 12, 15, 18, 20, 25, 30])} dB",
                            Maybe(0.6, $"IP3={Pick([25, 30, 35, 40])} dBm"));
                        break;
                    case 2:
                        Add(SymbolKind.Filter, [net, b], Maybe(0.6, $"Form={Pick(["Bandpass", "Lowpass", "Highpass"])}"),
                            Maybe(0.5, $"Order={r.Next(3, 8)}"));
                        break;
                    case 3:
                        Add(SymbolKind.Atten, [net, b], $"Loss={Pick([3, 6, 10, 20])} dB");
                        break;
                    case 4:
                        if (r.NextDouble() < 0.5) Add(SymbolKind.Switch, [net, b], $"State={r.Next(2)}");
                        else
                        {
                            string other = Net();
                            Add(SymbolKind.SwitchD, [net, b, other], $"State={r.Next(1, 3)}");
                            Terminate(other);
                        }
                        break;
                    case 5:
                    {
                        string third = Net();
                        Add(SymbolKind.Circulator, [net, b, third], Maybe(0.4, $"Direction={Pick(["CW", "CCW"])}"));
                        Terminate(third);
                        break;
                    }
                    case 6:
                    {
                        string c3 = Net(), c4 = Net();
                        Add(SymbolKind.Coupler, [net, b, c3, c4], $"Coupling={Pick([6, 10, 15, 20, 30])} dB");
                        Terminate(c3);
                        Terminate(c4);
                        break;
                    }
                    case 7:
                    {
                        // The unbalanced side in, BAL+ on along the chain, BAL- terminated.
                        string m = Net();
                        Add(SymbolKind.Balun, [net, b, m], Maybe(0.3, $"Zbal={Pick([100, 200, 50])} {Ohm()}"));
                        Terminate(m);
                        break;
                    }
                    default:
                    {
                        string lo = Net(Pick(["lo", "lo_in", "osc"]));
                        Add(SymbolKind.Mixer, [net, lo, b], Maybe(0.7, $"ConvGain={Pick([-6, -7, -8, -10])} dB"),
                            Maybe(0.5, $"Plo={Pick([7, 10, 13, 17])} dBm"));
                        if (r.NextDouble() < 0.6)
                            Add(SymbolKind.P1Tone, [lo, "0"], $"Num={++_port}", $"Pavl={Pick([7, 10, 13])} dBm", $"Z=50 {Ohm()}");
                        else Port(lo);
                        break;
                    }
                }
                net = b;
            }
            return net;
        }

        private void Terminate(string net)
        {
            if (r.NextDouble() < 0.6) Res(net, "0", 50);
            else Port(net);
        }

        private string SnpBlock(string a)
        {
            // Two ports and up: a one-port block draws one pin, and a perimeter class is labelled with two at least.
            int n = Pick([2, 2, 2, 3, 4]);
            string file = n switch
            {
                2 => Pick(["\"amp.s2p\"", "\"filter.s2p\"", "\"lna.s2p\"", "\"part.s2p\"", "\"bpf_2g4.s2p\""]),
                3 => Pick(["\"splitter.s3p\"", "\"diplexer.s3p\""]),
                _ => Pick(["\"coupler.s4p\"", "\"hybrid.s4p\""]),
            };
            if (r.NextDouble() < 0.5) a = LSection(a);
            var nets = new List<string> { a };
            string outNet = Net();
            nets.Add(outNet);
            for (int k = 2; k < n; k++) nets.Add(Net());
            Add(SymbolKind.Snp, [.. nets], $"File={file}", $"NumPorts={n}");
            for (int k = 2; k < n; k++) Terminate(nets[k]);
            return outNet;
        }

        // ── parts ────────────────────────────────────────────────────────────

        private void Res(string a, string b, double ohms) => Add(SymbolKind.Resistor, [a, b], "R=" + V.Ohms(r, ohms));
        private void Cap(string a, string b) => Add(SymbolKind.Capacitor, [a, b], "C=" + V.Farads(r));
        private void Ind(string a, string b, bool choke = false) => Add(SymbolKind.Inductor, [a, b], "L=" + V.Henries(r, choke));
        private void Mlin(string a, string b) => Add(SymbolKind.Mlin, [a, b], $"W={V.Width(r)}", $"L={V.Length(r)}");

        private void Tlin(string a, string b) =>
            Add(SymbolKind.Tline, [a, b], $"Z={V.Num(r, 20, 120, 0)} {Ohm()}", $"E={Pick([15, 30, 45, 60, 90, 90, 90, 120, 180])}",
                Maybe(0.8, $"F={V.Freq(r)}"));

        private string[] DeviceParams(SymbolKind kind)
        {
            var shown = ComponentTypeRegistry.DefaultParameters(kind, 0).Where(p => p.ShowOnSchematic).ToList();
            return [.. shown.Where(_ => r.NextDouble() < 0.6).Select(p => p.Name + "=" + p.Expression + (p.Unit is { Length: > 0 } u ? " " + u : ""))];
        }

        private string[] DiodeParams()
        {
            var list = new List<string>();
            if (r.NextDouble() < 0.6) list.Add($"Is={Pick(["1e-14", "5e-12", "2e-9"])} A");
            if (r.NextDouble() < 0.4) list.Add($"N={Pick(["1", "1.05", "1.2"])}");
            if (r.NextDouble() < 0.5) list.Add($"Rs={V.Num(r, 1, 20, 1)} {Ohm()}");
            if (r.NextDouble() < 0.4) list.Add($"Cj0={V.Num(r, 0.05, 1, 2)} pF");
            return [.. list];
        }

        /// <summary>The netlist reader's spelling; <see cref="Drawings"/> turns some into the palette's Ω once drawn.</summary>
        private static string Ohm() => "Ohm";
        private string Maybe(double p, string s) => r.NextDouble() < p ? s : "";
        private T Pick<T>(T[] options) => options[r.Next(options.Length)];
    }

    /// <summary>Values in circuitRF's own spellings.</summary>
    private static class V
    {
        private static readonly double[] E12 = [1.0, 1.2, 1.5, 1.8, 2.2, 2.7, 3.3, 3.9, 4.7, 5.6, 6.8, 8.2];

        private static double LogUniform(Random r, double lo, double hi) => Math.Exp(Math.Log(lo) + r.NextDouble() * (Math.Log(hi) - Math.Log(lo)));

        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>A number from [lo, hi] at a designer's precision: an E12 value, a round one or a computed one.</summary>
        public static string Num(Random r, double lo, double hi, int? decimals = null)
        {
            double v = LogUniform(r, lo, hi);
            if (decimals is { } d) return Math.Round(v, d).ToString("0.####", CultureInfo.InvariantCulture);
            double x = r.NextDouble();
            if (x < 0.55) return F(E12Value(v));
            if (x < 0.8) return F(Math.Round(v, v < 10 ? 1 : 0));
            return Sig(v, 3);
        }

        private static double E12Value(double v)
        {
            double decade = Math.Pow(10, Math.Floor(Math.Log10(v)));
            double m = v / decade;
            return E12.MinBy(e => Math.Abs(e - m)) * decade;
        }

        private static string Sig(double v, int digits)
        {
            int places = Math.Max(0, digits - 1 - (int)Math.Floor(Math.Log10(Math.Abs(v))));
            return Math.Round(v, places).ToString("0." + new string('#', Math.Max(places, 1)), CultureInfo.InvariantCulture);
        }

        public static double Resistance(Random r, double lo, double hi) => E12Value(LogUniform(r, lo, hi));

        public static string Ohms(Random r, double ohms)
        {
            const string unit = "Ohm";
            if (ohms >= 1000 && r.NextDouble() < 0.85) return F(ohms / 1000) + " k" + unit;
            return F(ohms) + " " + unit;
        }

        public static string Farads(Random r)
        {
            double pf = LogUniform(r, 0.1, 10000);
            if (pf >= 1000 && r.NextDouble() < 0.8) return Num(r, pf / 1000 * 0.999, pf / 1000 * 1.001) + " nF";
            return Num(r, pf * 0.999, pf * 1.001) + " pF";
        }

        public static string Henries(Random r, bool choke)
        {
            double nh = choke ? LogUniform(r, 22, 2200) : LogUniform(r, 0.2, 120);
            if (nh >= 1000) return Num(r, nh / 1000 * 0.999, nh / 1000 * 1.001) + " uH";
            return Num(r, nh * 0.999, nh * 1.001) + " nH";
        }

        public static string Width(Random r) => r.NextDouble() < 0.85
            ? Num(r, 0.1, 3, 2) + " mm"
            : Num(r, 50, 600, 0) + " um";

        public static string Length(Random r) => Num(r, 0.5, 30, 1) + " mm";

        public static string Freq(Random r) => r.NextDouble() < 0.85
            ? Num(r, 0.3, 40, 1) + " GHz"
            : Num(r, 50, 900, 0) + " MHz";
    }
}
