using System.Globalization;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Layout;

/// <summary>
/// brief-sar-2 — a frequency-dependent material evaluated at ONE frequency (overview L6): a Cole-Cole tissue
/// (<see cref="TechMaterial.ColeCole"/>) or a tabulated phantom liquid (<see cref="TechMaterial.DielectricVsFrequency"/>).
/// Every solver still receives a plain εr and σ: this runs once per material, before lowering, and never per time step.
/// The one rule — the elaborator, <c>check</c>, <c>explain --materials</c> and the material editor all call it.
/// </summary>
public static class MaterialDispersion
{
    /// <summary>Where <c>check</c> compares a record's plain numbers with its own model (R-sar2-1).</summary>
    public const double PlainNumbersFrequencyHz = 1e9;

    /// <summary>How far apart they may be before <c>check</c> warns: 1 %, em-3d.md §4.1a's rule for a number nothing reads.</summary>
    public const double DisagreementFraction = 0.01;

    /// <summary>The most terms a Cole-Cole block may state.</summary>
    public const int MaxTerms = 4;

    /// <summary>Whether <paramref name="m"/> states a frequency-dependent εr and σ.</summary>
    public static bool IsDispersive(TechMaterial m) => m.ColeCole is not null || m.DielectricVsFrequency is { Count: > 0 };

    /// <summary>
    /// εr and the total σ (S/m) of <paramref name="m"/> at <paramref name="frequencyHz"/>, or null with
    /// <paramref name="refusal"/> saying why: a table asked outside its rows, a malformed model. Null with no refusal for a
    /// material that is not dispersive.
    /// </summary>
    public static (double Epsr, double SigmaSm)? At(TechMaterial m, double frequencyHz, out string? refusal)
    {
        refusal = null;
        if (m.ColeCole is { } cc)
        {
            if (Fault(cc) is { } fault) { refusal = $"Material \"{m.Name}\" states a Cole-Cole model whose {fault}."; return null; }
            return ColeCole.EpsrSigma(frequencyHz, cc.EpsInf, cc.SigmaIonic, Terms(cc));
        }
        if (m.DielectricVsFrequency is not { Count: > 0 } table) return null;
        if (TableFault(table) is { } tf) { refusal = $"Material \"{m.Name}\" states DielectricVsFrequency, but {tf}."; return null; }
        double lo = table[0].FrequencyHz, hi = table[^1].FrequencyHz;
        if (frequencyHz < lo || frequencyHz > hi)
        {
            refusal = $"Material \"{m.Name}\" is tabulated from {Hz(lo)} to {Hz(hi)}, and it is asked for at {Hz(frequencyHz)}: a " +
                      "target table is interpolated between its rows and never extrapolated beyond them. Use a material tabulated there.";
            return null;
        }
        for (int i = 1; i < table.Count; i++)
        {
            var (a, b) = (table[i - 1], table[i]);
            if (frequencyHz > b.FrequencyHz) continue;
            double t = b.FrequencyHz == a.FrequencyHz ? 0 : (frequencyHz - a.FrequencyHz) / (b.FrequencyHz - a.FrequencyHz);
            return (a.Epsr + t * (b.Epsr - a.Epsr), a.SigmaSm + t * (b.SigmaSm - a.SigmaSm));
        }
        return (table[0].Epsr, table[0].SigmaSm);   // a one-row table, asked at its own frequency
    }

    /// <summary>The model's terms, as the engine's formula takes them.</summary>
    public static IReadOnlyList<ColeColeTerm> Terms(TechColeCole cc)
        => [.. cc.Terms.Select(t => new ColeColeTerm(t.DeltaEps, t.TauSeconds, t.Alpha))];

    /// <summary>What is wrong with a Cole-Cole block on its own, as a phrase after "whose", or null.</summary>
    public static string? Fault(TechColeCole cc)
    {
        if (!double.IsFinite(cc.EpsInf) || cc.EpsInf < 1) return "ε∞ (EpsInf) is not a finite number of at least 1";
        if (!double.IsFinite(cc.SigmaIonic) || cc.SigmaIonic < 0) return "ionic conductivity (SigmaIonic) is not a finite number of at least 0 S/m";
        if (cc.Terms.Count is < 1 or > MaxTerms) return $"Terms has {cc.Terms.Count} entries; a Cole-Cole model has one to {MaxTerms}";
        for (int i = 0; i < cc.Terms.Count; i++)
        {
            var t = cc.Terms[i];
            if (!double.IsFinite(t.DeltaEps) || t.DeltaEps < 0) return $"term {i + 1}'s DeltaEps is not a finite number of at least 0";
            if (!(t.TauSeconds > 0) || !double.IsFinite(t.TauSeconds)) return $"term {i + 1}'s TauSeconds is not a positive number of seconds";
            if (!double.IsFinite(t.Alpha) || t.Alpha < 0 || t.Alpha >= 1) return $"term {i + 1}'s Alpha is not in 0 ≤ α < 1";
        }
        return null;
    }

    /// <summary>What is wrong with a target table on its own, as a clause, or null.</summary>
    public static string? TableFault(IReadOnlyList<TechFrequencyPoint> table)
    {
        for (int i = 0; i < table.Count; i++)
        {
            var p = table[i];
            if (!(p.FrequencyHz > 0) || !double.IsFinite(p.FrequencyHz)) return $"row {i + 1}'s frequency is not a positive number of Hz";
            if (!double.IsFinite(p.Epsr) || p.Epsr < 1) return $"row {i + 1}'s εr is not a finite number of at least 1";
            if (!double.IsFinite(p.SigmaSm) || p.SigmaSm < 0) return $"row {i + 1}'s σ is not a finite number of at least 0 S/m";
            if (i > 0 && p.FrequencyHz < table[i - 1].FrequencyHz) return $"its frequencies do not increase (row {i + 1})";
        }
        return null;
    }

    /// <summary>
    /// R-sar2-1 — the record's plain <see cref="TechMaterial.Epsr"/>/<see cref="TechMaterial.Sigma20"/> against its own model at
    /// <see cref="PlainNumbersFrequencyHz"/>: the sentence <c>check</c> warns with, or null when they agree (or the model cannot
    /// be evaluated there — a table not reaching 1 GHz is compared at nothing). A missing plain number is reported too: a build
    /// that does not read the model would see no εr or σ at all.
    /// </summary>
    public static string? PlainNumbersDisagreement(TechMaterial m)
    {
        if (!IsDispersive(m)) return null;
        if (At(m, PlainNumbersFrequencyHz, out _) is not { } model) return null;
        if (m.Epsr is null || m.Sigma20 is null)
            return string.Create(CultureInfo.InvariantCulture,
                $"Material \"{m.Name}\" states a frequency-dependent model but not {(m.Epsr is null ? "Epsr" : "Sigma20")}: a build that does not read the model sees no value. State the model's values at 1 GHz (εr {model.Epsr:G4}, σ {model.SigmaSm:G4} S/m).");
        bool epsOff = Math.Abs(m.Epsr.Value - model.Epsr) > DisagreementFraction * Math.Abs(model.Epsr);
        bool sigOff = Math.Abs(m.Sigma20.Value - model.SigmaSm) > DisagreementFraction * Math.Abs(model.SigmaSm);
        if (!epsOff && !sigOff) return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"Material \"{m.Name}\" states Epsr {m.Epsr:G5} and Sigma20 {m.Sigma20:G5} S/m, and its model gives εr {model.Epsr:G5}, σ {model.SigmaSm:G5} S/m at 1 GHz. A 3D run uses the model; a build without it uses the plain numbers. Make them agree.");
    }

    /// <summary>A frequency as a sentence states it: <c>298 MHz</c>, <c>1.8 GHz</c>.</summary>
    public static string Hz(double f) => f switch
    {
        >= 1e9 => (f / 1e9).ToString("0.####", CultureInfo.InvariantCulture) + " GHz",
        >= 1e6 => (f / 1e6).ToString("0.####", CultureInfo.InvariantCulture) + " MHz",
        >= 1e3 => (f / 1e3).ToString("0.####", CultureInfo.InvariantCulture) + " kHz",
        _ => f.ToString("0.####", CultureInfo.InvariantCulture) + " Hz",
    };
}
