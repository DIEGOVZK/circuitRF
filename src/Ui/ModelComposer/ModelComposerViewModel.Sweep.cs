// The model sweep (Model Composer review 2): start, stop and step for vgs and for vds, on the toolbar where the bias cursor
// sits, for the tabs that evaluate a fitted model rather than show the data at a measured bias — the Current step's two
// Model tabs and the Charge step's C–V slices. On those tabs the cursor's lists would do nothing, and every other tab
// shows only the cursor lists it reads (none, on most of them), so the strip says what the plot in front of it reads.
//
// Both voltages are INTRINSIC, the plane the currents and charges are fitted on. One voltage is swept along the x axis and
// the other steps the family: the swept one is drawn at its step or at 101 points, whichever is finer, so a coarse family
// step never makes a coarse curve. View state only — not written to the .cmc and never an undo step. Until the user edits
// it, it follows the step's data span.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Design.Modeling;
using CircuitRF.Render.DataDisplay;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelComposerViewModel
{
    public const string ModelVdsTab = "Model (vds)", ModelVgsTab = "Model (vgs)";

    /// <summary>Most curves a family draws, and most points a swept voltage is drawn at.</summary>
    public const int MaxFamily = 41, MaxSwept = 2001;

    private const int MinSwept = 101;

    private string _sweepVgsStart = "", _sweepVgsStop = "", _sweepVgsStep = "", _sweepVdsStart = "", _sweepVdsStop = "", _sweepVdsStep = "";
    private bool _sweepEdited;
    private string? _sweepDefaultsFor;

    public string SweepVgsStart { get => _sweepVgsStart; set => SetSweep(ref _sweepVgsStart, value, nameof(SweepVgsStart)); }
    public string SweepVgsStop { get => _sweepVgsStop; set => SetSweep(ref _sweepVgsStop, value, nameof(SweepVgsStop)); }
    public string SweepVgsStep { get => _sweepVgsStep; set => SetSweep(ref _sweepVgsStep, value, nameof(SweepVgsStep), step: true); }
    public string SweepVdsStart { get => _sweepVdsStart; set => SetSweep(ref _sweepVdsStart, value, nameof(SweepVdsStart)); }
    public string SweepVdsStop { get => _sweepVdsStop; set => SetSweep(ref _sweepVdsStop, value, nameof(SweepVdsStop)); }
    public string SweepVdsStep { get => _sweepVdsStep; set => SetSweep(ref _sweepVdsStep, value, nameof(SweepVdsStep), step: true); }

    private void SetSweep(ref string field, string? value, string name, bool step = false)
    {
        string text = (value ?? "").Trim();
        if (text == field) return;
        if (!TryVolts(text, out double v) || step && !(v > 0))
        {
            Error($"Model sweep: '{text}' is not {(step ? "a positive step" : "a voltage")}.");
            OnPropertyChanged(name);   // the box goes back to the value it had
            return;
        }
        field = Volts(v);
        _sweepEdited = true;
        OnPropertyChanged(name);
        RebuildPlots();
    }

    private static bool TryVolts(string text, out double v) =>
        ModelValueFormat.TryParse(text, "V", out v) && double.IsFinite(v) || TryNum(text, out v);

    private static string Volts(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>Fills the sweep from the step's data span — vgs and vds each from its lowest to its highest value, stepped in
    /// about eight — until the user edits it.</summary>
    private void DefaultSweep()
    {
        if (_sweepEdited || StepPlots?.DataSpan is not { } span) return;
        string key = $"{StepPlots.Step}|{span}";
        if (key == _sweepDefaultsFor) return;
        _sweepDefaultsFor = key;
        (_sweepVgsStart, _sweepVgsStop, _sweepVgsStep) = Range(span.VgsLo, span.VgsHi);
        (_sweepVdsStart, _sweepVdsStop, _sweepVdsStep) = Range(span.VdsLo, span.VdsHi);
        foreach (string p in new[] { nameof(SweepVgsStart), nameof(SweepVgsStop), nameof(SweepVgsStep),
                                     nameof(SweepVdsStart), nameof(SweepVdsStop), nameof(SweepVdsStep) })
            OnPropertyChanged(p);

        static (string, string, string) Range(double lo, double hi)
        {
            double step = Nice(Math.Max(hi - lo, 1e-3) / 8);
            return (Volts(Math.Floor(lo / step + 1e-9) * step), Volts(Math.Ceiling(hi / step - 1e-9) * step), Volts(step));
        }
    }

    /// <summary>1, 2, 2.5 or 5 times a power of ten — the nearest at or above <paramref name="x"/>.</summary>
    private static double Nice(double x)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(x)));
        foreach (double m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * p >= x * (1 - 1e-9)) return m * p;
        return 10 * p;
    }

    /// <summary>The family a voltage steps: start, start + step, … up to stop, at most <see cref="MaxFamily"/>.</summary>
    private static IReadOnlyList<double>? Family(string start, string stop, string step)
    {
        if (!TryVolts(start, out double a) || !TryVolts(stop, out double b) || !TryVolts(step, out double s) || !(s > 0)) return null;
        if (b < a) (a, b) = (b, a);
        int n = (int)Math.Floor((b - a) / s + 1e-9) + 1;
        return n > MaxFamily ? null : [.. Enumerable.Range(0, n).Select(k => Math.Round(a + k * s, 9))];
    }

    /// <summary>The span and point count a voltage is swept over: its step, or <see cref="MinSwept"/> points if finer.</summary>
    private static (double Lo, double Hi, int Points)? Swept(string start, string stop, string step)
    {
        if (!TryVolts(start, out double a) || !TryVolts(stop, out double b) || !TryVolts(step, out double s) || !(s > 0) || a == b) return null;
        if (b < a) (a, b) = (b, a);
        int n = (int)Math.Floor((b - a) / s + 1e-9) + 1;
        return (a, b, Math.Clamp(n, MinSwept, MaxSwept));
    }

    /// <summary>What a family that does not fit says.</summary>
    private string SweepRefusal(bool gateFamily) =>
        $"Model sweep: {(gateFamily ? "vgs" : "vds")} steps more than {MaxFamily} curves, or its start, stop or step is not a voltage.";

    /// <summary>The temperature a model sweep is evaluated at: the cursor's, else the data's.</summary>
    private double SweepTemperature(StepPlotData sp) =>
        SelectedT?.Value is { } t && double.IsFinite(t) ? t
        : sp.Sets.Select(n => Document.DataSets.FirstOrDefault(s => s.Name == n)?.Conditions.AmbientC).FirstOrDefault(a => a is not null) ?? 25;

    /// <summary>The Current step's Model tabs: the fitted Id along vds, one curve per vgs (or the reverse), intrinsic.</summary>
    private void ModelSweepPlot(Plot plot, StepPlotData sp, bool sweepDrain)
    {
        double t = SweepTemperature(sp);
        string fx = sweepDrain ? "vds" : "vgs", ff = sweepDrain ? "vgs" : "vds";
        plot.CustomTitle = $"Model Id({fx}) · {ModelValueFormat.G(t)} °C · dashed beyond the data";
        Labels(plot, $"{fx} (V)", "Id (mA)");
        var family = sweepDrain ? Family(SweepVgsStart, SweepVgsStop, SweepVgsStep) : Family(SweepVdsStart, SweepVdsStop, SweepVdsStep);
        var swept = sweepDrain ? Swept(SweepVdsStart, SweepVdsStop, SweepVdsStep) : Swept(SweepVgsStart, SweepVgsStop, SweepVgsStep);
        if (family is null || swept is not { } x) { Warn(SweepRefusal(gateFamily: sweepDrain)); return; }
        if (sp.CurrentSweep(family, x.Lo, x.Hi, x.Points, sweepDrain, t) is not { } ds) return;
        var cube = ds["Id"];
        double[] xs = cube.Axes[1].Values, id = cube.RealValues, inside = ds["Inside"].RealValues;
        int n = xs.Length;
        for (int f = 0; f < family.Count; f++)
        {
            // Inside the training domain the fit, solid; beyond it the model's continuation, dashed and left out of the
            // autoscale — it can run to amperes there, and would otherwise flatten everything the model was fitted to.
            bool In(int k) => inside[f * n + k] > 0.5;
            double Y(int k) => id[f * n + k] * 1e3;
            string name = $"{ff} {ModelValueFormat.G(family[f])} V";
            plot.Traces.Add(Xy(name, xs, [.. Enumerable.Range(0, n).Select(k => In(k) ? Y(k) : double.NaN)], fx, "V", FamilyColour, line: true, marker: null));
            // Beyond: every point outside, and the inside points next to one, so the dashed piece meets the solid one.
            var beyond = Enumerable.Range(0, n).Select(k => !In(k) || k > 0 && !In(k - 1) || k < n - 1 && !In(k + 1) ? Y(k) : double.NaN).ToArray();
            if (Enumerable.Range(0, n).Any(k => !In(k)))
            {
                var dashed = Xy($"{name} beyond", xs, beyond, fx, "V", FamilyColour, line: true, marker: null);
                dashed.Properties.LineType = LineType.Dashed;
                dashed.Properties.Custom = false;
                dashed.ExcludeFromAutoscale = true;
                plot.Traces.Add(dashed);
            }
        }
    }

    /// <summary>The C–V slices of the sweep (R-mc15-3): along one voltage over its sweep span, one family member per value of
    /// the other — measured symbols where the data lies near a member, model lines.</summary>
    private void CvSweep(Plot plot, StepPlotData sp, bool sweepGate)
    {
        string swept = sweepGate ? "vgs" : "vds", fixedName = sweepGate ? "vds" : "vgs";
        Labels(plot, $"{swept} (V)", "C (fF)");
        var family = sweepGate ? Family(SweepVdsStart, SweepVdsStop, SweepVdsStep) : Family(SweepVgsStart, SweepVgsStop, SweepVgsStep);
        var x = sweepGate ? Swept(SweepVgsStart, SweepVgsStop, SweepVgsStep) : Swept(SweepVdsStart, SweepVdsStop, SweepVdsStep);
        if (family is null || x is not { } span) { Warn(SweepRefusal(gateFamily: !sweepGate)); return; }
        plot.CustomTitle = family.Count == 1
            ? $"C–V against {swept} · {fixedName} {ModelValueFormat.G(family[0])} V"
            : $"C–V against {swept} · {fixedName} {ModelValueFormat.G(family[0])} … {ModelValueFormat.G(family[^1])} V";
        IReadOnlyList<string> names = CvQuantity == "Circuit set" ? ["Cgs", "Cgd", "Cds", "Cm"] : [CvQuantity];
        LastSlice = null;
        foreach (double fixedValue in family)
        {
            if (sp.Slice(sweepGate, fixedValue, (span.Lo, span.Hi), span.Points) is not { } ds) return;
            LastSlice ??= (sweepGate, fixedValue, ds);
            string member = family.Count == 1 ? "" : $" {fixedName} {ModelValueFormat.G(fixedValue)} V";
            double[] xm = R(ds, "x.meas");
            int c = 0;
            foreach (string n in names)
            {
                var model = ds[$"{n}.model"];
                plot.Traces.Add(Xy($"{n} model{member}", model.Axes[0].Values, [.. model.RealValues.Select(v => v * 1e15)], swept, "V", c, line: true, marker: null));
                if (xm.Length > 0)
                {
                    var m = Xy($"{n}{member}", xm, [.. R(ds, $"{n}.meas").Select(v => v * 1e15)], swept, "V", c, line: false, marker: MarkerType.Circle);
                    m.Properties.MarkerSize = SmallMarker;
                    m.Properties.Custom = false;
                    plot.Traces.Add(m);
                }
                c++;
            }
        }
    }

    // ── which toolbar controls the selected tab reads ────────────────────────

    public bool ShowVgsCursor => CursorUse().Vgs;
    public bool ShowVdsCursor => CursorUse().Vds;
    public bool ShowTCursor => CursorUse().T;
    public bool ShowQCursor => CursorUse().Q;
    public bool ShowSweep => CursorUse().Sweep;

    /// <summary>
    /// The cursor lists the selected tab reads, and whether it reads the model sweep. Vgs and Vds pick the measured point a
    /// frequency plot is drawn at, or the fixed voltage of a Gm, Gds or pulsed slice. T and Q pick a step's data set only
    /// when the data list's selection is not one of its sets, and T is the temperature a model sweep is evaluated at.
    /// </summary>
    private (bool Vgs, bool Vds, bool T, bool Q, bool Sweep) CursorUse()
    {
        if (IsChoosingKind || IsPassive) return (false, false, false, false, false);
        bool tq = StepPlots is { } sp && !sp.Sets.Contains(SelectedDataSet?.Name ?? "") && (TChoices.Count > 1 || QChoices.Count > 1);
        bool t = TChoices.Count > 1;
        return (StepName, SelectedPlotTab) switch
        {
            (_, BiasPlaneTab) => (true, true, false, false, false),
            ("Data" or "Pads" or "Validate", STab or SmithTab) => (true, true, false, false, false),
            ("Network", CapacitanceTab or ConductanceTab) => (true, true, false, false, false),
            ("Current" or "Traps", GmTab) => (false, true, tq && t, tq, false),
            ("Current" or "Traps", GdsTab) => (true, false, tq && t, tq, false),
            ("Traps", PulsedTab) => (true, false, false, false, false),
            ("Current" or "Export", OutputTab or TransferTab) or ("Current", ErrorTab) or ("Thermal" or "Traps", DcIvTab)
                => (false, false, tq && t, tq, false),
            ("Current", ModelVdsTab or ModelVgsTab) => (false, false, t, false, true),
            ("Charge", CvGateTab or CvDrainTab) => (false, false, false, false, true),
            _ => (false, false, false, false, false),
        };
    }

    private void NotifyCursorUse()
    {
        foreach (string p in new[] { nameof(ShowVgsCursor), nameof(ShowVdsCursor), nameof(ShowTCursor), nameof(ShowQCursor), nameof(ShowSweep) })
            OnPropertyChanged(p);
    }
}
