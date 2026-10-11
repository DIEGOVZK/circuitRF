// A plot picture's flags, as `recognize` reads them — brief-img-14-sparameter-plots-to-touchstone.md R-im14-14.
//
// THIS FILE OWNS NO PLOT READING (R-im14-15). Every flag here is the spelling of one field of PlotReadingOptions or
// TouchstoneAssemblyOptions — the options the dialog's rows fill in — and nothing more. The reading is PlotReading's,
// the file TouchstoneAssembly's, the CSV PlotCsv's.
//
// EVERY PICTURE COORDINATE IS IN PIXELS, as --region's are: --x-axis takes `120px=1GHz,900px=5GHz` (the `px` is optional
// here, because the value side carries the meaning and nothing else on this flag could be a length).

using System.Globalization;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Design.Layout.Recognition;
using RfCore;

namespace CircuitRF.Cli;

internal sealed class PlotFlags
{
    public readonly List<int> Plots = [];
    public readonly List<(int Plot, char Axis, double P0, double V0, double P1, double V1)> Clicks = [];
    public readonly List<(int Plot, char Axis, bool Log)> Scales = [];
    public readonly List<PlotTraceOverride> Traces = [];
    public readonly List<PlotFrequencyOverride> Frequencies = [];
    public readonly Dictionary<(int I, int J), PhaseRule> Phase = [];
    public readonly Dictionary<(int I, int J), FillRule> Fill = [];
    public readonly Dictionary<(int I, int J), string> Prefer = [];
    public double? SmithZ0;
    public AssemblyGrid? Grid;
    public MatrixFormat? Format;
    public int? Ports;
    public string? Csv;
    public bool KeepSources = true;

    /// <summary>The plot flags given, by spelling — refused on any other kind of input.</summary>
    public readonly List<string> Given = [];

    /// <summary>Reads <c>args[i]</c> when it is a plot flag. Null when it is not one, 0 when taken, else a refusal's
    /// exit code.</summary>
    public int? Take(string verb, string[] args, ref int i)
    {
        string a = args[i];
        // The two bare flags first: they take no value.
        if (a is "--x-log" or "--y-log")
        {
            Scales.Add((0, a[2], true));
            Given.Add(a);
            return 0;
        }
        if (i + 1 >= args.Length) return null;
        string v = args[i + 1];
        int? Bad(string expected) => JsonRun.Fail(CliDiagnostics.PictureBadValue(verb, a, v, expected));
        switch (a)
        {
            case "--plot":
                foreach (string part in v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                        return Bad("the session's plot numbers to read, comma-separated (1,3)");
                    Plots.Add(n);
                }
                break;
            case "--x-axis" or "--y-axis":
            {
                char axis = a[2];
                var (text, plot) = At(v);
                var pairs = text.Split(',', StringSplitOptions.TrimEntries);
                var parsed = new List<(double P, double V)>();
                foreach (string pair in pairs)
                {
                    int eq = pair.IndexOf('=');
                    if (eq < 0 || Px(pair[..eq]) is not { } px || AxisValue(pair[(eq + 1)..], axis) is not { } value) { parsed.Clear(); break; }
                    parsed.Add((px, value));
                }
                if (parsed.Count != 2 || plot is null)
                    return Bad(axis == 'x'
                        ? "<px>=<value>,<px>=<value>[@plot]: two pixel positions on the axis and their values (120px=1GHz,900px=5GHz)"
                        : "<px>=<value>,<px>=<value>[@plot]: two pixel positions on the axis and their values (80px=0,560px=-40)");
                Clicks.Add((plot.Value, axis, parsed[0].P, parsed[0].V, parsed[1].P, parsed[1].V));
                break;
            }
            case "--trace":
            {
                int eq = v.LastIndexOf('=');
                if (eq <= 0 || PlotQuantity.ParseAssignment(v[(eq + 1)..].Trim()) is not { } assignment)
                    return Bad("<T3|#rrggbb|solid|dashed|dotted>[@plot]=S21:db (or ignore); quantities db, mag, phase, rad, re, im, complex, vswr, rl, il");
                Traces.Add(new PlotTraceOverride(v[..eq].Trim(), assignment));
                break;
            }
            case "--smith-z0":
            {
                string t = v.Trim();
                foreach (string unit in new[] { "Ohm", "ohm", "Ω" })
                    if (t.EndsWith(unit, StringComparison.Ordinal)) { t = t[..^unit.Length]; break; }
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double z) || !(z > 0) || !double.IsFinite(z))
                    return Bad("a reference impedance in ohms (50Ohm)");
                SmithZ0 = z;
                break;
            }
            case "--freq":
            {
                int eq = v.LastIndexOf('=');
                var range = eq > 0 ? Range(v[(eq + 1)..]) : null;
                if (range is not { } r) return Bad("<trace>=<start>..<stop>, each with its unit (T4=1GHz..5GHz)");
                Frequencies.Add(new PlotFrequencyOverride(v[..eq].Trim(), r.Start, r.Stop));
                break;
            }
            case "--phase":
                foreach (string entry in v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = entry.IndexOf('=');
                    var at = eq > 0 ? PlotQuantity.Entry(entry[..eq]) : null;
                    var rule = eq > 0 ? PhaseOf(entry[(eq + 1)..]) : null;
                    if (at is null || rule is null) return Bad("S21=min|zero|delay:<time>, comma-separated (S21=delay:250ps)");
                    Phase[at.Value] = rule;
                }
                break;
            case "--fill":
                foreach (string entry in v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = entry.IndexOf('=');
                    var at = eq > 0 ? PlotQuantity.Entry(entry[..eq]) : null;
                    var rule = at is { } e ? FillOf(e, entry[(eq + 1)..]) : null;
                    if (at is null || rule is null) return Bad("S12=S21|zero|default|unassigned, S22=S11, comma-separated");
                    Fill[at.Value] = rule.Value;
                }
                break;
            case "--prefer":
                foreach (string entry in v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = entry.IndexOf('=');
                    var at = eq > 0 ? PlotQuantity.Entry(entry[..eq]) : null;
                    string which = eq > 0 ? entry[(eq + 1)..].Trim() : "";
                    if (at is null || !(which is "chart" or "rect" || (which.Length > 1 && which[0] is 'T' or 't' && int.TryParse(which[1..], out _))))
                        return Bad("S11=chart|rect|T3, comma-separated: which of two traces of one entry is written");
                    Prefer[at.Value] = which;
                }
                break;
            case "--grid":
            {
                string t = v.Trim();
                bool log = t.EndsWith("log", StringComparison.OrdinalIgnoreCase);
                if (log) t = t[..^3];
                int slash = t.LastIndexOf('/');
                var range = slash > 0 ? Range(t[..slash]) : null;
                if (range is not { } r || !int.TryParse(t[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 2
                    || (log && !(r.Start > 0)))
                    return Bad("<start>..<stop>/<points>, log appended for a logarithmic grid (1GHz..5GHz/81)");
                Grid = new AssemblyGrid(r.Start, r.Stop, n, log);
                break;
            }
            case "--format":
                Format = v.Trim().ToLowerInvariant() switch { "db" => MatrixFormat.DB, "ma" => MatrixFormat.MA, "ri" => MatrixFormat.RI, _ => null };
                if (Format is null) return Bad("db, ma or ri");
                break;
            case "--ports":
                if (!int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ports)
                    || ports < 1 || ports > TouchstoneAssembly.MaxPorts)
                    return Bad($"a whole number of ports from 1 to {TouchstoneAssembly.MaxPorts}");
                Ports = ports;
                break;
            case "--csv":
                Csv = v;
                break;
            case "--keep-sources":
                switch (v.Trim().ToLowerInvariant())
                {
                    case "on" or "true" or "1": KeepSources = true; break;
                    case "off" or "false" or "0": KeepSources = false; break;
                    default: return Bad("on or off");
                }
                break;
            default:
                return null;
        }
        Given.Add(a);
        i++;
        return 0;
    }

    /// <summary>The reading's options these flags spell. <paramref name="factor"/> carries file pixels onto a reduced
    /// reading, as the trace's pixel flags are carried.</summary>
    public PlotReadingOptions ReadingOptions(Func<int, double> factor) => new()
    {
        Plots = Plots.Count > 0 ? [.. Plots] : null,
        Axes = [.. Clicks.Select(c => new PlotAxisOverride(c.Plot, c.Axis,
                    new AxisClicks(c.P0 * factor(c.Plot), c.V0, c.P1 * factor(c.Plot), c.V1,
                                   Scales.Any(s => s.Axis == c.Axis && (s.Plot == 0 || s.Plot == c.Plot) && s.Log) ? AxisScale.Log : AxisScale.Linear)))],
        Scales = [.. Scales],
        Traces = [.. Traces],
        SmithZ0 = SmithZ0,
        Frequencies = [.. Frequencies],
    };

    /// <summary>The file's options these flags spell.</summary>
    public TouchstoneAssemblyOptions AssemblyOptions() => new()
    {
        Phase = new Dictionary<(int, int), PhaseRule>(Phase),
        Fill = new Dictionary<(int, int), FillRule>(Fill),
        Prefer = new Dictionary<(int, int), string>(Prefer),
        Grid = Grid,
        Format = Format ?? MatrixFormat.DB,
        Ports = Ports,
    };

    // ── spellings ────────────────────────────────────────────────────────────

    /// <summary><c>text@n</c> → (text, n); no <c>@</c> is plot 1.</summary>
    private static (string Text, int? Plot) At(string v)
    {
        int at = v.LastIndexOf('@');
        if (at < 0) return (v, 1);
        return int.TryParse(v[(at + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1 ? (v[..at], n) : (v, null);
    }

    private static double? Px(string s)
    {
        s = s.Trim();
        if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s[..^2];
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : null;
    }

    /// <summary>An x value is a frequency with its unit (or a plain number, read as hertz); a y value is a plain number
    /// in the axis's own units.</summary>
    private static double? AxisValue(string s, char axis)
    {
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)) return d;
        return axis == 'x' && RecognitionSweep.Parse(s) is { } f ? f.Hz : null;
    }

    private static (double Start, double Stop)? Range(string s)
    {
        int dots = s.IndexOf("..", StringComparison.Ordinal);
        if (dots <= 0) return null;
        if (RecognitionSweep.Parse(s[..dots].Trim()) is not { } a || RecognitionSweep.Parse(s[(dots + 2)..].Trim()) is not { } b) return null;
        return b.Hz > a.Hz ? (a.Hz, b.Hz) : null;
    }

    private static PhaseRule? PhaseOf(string s)
    {
        s = s.Trim();
        switch (s.ToLowerInvariant())
        {
            case "min" or "minimum": return new PhaseRule(MissingPhase.MinimumPhase);
            case "zero": return new PhaseRule(MissingPhase.Zero);
        }
        if (!s.StartsWith("delay:", StringComparison.OrdinalIgnoreCase)) return null;
        return Seconds(s[6..]) is { } t ? new PhaseRule(MissingPhase.Delay, t) : null;
    }

    /// <summary>A time with its unit: <c>250ps</c>, <c>1.2ns</c>, <c>3e-10s</c>.</summary>
    private static double? Seconds(string s)
    {
        s = s.Trim();
        (string Suffix, double Scale)[] units = [("ps", 1e-12), ("ns", 1e-9), ("us", 1e-6), ("µs", 1e-6), ("ms", 1e-3), ("fs", 1e-15), ("s", 1)];
        foreach (var (suffix, scale) in units)
            if (s.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(s[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
                return v * scale;
        return null;
    }

    private static FillRule? FillOf((int I, int J) at, string s)
    {
        s = s.Trim();
        switch (s.ToLowerInvariant())
        {
            case "zero" or "matched": return FillRule.Zero;
            case "default": return FillRule.Default;
            case "unassigned": return FillRule.Unassigned;
            case "reciprocal": return FillRule.Reciprocal;
            case "symmetric": return FillRule.Symmetric;
        }
        if (s.StartsWith('=')) s = s[1..];
        if (PlotQuantity.Entry(s) is not { } from) return null;
        if (from == (at.J, at.I) && at.I != at.J) return FillRule.Reciprocal;
        if (at.I == at.J && from.I == from.J && from != at) return FillRule.Symmetric;
        return null;
    }
}
