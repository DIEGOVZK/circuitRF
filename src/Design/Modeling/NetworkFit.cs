// Fitting the extrinsic network (brief-modelcomposer-5; overview D5, D13).
//
// Every method here is a least-squares problem over the network's free variables, with the network solved by the
// linear engine at each trial point (SixPinNetwork.Blocks) — nothing re-stamps it — and the residual vector built from
// three kinds of term:
//
//   (a) FLATNESS at the active points. Through a correct shell the intrinsic Y is a lumped, frequency-independent
//       G + jωC ([P1] §5.6.1–5.6.3), so each of Re Y_ij and Im Y_ij / ω is constant across the flatness band. The
//       residual is each one's deviation from its own band mean, divided by the point's admittance scale.
//   (b) THE COLD-FET MODELS at the cold points ([P1] §5.5.2), compared in the MEASURED domain. The cold core's own
//       elements are not coordinates: each trial de-embeds the point, fits the core in closed form, embeds that core
//       back through the trial network and compares with the measurement (variable projection).
//         · pinch-off, Vds = 0: Gm = 0, R_DS → ∞, intrinsic C_DS = 0 ([P1] §5.5.2.2), so the core is a reciprocal
//           capacitance matrix [[Cgs + Cgd, −Cgd], [−Cgd, Cgd]]. The residual is Y, per entry, ÷ ωC_scale — eq. 5.4's
//           quantities, with the series elements kept rather than neglected, which is what separates an outer
//           capacitance from an inner one at all.
//         · forward gate, Vds = 0, extreme ([P1] §5.5.2.4.1): the intrinsic device is one node, so the extrinsic Y is
//           the network's own with its intrinsic ports shorted (Y_aa); the residual is Z, ÷ Z_scale (eq. 5.7's domain).
//         · forward gate, moderate ([P1] Fig. 5.17): Z_int = [[Z1 + Rch/3, Rch/2], [Rch/2, Rch]],
//           Z1 = R1 ∥ C1. R_ch is given (a test structure's); 1/Z1 = 1/R1 + jωC1 is linear in its two unknowns.
//   (c) optionally, S against a fitted small-signal circuit at each active point: the band-mean G + jωC, embedded.
//
// The weights make each term's cost its weight times its mean square. A mutual's |k| < 1 is a residual that is zero
// inside. The variables are coordinates of the unit box — logarithmic for a positive quantity — so the registry's
// ask/tell algorithms drive this cost unchanged (src/Engine/Optimization); they never call a simulator.
//
// COLD-FET DIRECT alternates two Levenberg–Marquardt fits until neither moves: the capacitances on the pinch-off
// residual with the series elements held, then the resistances, inductances and mutuals on the forward residual with
// the capacitances held. MULTI-BIAS runs a seeded global search per start, polishes each with Levenberg–Marquardt and
// keeps the best; the spread of the polished starts is reported beside each standard error.
//
// Nothing here writes: Run returns a result, Apply puts it in a document, FitFile does both and saves. A cancelled run
// throws OperationCanceledException and the document — and the file — keep the previous values (D13).

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using CircuitRF.Engine.Optimization;
using NumFlat;

namespace CircuitRF.Design.Modeling;

/// <summary>One fitted variable and how well the data determines it (R-mc5-4).</summary>
public sealed record FittedVariable(
    string Name, double Value, double Min, double Max,
    /// <summary>Standard error, base SI; +∞ when the data does not move the residual at all.</summary>
    double StdError,
    double RelativeError,
    /// <summary>max − min of this variable over the polished starts; 0 for a single start.</summary>
    double Spread,
    /// <summary><c>Min</c> or <c>Max</c> when the optimum sits on that bound.</summary>
    string? AtBound,
    /// <summary>The variable this one is most correlated with, and the correlation.</summary>
    string? Partner, double Correlation,
    bool Poor);

/// <summary>The identifiability report: every fitted variable, and the one-line findings.</summary>
public sealed record IdentifiabilityReport(IReadOnlyList<FittedVariable> Variables, IReadOnlyList<string> Lines)
{
    public FittedVariable this[string name] => Variables.Single(v => v.Name == name);
}

/// <summary>What a fit found. <see cref="Values"/> holds every network variable — fitted, tied and locked — in base SI.</summary>
public sealed record NetworkFitResult(
    IReadOnlyDictionary<string, double> Values,
    IdentifiabilityReport Report,
    NetworkFitSettings Settings,
    NetworkFitMethod Method,
    double Cost,
    IReadOnlyList<string> Notes,
    long Evaluations);

public static class NetworkFit
{
    /// <summary>A point is at Vds = 0 V within this, volts.</summary>
    public const double ZeroVds = 0.02;

    /// <summary>
    /// Fits the network of <paramref name="doc"/> to <paramref name="data"/> (the open document's normalised data) with
    /// the document's settings. Throws <see cref="ModelingException"/> for a refusal and
    /// <see cref="OperationCanceledException"/> when cancelled; nothing is written either way.
    /// </summary>
    public static NetworkFitResult Run(string cmcPath, CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                                       RunControl? rc = null)
    {
        var problem = NetworkFitProblem.Build(Path.GetFullPath(cmcPath), doc, data);
        return problem.Fit(rc);
    }

    /// <summary>
    /// The document with <paramref name="result"/> in its Network block (R-mc5-5): each fitted and tied variable's
    /// <c>Value</c>, the resolved settings (the chosen points included, so they can be edited), and the record. The
    /// changed values change the intrinsic cache's key, and <see cref="StaleSteps"/> names the steps computed before.
    /// </summary>
    public static CmcDocument Apply(CmcDocument doc, NetworkFitResult result)
    {
        var n = CmcExtrinsic.Network(doc);
        var vars = new Dictionary<string, NetworkVariableSettings>(n.Variables, StringComparer.Ordinal);
        foreach (var (name, value) in result.Values)
        {
            var s = vars.GetValueOrDefault(name) ?? new NetworkVariableSettings();
            if (s.Lock && s.Tie is null) continue;       // a locked value is never fitted, so never rewritten
            vars[name] = s with { Value = value };
        }
        var updated = n with { Variables = vars, Fit = result.Settings };
        var record = new NetworkFitRecord
        {
            Method = result.Method.ToString(),
            Cost = result.Cost,
            Digest = Digest(updated),
            Variables = result.Report.Variables.ToDictionary(
                v => v.Name, v => new FittedValueRecord(v.StdError, v.Spread, v.AtBound), StringComparer.Ordinal),
            Report = result.Report.Lines,
        };
        return CmcExtrinsic.With(doc, updated with { Result = record });
    }

    /// <summary>
    /// Reads the <c>.cmc</c>, fits, applies and writes it back — the operation the window and the <c>model</c> verb both
    /// call. <paramref name="data"/> null opens the document's data through its cache. Cancelled, nothing is written.
    /// </summary>
    public static NetworkFitResult FitFile(string cmcPath, IReadOnlyDictionary<string, NormalisedData>? data = null,
                                           RunControl? rc = null)
    {
        var doc = CmcFile.Read(cmcPath);
        data ??= new CmcCache(cmcPath).Open(doc, rc).Data;
        var result = Run(cmcPath, doc, data, rc);
        rc?.ThrowIfCancellationRequested();
        CmcFile.Write(cmcPath, Apply(doc, result));
        return result;
    }

    /// <summary>
    /// The digest of a network block's schematic and the values its variables take — what a later step records, so a
    /// changed network makes it stale.
    /// </summary>
    public static string Digest(NetworkSettings n)
    {
        var sb = new StringBuilder(n.Schematic ?? "").Append('\n');
        foreach (var (k, v) in n.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(v.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>
    /// The steps whose result was computed through a different network (R-mc5-5): an entry of the document's
    /// <c>Steps</c> object that records a <c>Network</c> digest other than the current one.
    /// </summary>
    public static IReadOnlyList<string> StaleSteps(CmcDocument doc)
    {
        if (doc.Steps is not JsonObject steps) return [];
        string now = Digest(CmcExtrinsic.Network(doc));
        var stale = new List<string>();
        foreach (var (name, node) in steps)
            if (node is JsonObject o && o["Network"] is JsonValue v && v.TryGetValue<string>(out var d) && d != now)
                stale.Add(name);
        return stale;
    }
}

/// <summary>One bias point a fit reads: its S at the device plane and the admittance it implies, per frequency.</summary>
internal sealed class FitPoint
{
    public required PointRef Ref { get; init; }
    public required BiasPoint Bias { get; init; }
    public required double[] F { get; init; }
    public required string AxisKey { get; init; }
    public required C2[] S { get; init; }
    public required C2[] Y { get; init; }
    public required double Z0 { get; init; }
}

/// <summary>A variable as a coordinate of the unit box.</summary>
internal sealed record FitCoordinate(int Index, string Name, NetworkElementKind Kind, double Min, double Max, bool Log)
{
    /// <summary>The bottom of a logarithmic axis: the minimum, or a millionth of the maximum when the minimum is zero.</summary>
    public double LogFloor => Min > 0 ? Min : Max * 1e-6;

    public double Decode(double u) => Log ? LogFloor * Math.Pow(Max / LogFloor, u) : Min + u * (Max - Min);

    public double Encode(double v) => Math.Clamp(
        Log ? Math.Log(Math.Max(v, LogFloor) / LogFloor) / Math.Log(Max / LogFloor) : (v - Min) / (Max - Min), 0, 1);
}

[Flags]
internal enum FitTerms { None = 0, Flatness = 1, PinchOff = 2, Forward = 4, SmallSignal = 8, Cold = PinchOff | Forward }

internal sealed class NetworkFitProblem
{
    private readonly SixPinNetwork _network;
    private readonly IReadOnlyList<NetworkVariable> _vars;
    private readonly double[] _start;                      // every variable, network order
    private readonly int[] _tieTo;                         // −1, or the index this one follows
    private readonly double[] _tieFactor;
    private readonly FitCoordinate[] _free;
    private readonly NetworkFitSettings _settings;
    private readonly FitPoint[] _active;
    private readonly FitPoint? _pinch, _forward;
    private readonly (double Lo, double Hi) _flatBand;
    private readonly (double Lo, double Hi)? _coldBand;
    private readonly List<string> _notes;
    private long _evaluations;

    private NetworkFitProblem(SixPinNetwork network, double[] start, int[] tieTo, double[] tieFactor, FitCoordinate[] free,
                              NetworkFitSettings settings, FitPoint[] active, FitPoint? pinch, FitPoint? forward,
                              (double, double) flatBand, List<string> notes)
    {
        _network = network;
        _vars = network.Variables;
        _start = start;
        _tieTo = tieTo;
        _tieFactor = tieFactor;
        _free = free;
        _settings = settings;
        _active = active;
        _pinch = pinch;
        _forward = forward;
        _flatBand = flatBand;
        _coldBand = settings.ColdBand;
        _notes = notes;
    }

    // ── Building the problem ──────────────────────────────────────────────────

    public static NetworkFitProblem Build(string cmcPath, CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data)
    {
        string dir = Path.GetDirectoryName(cmcPath)!;
        var net = CmcExtrinsic.Network(doc);
        if (net.Schematic is null)
            throw new ModelingException("Network", "there is no extrinsic network to fit; choose one first.");
        var network = SixPinNetwork.Load(DataImport.ResolveSource(dir, net.Schematic));
        var settings = net.Fit ?? NetworkFitSettings.Default;
        var notes = new List<string>();

        // Variables: start, bounds, ties, locks (R-mc5-1.3, R-mc5-2).
        var vars = network.Variables;
        int n = vars.Count;
        var start = new double[n];
        var tieTo = new int[n];
        var tieFactor = new double[n];
        var free = new List<FitCoordinate>();
        for (int i = 0; i < n; i++)
        {
            var v = vars[i];
            var s = net.Variables.GetValueOrDefault(v.Name) ?? new NetworkVariableSettings();
            start[i] = s.Value ?? v.Value;
            tieTo[i] = -1;
            if (s.Tie is { } tie)
            {
                int t = IndexOf(vars, tie.To);
                if (t < 0) throw new ModelingException($"Network.Variables.{v.Name}.Tie.To", $"'{tie.To}' is not a variable of the network.");
                (tieTo[i], tieFactor[i]) = (t, tie.Factor);
                continue;
            }
            if (s.Lock) continue;
            var d = SixPinNetwork.DefaultFor(v);
            double? lo = s.Min ?? d.Min, hi = s.Max ?? d.Max;
            if (lo is null || hi is null)
                throw new ModelingException(v.Name, $"it has no {(lo is null ? "Min" : "Max")}; give it one, or lock it.");
            if (!(hi > lo)) throw new ModelingException(v.Name, $"its range {G(lo.Value)} to {G(hi.Value)} is empty; lock it instead.");
            bool log = v.Kind != NetworkElementKind.Other && lo >= 0;
            var c = new FitCoordinate(i, v.Name, v.Kind, lo.Value, hi.Value, log);
            double inBox = c.Decode(c.Encode(start[i]));
            if (inBox != start[i] && Math.Abs(inBox - start[i]) > 1e-12 * Math.Abs(start[i]))
            {
                notes.Add($"{v.Name}: start {G(start[i])} moved to {G(inBox)}, inside its range.");
                start[i] = inBox;
            }
            free.Add(c);
        }
        if (free.Count == 0) throw new ModelingException("Network", "every variable is locked or tied; there is nothing to fit.");

        // The data at the device plane.
        var pads = PadDeembedding.Load(CmcExtrinsic.Pads(doc), dir);
        var points = new List<FitPoint>();
        foreach (var set in doc.DataSets)
        {
            if (!data.TryGetValue(set.Name, out var nd) || nd.S is null || nd.Ports != 2) continue;
            var plane = set.Conditions.ReferencePlane ?? ReferencePlane.Probe;
            if (plane == ReferencePlane.Intrinsic) continue;
            var d = plane is ReferencePlane.Instrument or ReferencePlane.Probe ? pads.Remove(nd, set.Name) : nd;
            double[] f = d.FrequenciesHz!;
            string axis = string.Join(",", f.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
            for (int p = 0; p < d.Count; p++)
            {
                var bp = d.Points[p];
                var s = new C2[f.Length];
                var y = new C2[f.Length];
                for (int k = 0; k < f.Length; k++)
                {
                    s[k] = new C2(d.SAt(p, k, 0, 0), d.SAt(p, k, 0, 1), d.SAt(p, k, 1, 0), d.SAt(p, k, 1, 1));
                    y[k] = NetworkAlgebra.SToY(s[k], d.Z0);
                }
                points.Add(new FitPoint { Ref = new PointRef(set.Name, bp.PointId), Bias = bp, F = f, AxisKey = axis, S = s, Y = y, Z0 = d.Z0 });
            }
        }
        if (points.Count == 0) throw new ModelingException("Network", "no data set holds S at the device plane or before it; there is nothing to fit to.");

        // Which points (R-mc5-1).
        bool cold = settings.Method != NetworkFitMethod.MultiBias || settings.Weights.Cold > 0;
        bool needCold = settings.Method != NetworkFitMethod.MultiBias;
        var pinch = Choose(points, settings.PinchOff, IsPinchedOff, lowest: true);
        var forward = Choose(points, settings.Forward, IsForward, lowest: false);
        if (needCold && pinch is null)
            throw new ModelingException("Cold-FET", "it needs a pinch-off point: S at Vds = 0 V with Vgs below threshold; none of the data is one.");
        if (needCold && forward is null)
            throw new ModelingException("Cold-FET", "it needs a forward-gate point: S at Vds = 0 V with the gate forward-biased; none of the data is one.");
        if (cold && !needCold && (pinch is null || forward is null))
            notes.Add($"No {(pinch is null ? "pinch-off" : "forward-gate")} point at Vds = 0 V; the cold term uses what there is.");
        if (!cold) (pinch, forward) = (null, null);
        if (forward is not null && settings.ForwardVariant == ForwardVariant.Moderate && settings.ChannelResistance is null)
            throw new ModelingException("Network.Fit.ChannelResistance", "the moderate forward variant needs the channel resistance, from another test structure.");

        var active = settings.Method == NetworkFitMethod.ColdFet ? [] : ChooseActive(points, settings.Active);
        if (settings.Method != NetworkFitMethod.ColdFet && active.Length == 0)
            throw new ModelingException("Multi-bias", "it needs S at an active bias point (Vds above 0 V); none of the data is one.");

        var resolved = settings with
        {
            PinchOff = pinch?.Ref ?? settings.PinchOff,
            Forward = forward?.Ref ?? settings.Forward,
            Active = settings.Method == NetworkFitMethod.ColdFet ? settings.Active : [.. active.Select(p => p.Ref)],
        };
        var axisF = (active.FirstOrDefault() ?? pinch ?? forward ?? points[0]).F;
        var band = net.FlatnessBand ?? IntrinsicQuantities.DefaultBand(axisF);
        return new NetworkFitProblem(network, start, tieTo, tieFactor, [.. free], resolved, active, pinch, forward, band, notes);
    }

    private static int IndexOf(IReadOnlyList<NetworkVariable> vars, string name)
    {
        for (int i = 0; i < vars.Count; i++) if (vars[i].Name == name) return i;
        return -1;
    }

    private static bool AtZeroVds(FitPoint p) => Math.Abs(p.Bias.Vds) <= NetworkFit.ZeroVds && !p.Bias.Excluded;

    /// <summary>Pinched off: at the lowest frequency the output admittance is capacitive, not resistive.</summary>
    private static bool IsPinchedOff(FitPoint p) =>
        AtZeroVds(p) && p.Y[0].D.Real < 0.5 * p.Y[0].D.Magnitude;

    /// <summary>Forward gate: at the lowest frequency the input admittance is resistive, and the gate current (when
    /// measured) flows in.</summary>
    private static bool IsForward(FitPoint p) =>
        AtZeroVds(p) && p.Y[0].A.Real > 0.5 * p.Y[0].A.Magnitude && !(p.Bias.Ig <= 0);

    private static FitPoint? Choose(List<FitPoint> points, PointRef? named, Func<FitPoint, bool> test, bool lowest)
    {
        if (named is not null)
            return Find(points, named);
        var c = points.Where(test);
        return lowest ? c.MinBy(p => p.Bias.Vgs) : c.MaxBy(p => p.Bias.Vgs);
    }

    private static FitPoint Find(List<FitPoint> points, PointRef r) =>
        points.FirstOrDefault(p => p.Ref == r)
        ?? throw new ModelingException(r.ToString(), "no data set holds this point with S at the device plane or before it.");

    /// <summary>
    /// The active points (R-mc5-1.2): the named ones, or one off (lowest Vgs at the highest Vds), one on (highest Vgs
    /// at the lowest Vds), one near the knee (highest Vgs, the first Vds whose Id reaches 90 % of the saturated one;
    /// without currents the Vds nearest a quarter of the highest) and one in saturation (highest Vgs and Vds).
    /// </summary>
    private static FitPoint[] ChooseActive(List<FitPoint> points, IReadOnlyList<PointRef> named)
    {
        if (named.Count > 0) return [.. named.Select(r => Find(points, r))];
        var act = points.Where(p => !p.Bias.Excluded && Math.Abs(p.Bias.Vds) > NetworkFit.ZeroVds).ToList();
        if (act.Count == 0) return [];
        const double tol = 1e-6;
        double minVgs = act.Min(p => p.Bias.Vgs), maxVgs = act.Max(p => p.Bias.Vgs), maxVds = act.Max(p => p.Bias.Vds);
        var off = act.Where(p => p.Bias.Vgs <= minVgs + tol).MaxBy(p => p.Bias.Vds)!;
        var top = act.Where(p => p.Bias.Vgs >= maxVgs - tol).OrderBy(p => p.Bias.Vds).ToList();
        var on = top[0];
        var sat = top[^1];
        FitPoint knee;
        if (top.All(p => double.IsFinite(p.Bias.Id)) && sat.Bias.Id != 0)
            knee = top.First(p => Math.Abs(p.Bias.Id) >= 0.9 * Math.Abs(sat.Bias.Id));
        else
            knee = top.MinBy(p => Math.Abs(p.Bias.Vds - 0.25 * maxVds))!;
        return [.. new[] { off, on, knee, sat }.Distinct()];
    }

    // ── Values and coordinates ────────────────────────────────────────────────

    /// <summary>Every variable's value at the free coordinates' values <paramref name="freeValues"/>, ties applied.</summary>
    private double[] Compose(double[] freeValues, double[] baseline)
    {
        var v = (double[])baseline.Clone();
        for (int j = 0; j < _free.Length; j++) v[_free[j].Index] = freeValues[j];
        for (int i = 0; i < v.Length; i++)
            if (_tieTo[i] >= 0) v[i] = v[_tieTo[i]] * _tieFactor[i];
        return v;
    }

    private Dictionary<string, double> Named(double[] all)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < all.Length; i++) d[_vars[i].Name] = all[i];
        return d;
    }

    // ── The residual vector ───────────────────────────────────────────────────

    private int[] BandIndices(FitPoint p, (double Lo, double Hi)? band)
    {
        var idx = Enumerable.Range(0, p.F.Length).Where(k => band is not { } b || (p.F[k] >= b.Lo && p.F[k] <= b.Hi)).ToArray();
        if (idx.Length < 2)
            throw new ModelingException(p.Ref.ToString(), $"fewer than two of its frequencies lie in the band {G((band ?? default).Lo)} to {G((band ?? default).Hi)} Hz.");
        return idx;
    }

    /// <summary>The weighted residual vector at <paramref name="all"/> (every variable's value).</summary>
    internal double[] Residuals(double[] all, FitTerms terms)
    {
        var values = Named(all);
        // |k| < 1 for every mutual (R-mc5-2): the network is solved with each coupling held to MaxCoupling — the
        // engine is never handed a non-physical one — and the excess is a penalty that is zero inside.
        var penalties = new List<double>();
        var k = _network.CouplingFactors(values);
        for (int i = 0; i < k.Count; i++)
        {
            penalties.Add(10 * Math.Max(0, k[i] - MaxCoupling));
            if (k[i] > MaxCoupling && values.ContainsKey(_network.Mutuals[i].M.Trim()))
                values[_network.Mutuals[i].M.Trim()] *= MaxCoupling / k[i];
        }
        var blocks = new Dictionary<string, NetworkBlocks[]>(StringComparer.Ordinal);
        NetworkBlocks[] B(FitPoint p)
        {
            if (!blocks.TryGetValue(p.AxisKey, out var b)) blocks[p.AxisKey] = b = _network.Blocks(p.F, values);
            return b;
        }

        var w = _settings.Weights;
        var r = new List<double>();
        if ((terms & FitTerms.Flatness) != 0 && _active.Length > 0)
        {
            var piece = new List<double>();
            foreach (var p in _active) Flatness(p, B(p), piece);
            Append(r, piece, w.Flatness);
        }
        int coldParts = ((terms & FitTerms.PinchOff) != 0 && _pinch is not null ? 1 : 0) + ((terms & FitTerms.Forward) != 0 && _forward is not null ? 1 : 0);
        double coldWeight = terms == FitTerms.PinchOff || terms == FitTerms.Forward || terms == FitTerms.Cold ? 1 : w.Cold;
        if ((terms & FitTerms.PinchOff) != 0 && _pinch is not null)
        {
            var piece = new List<double>();
            PinchOff(_pinch, B(_pinch), piece);
            Append(r, piece, coldWeight / coldParts);
        }
        if ((terms & FitTerms.Forward) != 0 && _forward is not null)
        {
            var piece = new List<double>();
            Forward(_forward, B(_forward), piece);
            Append(r, piece, coldWeight / coldParts);
        }
        if ((terms & FitTerms.SmallSignal) != 0 && w.SmallSignal > 0 && _active.Length > 0)
        {
            var piece = new List<double>();
            foreach (var p in _active) SmallSignal(p, B(p), piece);
            Append(r, piece, w.SmallSignal);
        }
        r.AddRange(penalties);
        // A non-finite residual makes the trial point a failed evaluation (Evaluate catches it); it reaches no one.
        foreach (double x in r)
            if (!double.IsFinite(x)) throw new ArithmeticException();
        return [.. r];
    }

    /// <summary>The largest coupling factor a trial point is solved at; above it the excess is the penalty.</summary>
    internal const double MaxCoupling = 0.999;

    private static void Append(List<double> r, List<double> piece, double weight)
    {
        if (piece.Count == 0 || weight == 0) return;
        double scale = Math.Sqrt(weight / piece.Count);
        foreach (double x in piece) r.Add(scale * x);
    }

    private static C2 Deembed(NetworkBlocks b, C2 y) => NetworkAlgebra.Deembed(b.Aa, b.Ab, b.Ba, b.Bb, y).YInt;

    private static C2 Embed(NetworkBlocks b, C2 yCore) => NetworkAlgebra.Embed(b.Aa, b.Ab, b.Ba, b.Bb, yCore);

    /// <summary>The extrinsic Y of the network around a core given by its IMPEDANCE, finite for a short:
    /// <c>Y_aa − Y_ab Z (I + Y_bb Z)⁻¹ Y_ba</c>.</summary>
    private static C2 EmbedZ(NetworkBlocks b, C2 zCore) => b.Aa - b.Ab * (zCore * (C2.Identity + b.Bb * zCore).Inverse()) * b.Ba;

    private static double W(double f) => 2 * Math.PI * f;

    /// <summary>The largest |Im Y_ij| / ω of the measurement at its lowest frequency — the point's capacitance scale.</summary>
    private static double CapScale(FitPoint p, int k) =>
        Math.Max(1e-18, new[] { p.Y[k].A, p.Y[k].B, p.Y[k].C, p.Y[k].D }.Max(c => Math.Abs(c.Imaginary)) / W(p.F[k]));

    /// <summary>The intrinsic G and C at each band frequency, and their band means.</summary>
    private static (double[][] G, double[][] C, double[] Gm, double[] Cm) Lumped(FitPoint p, NetworkBlocks[] b, int[] idx)
    {
        var g = new double[4][];
        var c = new double[4][];
        for (int e = 0; e < 4; e++) { g[e] = new double[idx.Length]; c[e] = new double[idx.Length]; }
        for (int t = 0; t < idx.Length; t++)
        {
            int k = idx[t];
            var y = Deembed(b[k], p.Y[k]);
            double w = W(p.F[k]);
            Complex[] e4 = [y.A, y.B, y.C, y.D];
            for (int e = 0; e < 4; e++) { g[e][t] = e4[e].Real; c[e][t] = e4[e].Imaginary / w; }
        }
        return (g, c, [.. g.Select(x => x.Average())], [.. c.Select(x => x.Average())]);
    }

    /// <summary>Term (a): each of Re Y_ij and Im Y_ij/ω about its band mean, over the point's admittance scale.</summary>
    private void Flatness(FitPoint p, NetworkBlocks[] b, List<double> r)
    {
        var idx = BandIndices(p, _flatBand);
        var (g, c, gm, cm) = Lumped(p, b, idx);
        double cs = CapScale(p, idx[0]);
        double gs = Math.Max(new[] { p.Y[idx[0]].A, p.Y[idx[0]].B, p.Y[idx[0]].C, p.Y[idx[0]].D }.Max(x => Math.Abs(x.Real)),
                             W(Math.Sqrt(p.F[idx[0]] * p.F[idx[^1]])) * cs);
        for (int t = 0; t < idx.Length; t++)
            for (int e = 0; e < 4; e++)
            {
                r.Add((g[e][t] - gm[e]) / gs);
                r.Add((c[e][t] - cm[e]) / cs);
            }
    }

    /// <summary>Term (c): S of the band-mean G + jωC through the network, against the measurement at every frequency.</summary>
    private void SmallSignal(FitPoint p, NetworkBlocks[] b, List<double> r)
    {
        var (_, _, gm, cm) = Lumped(p, b, BandIndices(p, _flatBand));
        for (int k = 0; k < p.F.Length; k++)
        {
            var jw = new Complex(0, W(p.F[k]));
            var core = new C2(gm[0] + jw * cm[0], gm[1] + jw * cm[1], gm[2] + jw * cm[2], gm[3] + jw * cm[3]);
            var d = NetworkAlgebra.YToS(Embed(b[k], core), p.Z0) - p.S[k];
            foreach (var x in new[] { d.A, d.B, d.C, d.D }) { r.Add(x.Real); r.Add(x.Imaginary); }
        }
    }

    /// <summary>Term (b), pinch-off: the reciprocal capacitive core fitted in closed form, embedded, against the measured Y
    /// over ωC_scale.</summary>
    private void PinchOff(FitPoint p, NetworkBlocks[] b, List<double> r)
    {
        var idx = BandIndices(p, _coldBand);
        var (_, _, _, cm) = Lumped(p, b, idx);
        // Least squares of C11 = Cgs + Cgd, C12 = C21 = −Cgd, C22 = Cgd over the band.
        double cgd = (cm[3] - cm[1] - cm[2]) / 3, cgs = cm[0] - cgd;
        double cs = CapScale(p, idx[0]);
        foreach (int k in idx)
        {
            double w = W(p.F[k]);
            var jw = new Complex(0, w);
            var core = new C2(jw * (cgs + cgd), -jw * cgd, -jw * cgd, jw * cgd);
            var d = Embed(b[k], core) - p.Y[k];
            foreach (var x in new[] { d.A, d.B, d.C, d.D }) { r.Add(x.Real / (w * cs)); r.Add(x.Imaginary / (w * cs)); }
        }
    }

    /// <summary>Term (b), forward gate: the collapsed (extreme) or Fig. 5.17 (moderate) core, embedded, against the measured
    /// Z over the point's impedance scale.</summary>
    private void Forward(FitPoint p, NetworkBlocks[] b, List<double> r)
    {
        var idx = BandIndices(p, _coldBand);
        double zs = 1e-3;
        foreach (int k in idx) zs = Math.Max(zs, p.Y[k].Inverse().MaxAbs);

        double rch = _settings.ChannelResistance ?? 0;
        double g1 = 0, c1 = 0;
        if (_settings.ForwardVariant == ForwardVariant.Moderate)
        {
            // 1/Z1 = G1 + jωC1, with Z1 = Z_int,11 − Rch/3: linear in both.
            foreach (int k in idx)
            {
                var y1 = 1 / (Deembed(b[k], p.Y[k]).Inverse().A - rch / 3);
                g1 += y1.Real;
                c1 += y1.Imaginary / W(p.F[k]);
            }
            (g1, c1) = (g1 / idx.Length, c1 / idx.Length);
        }
        foreach (int k in idx)
        {
            C2 zCore = default;
            if (_settings.ForwardVariant == ForwardVariant.Moderate)
            {
                var z1 = 1 / new Complex(g1, W(p.F[k]) * c1);
                zCore = new C2(z1 + rch / 3, rch / 2, rch / 2, rch);
            }
            var d = EmbedZ(b[k], zCore).Inverse() - p.Y[k].Inverse();
            foreach (var x in new[] { d.A, d.B, d.C, d.D }) { r.Add(x.Real / zs); r.Add(x.Imaginary / zs); }
        }
    }

    // ── Driving the ask/tell algorithms ───────────────────────────────────────

    private sealed record Solved(double[] Free, double Cost);

    private static readonly ParallelOptions Serial = new() { MaxDegreeOfParallelism = 1 };

    /// <summary>
    /// Runs <paramref name="alg"/> over the coordinates <paramref name="coords"/> (indices into <see cref="_free"/>), the
    /// others held at <paramref name="baseline"/>, until it finishes or has spent <paramref name="maxEvals"/>.
    /// A batch is evaluated in parallel; the results go back in batch order, so the run is the same whatever the cores.
    /// </summary>
    private Solved Drive(IOptimizerAlgorithm alg, int[] coords, double[] baseline, FitTerms terms, int maxEvals, RunControl? rc)
    {
        double[]? best = null;
        Evaluation? bestE = null;
        int used = 0;
        var po = new ParallelOptions { CancellationToken = rc?.Token ?? CancellationToken.None, MaxDegreeOfParallelism = Environment.ProcessorCount };
        while (!alg.IsFinished && used < maxEvals)
        {
            rc?.ThrowIfCancellationRequested();
            var batch = alg.Ask();
            if (batch.Count == 0) break;
            var results = new Evaluation[batch.Count];
            Parallel.For(0, batch.Count, batch.Count > 1 ? po : Serial, i => results[i] = Evaluate(batch[i], coords, baseline, terms));
            rc?.ThrowIfCancellationRequested();
            used += batch.Count;
            Interlocked.Add(ref _evaluations, batch.Count);
            rc?.TickStage(batch.Count);
            for (int i = 0; i < batch.Count; i++)
                if (bestE is null || Better(results[i], bestE)) (bestE, best) = (results[i], batch[i]);
            alg.Tell(results);
        }
        var freeValues = (double[])baseline.Clone();
        if (best is not null)
            for (int j = 0; j < coords.Length; j++) freeValues[coords[j]] = _free[coords[j]].Decode(best[j]);
        return new Solved(freeValues, bestE?.Cost ?? double.PositiveInfinity);
    }

    private static bool Better(Evaluation a, Evaluation b) => a.Failed != b.Failed ? !a.Failed : a.Cost < b.Cost;

    private Evaluation Evaluate(double[] u, int[] coords, double[] baseline, FitTerms terms)
    {
        var freeValues = (double[])baseline.Clone();
        for (int j = 0; j < coords.Length; j++) freeValues[coords[j]] = _free[coords[j]].Decode(u[j]);
        try
        {
            var r = Residuals(Compose(freeValues, _start), terms);
            double cost = 0;
            foreach (double x in r) cost += x * x;
            return new Evaluation(cost, r);
        }
        catch (Exception ex) when (ex is ArithmeticException or ModelingException or InvalidOperationException or SingularMatrixException)
        {
            return new Evaluation(1e300, Failed: true);
        }
    }

    private double[] FreeStart => [.. _free.Select(c => _start[c.Index])];

    private double[] Encode(double[] freeValues, int[] coords) => [.. coords.Select(j => _free[j].Encode(freeValues[j]))];

    /// <summary>Levenberg–Marquardt from <paramref name="from"/> over <paramref name="coords"/>, up to 100 iterations.</summary>
    private Solved Polish(double[] from, int[] coords, FitTerms terms, RunControl? rc)
    {
        if (coords.Length == 0) return new Solved(from, Cost(from, terms));
        var lm = new LevenbergMarquardt(Encode(from, coords));
        var s = Drive(lm, coords, from, terms, 100 * (coords.Length + 2), rc);
        return s.Cost <= Cost(from, terms) ? s : new Solved(from, Cost(from, terms));
    }

    private double Cost(double[] freeValues, FitTerms terms)
    {
        try { return Residuals(Compose(freeValues, _start), terms).Sum(x => x * x); }
        catch (Exception ex) when (ex is ArithmeticException or ModelingException or InvalidOperationException or SingularMatrixException)
        { return double.PositiveInfinity; }
    }

    // ── The methods ───────────────────────────────────────────────────────────

    public NetworkFitResult Fit(RunControl? rc)
    {
        double[] values = FreeStart;
        var spreads = new double[_free.Length];
        var terms = FitTerms.Cold;
        if (_settings.Method != NetworkFitMethod.MultiBias)
            values = ColdFetDirect(values, rc);
        if (_settings.Method != NetworkFitMethod.ColdFet)
        {
            terms = FitTerms.Flatness | FitTerms.SmallSignal | (_settings.Weights.Cold > 0 ? FitTerms.Cold : FitTerms.None);
            (values, spreads) = MultiBias(values, terms, rc);
        }
        rc?.ThrowIfCancellationRequested();
        var report = Identify(values, spreads, terms);
        return new NetworkFitResult(Named(Compose(values, _start)), report, _settings, _settings.Method, Cost(values, terms),
                                    _notes, Interlocked.Read(ref _evaluations));
    }

    /// <summary>R-mc5-1.1: the capacitances from the pinch-off point, then the series elements from the forward point,
    /// alternated until a round moves no value by more than 1e-7 of itself (at most eight rounds), then both together.</summary>
    private double[] ColdFetDirect(double[] values, RunControl? rc)
    {
        int[] caps = [.. Enumerable.Range(0, _free.Length).Where(j => _free[j].Kind == NetworkElementKind.Capacitance)];
        int[] series = [.. Enumerable.Range(0, _free.Length).Where(j => _free[j].Kind is NetworkElementKind.Resistance
                                                                     or NetworkElementKind.Inductance or NetworkElementKind.Mutual)];
        const int rounds = 8;
        for (int round = 0; round < rounds; round++)
        {
            var before = (double[])values.Clone();
            rc?.BeginStage($"Cold-FET round {round + 1}: pinch-off", 0, "evaluation(s)");
            values = Polish(values, caps, FitTerms.PinchOff, rc).Free;
            rc?.BeginStage($"Cold-FET round {round + 1}: forward gate", 0, "evaluation(s)");
            values = Polish(values, series, FitTerms.Forward, rc).Free;
            double moved = 0;
            for (int j = 0; j < values.Length; j++)
                moved = Math.Max(moved, Math.Abs(values[j] - before[j]) / Math.Max(Math.Abs(before[j]), 1e-300));
            if (moved < 1e-7) break;
        }
        // The alternation zig-zags along a valley two steps share — the moderate core's R1 against Rg — so it ends with
        // both cold terms polished together over every element.
        rc?.BeginStage("Cold-FET: both points", 0, "evaluation(s)");
        return Polish(values, [.. caps, .. series], FitTerms.Cold, rc).Free;
    }

    /// <summary>R-mc5-1.2, R-mc5-3: per start a seeded global search over every free variable, polished by
    /// Levenberg–Marquardt; the best start wins and the polished starts' spread is kept.</summary>
    private (double[] Values, double[] Spread) MultiBias(double[] from, FitTerms terms, RunControl? rc)
    {
        var all = Enumerable.Range(0, _free.Length).ToArray();
        int budget = _settings.Budget ?? NetworkFitSettings.DefaultBudgetPerCoordinate * _free.Length;
        var u0 = Encode(from, all);
        var finals = new List<Solved>();
        for (int s = 0; s < _settings.Starts; s++)
        {
            rc?.BeginStage($"Start {s + 1} of {_settings.Starts}", budget, "evaluation(s)");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_settings.Algorithm == DifferentialEvolution.AlgorithmId)
                options["budget"] = budget.ToString(CultureInfo.InvariantCulture);
            var alg = OptimizerFactory.Create(_settings.Algorithm, u0, _settings.Seed + (ulong)s, options)!;
            var global = Drive(alg, all, from, terms, budget, rc);
            rc?.BeginStage($"Start {s + 1} of {_settings.Starts}: polish", 0, "evaluation(s)");
            finals.Add(Polish(global.Free, all, terms, rc));
        }
        var best = finals.MinBy(f => f.Cost)!;
        var spread = new double[_free.Length];
        for (int j = 0; j < spread.Length; j++)
            spread[j] = finals.Max(f => f.Free[j]) - finals.Min(f => f.Free[j]);
        return (best.Free, spread);
    }

    // ── Identifiability (R-mc5-4) ─────────────────────────────────────────────

    /// <summary>
    /// The residual Jacobian at the solution by central differences — in ln v for a logarithmic variable, so its column
    /// reads relative sensitivity — then <c>Cov = s²(JᵀJ)⁻¹</c>, <c>s² = SSR/(m − n)</c>: each variable's standard error
    /// and its correlation with every other ([R11] ch. 4).
    /// </summary>
    private IdentifiabilityReport Identify(double[] values, double[] spreads, FitTerms terms)
    {
        int n = _free.Length;
        double[] r0 = Residuals(Compose(values, _start), terms);
        int m = r0.Length;
        var jac = new double[m, n];
        for (int j = 0; j < n; j++)
        {
            var c = _free[j];
            double h = c.Log ? 1e-4 : 1e-4 * (c.Max - c.Min);
            double[] Step(double sgn)
            {
                var v = (double[])values.Clone();
                v[j] = c.Log ? values[j] * Math.Exp(sgn * h) : values[j] + sgn * h;
                return Residuals(Compose(v, _start), terms);
            }
            var rp = Step(+1);
            var rm = Step(-1);
            for (int k = 0; k < m; k++) jac[k, j] = (rp[k] - rm[k]) / (2 * h);
        }
        double ssr = r0.Sum(x => x * x);
        double s2 = ssr / Math.Max(1, m - n);

        // JᵀJ, normalised to a unit diagonal, inverted through its SVD (a direction the data does not see is a huge
        // variance, not a crash).
        var jtj = new double[n, n];
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                double sum = 0;
                for (int k = 0; k < m; k++) sum += jac[k, a] * jac[k, b];
                jtj[a, b] = sum;
            }
        var dScale = new double[n];
        for (int a = 0; a < n; a++) dScale[a] = jtj[a, a] > 0 ? 1 / Math.Sqrt(jtj[a, a]) : 0;
        var mat = new Mat<double>(n, n);
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
                mat[a, b] = jtj[a, b] * dScale[a] * dScale[b];
        var svd = mat.Svd();
        double smax = svd.S[0];
        var inv = new double[n, n];
        for (int q = 0; q < n; q++)
        {
            double sv = Math.Max(svd.S[q], 1e-15 * Math.Max(smax, 1e-300));
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++)
                    inv[a, b] += svd.U[a, q] * svd.U[b, q] / sv;
        }

        var vars = new List<FittedVariable>();
        for (int j = 0; j < n; j++)
        {
            var c = _free[j];
            double v = values[j];
            bool seen = dScale[j] > 0;
            double sigmaP = seen ? Math.Sqrt(s2 * inv[j, j]) * dScale[j] : double.PositiveInfinity;
            double stdErr = c.Log ? Math.Abs(v) * sigmaP : sigmaP;
            double rel = v != 0 ? stdErr / Math.Abs(v) : double.PositiveInfinity;
            string? partner = null;
            double rho = 0;
            for (int k = 0; k < n; k++)
            {
                if (k == j || !seen || dScale[k] == 0) continue;
                double rk = inv[j, k] / Math.Sqrt(inv[j, j] * inv[k, k]);
                if (Math.Abs(rk) > Math.Abs(rho)) (rho, partner) = (rk, _free[k].Name);
            }
            double u = c.Encode(v);
            string? atBound = u <= 0.005 ? "Min" : u >= 0.995 ? "Max" : null;
            bool poor = !seen || rel > _settings.PoorRelativeError || Math.Abs(rho) > _settings.PoorCorrelation;
            vars.Add(new FittedVariable(c.Name, v, c.Min, c.Max, stdErr, rel, spreads[j], atBound, partner, rho, poor));
        }

        var lines = new List<string>();
        foreach (var v in vars.Where(v => v.Poor))
        {
            if (double.IsInfinity(v.StdError)) lines.Add($"{v.Name} not determined by the data");
            else if (Math.Abs(v.Correlation) > _settings.PoorCorrelation)
                lines.Add($"{v.Name} poorly determined (ρ = {Signed(v.Correlation)} with {v.Partner})");
            else lines.Add($"{v.Name} poorly determined (±{(100 * v.RelativeError).ToString("0", CultureInfo.InvariantCulture)} %)");
        }
        foreach (var v in vars.Where(v => v.AtBound is not null))
            lines.Add($"{v.Name} at bound {v.AtBound} ({G(v.AtBound == "Min" ? v.Min : v.Max)})");
        // One suggestion per poorly determined variable or correlated pair, named once.
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in vars.Where(v => v.Poor))
        {
            if (!named.Add(v.Name)) continue;
            if (v.Partner is { } p && Math.Abs(v.Correlation) > _settings.PoorCorrelation)
            {
                named.Add(p);
                double other = vars.First(x => x.Name == p).Value;
                lines.Add($"Lock {v.Name}, or tie it to {p} × {G(v.Value / other)}.");
            }
            else lines.Add($"Lock {v.Name} at a typed value.");
        }
        return new IdentifiabilityReport(vars, lines);
    }

    private static string Signed(double x) =>
        (x < 0 ? "−" : "") + Math.Abs(x).ToString("0.00", CultureInfo.InvariantCulture);

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
