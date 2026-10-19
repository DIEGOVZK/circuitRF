namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// Even- and odd-mode characteristic impedance and effective permittivity of a symmetric pair of
/// edge-coupled microstrip lines, static and dispersive — the electrical core of MCLIN.
///
/// <para><b>Source: M. Kirschning and R. H. Jansen, "Accurate Wide-Range Design Equations for the
/// Frequency-Dependent Characteristic of Parallel Coupled Microstrip Lines," IEEE Trans. Microwave
/// Theory Tech., vol. MTT-32, no. 1, pp. 83-90, Jan. 1984, with the authors' correction in vol. MTT-33,
/// no. 3, p. 288, Mar. 1985</b> — the coupled-line companion of the single-line dispersion paper
/// <see cref="KirschningJansen"/> implements. Static part: εeff,e/o(0) and Z₀e/o(0) as closed-form
/// corrections of the single line's Hammerstad-Jensen values at the same width (eqs. for v, a_e, b_e,
/// a_o, b_o, c_o, d_o and Q₁-Q₁₀). Dispersion: εeff,e/o(f) by the P₁-P₁₅ forms, Z₀e(f) and Z₀o(f) by the
/// Q₁₁-Q₂₉ forms, both written in terms of the single line's dispersed values. Implemented here from
/// the published equations; no other program's source was used.</para>
///
/// <para><b>Provenance caveat, recorded as <see cref="KirschningJansen"/> records its own.</b> The
/// constants were transcribed from the published equations and have NOT been cross-checked against a
/// second independent transcription. What IS checked, by test: (1) the static values against
/// circuitRF's own 2D field solver (<see cref="Coupled.QuasiStaticSolver2D"/>) on representative
/// geometries, (2) every dispersive quantity reduces exactly to its static value as f → 0, and (3)
/// both modes reduce to the single line as the gap grows. An error in a dispersion coefficient would
/// show as a wrong high-frequency curvature, not as a discontinuity.</para>
///
/// <para><b>Validity (published): 0.1 ≤ W/h ≤ 10, 0.1 ≤ s/h ≤ 10, 1 ≤ εr ≤ 18</b>; the dispersion
/// is claimed to f·h ≈ 25 GHz·mm (provisional, as <see cref="KirschningJansen"/>'s range is). Values
/// outside are reported once per instance, never silently extrapolated.</para>
///
/// <para><b>Finite strip thickness</b> — three first-order corrections, each a published single-line or
/// coupled-line device, combined in capacitance terms so they compose: (1) both modes are evaluated at a
/// widened strip u + Δu_e, with Δu the single line's dielectric-weighted Hammerstad-Jensen correction
/// (<see cref="HammerstadJensen.ThicknessCorrectedWidths"/>) reduced for the neighbour as in Jansen's
/// coupled-line treatment (R. H. Jansen, IEEE Trans. MTT-26, no. 2, 1978): Δt = (t/h)/(εr·g),
/// Δu_e = Δu·(1 − ½·exp(−0.69·Δu/Δt)); (2) each mode's εeff is lowered by the single-line thickness term
/// (εr − 1)·(t/h)/(4.6·√u) (Bahl and Garg), the share of the field the strip's own height lifts into the
/// air; (3) the odd mode gains the parallel-plate capacitance of the two facing side walls to the
/// symmetry plane, 2·ε₀·t/s, in both C and C₀ (it is in air). Checked against circuitRF's 2D field solver
/// for t/h up to 0.1 (tests): Z within ~2.5 %, εeff within ~1.5 %.</para>
/// </summary>
public static class KirschningJansenCoupled
{
    public static readonly ValidityRange WOverHRange = new(0.1, 10.0);
    public static readonly ValidityRange GapOverHRange = new(0.1, 10.0);
    public static readonly ValidityRange EpsRRange = new(1.0, 18.0);
    public static readonly ValidityRange NormalizedFreqRangeProvisional = new(0.0, 25.0, "GHz·mm");

    private const double Eta0 = 376.730313668;

    /// <summary>The pair's modal quantities at one frequency.</summary>
    public readonly record struct Modes(double Ze, double Zo, double EeffE, double EeffO);

    /// <summary>Static (f → 0) even/odd effective permittivity for zero-thickness strips.</summary>
    public static (double EeffE, double EeffO) StaticEeff(double u, double g, double epsR)
    {
        double eeff = HammerstadJensen.StaticEeff(u, epsR);
        double v = u * (20.0 + g * g) / (10.0 + g * g) + g * Math.Exp(-g);
        double ae = HammerstadJensen.A(v), be = HammerstadJensen.B(epsR);
        double ee = (epsR + 1.0) / 2.0 + (epsR - 1.0) / 2.0 * Math.Pow(1.0 + 10.0 / v, -ae * be);

        double ao = 0.7287 * (eeff - (epsR + 1.0) / 2.0) * (1.0 - Math.Exp(-0.179 * u));
        double bo = 0.747 * epsR / (0.15 + epsR);
        double co = bo - (bo - 0.207) * Math.Exp(-0.414 * u);
        double dO = 0.593 + 0.694 * Math.Exp(-0.562 * u);
        double eo = ((epsR + 1.0) / 2.0 + ao - eeff) * Math.Exp(-co * Math.Pow(g, dO)) + eeff;
        return (ee, eo);
    }

    /// <summary>Q₄ (the even-mode impedance correction) — shared with the odd mode's Q₁₀.</summary>
    private static double Q4(double u, double g, out double q2)
    {
        double q1 = 0.8695 * Math.Pow(u, 0.194);
        q2 = 1.0 + 0.7519 * g + 0.189 * Math.Pow(g, 2.31);
        double q3 = 0.1975 + Math.Pow(16.6 + Math.Pow(8.4 / g, 6.0), -0.387)
                  + Math.Log(Math.Pow(g, 10.0) / (1.0 + Math.Pow(g / 3.4, 10.0))) / 241.0;
        return 2.0 * q1 / q2 / (Math.Exp(-g) * Math.Pow(u, q3) + (2.0 - Math.Exp(-g)) * Math.Pow(u, -q3));
    }

    /// <summary>Static even-mode impedance (zero thickness), given that mode's static εeff.</summary>
    public static double StaticZe(double u, double g, double epsR, double eeffE)
    {
        double eL = HammerstadJensen.StaticEeff(u, epsR);
        double zL = HammerstadJensen.StaticZ0Air(u) / Math.Sqrt(eL);
        double q4 = Q4(u, g, out _);
        return zL * Math.Sqrt(eL / eeffE) / (1.0 - zL / Eta0 * Math.Sqrt(eL) * q4);
    }

    /// <summary>Static odd-mode impedance (zero thickness), given that mode's static εeff.</summary>
    public static double StaticZo(double u, double g, double epsR, double eeffO)
    {
        double eL = HammerstadJensen.StaticEeff(u, epsR);
        double zL = HammerstadJensen.StaticZ0Air(u) / Math.Sqrt(eL);
        double q4 = Q4(u, g, out double q2);
        double q5 = 1.794 + 1.14 * Math.Log(1.0 + 0.638 / (g + 0.517 * Math.Pow(g, 2.43)));
        double q6 = 0.2305 + Math.Log(Math.Pow(g, 10.0) / (1.0 + Math.Pow(g / 5.8, 10.0))) / 281.3
                  + Math.Log(1.0 + 0.598 * Math.Pow(g, 1.154)) / 5.1;
        double q7 = (10.0 + 190.0 * g * g) / (1.0 + 82.3 * g * g * g);
        double q8 = Math.Exp(-6.5 - 0.95 * Math.Log(g) - Math.Pow(g / 0.15, 5.0));
        double q9 = Math.Log(q7) * (q8 + 1.0 / 16.5);
        double q10 = q4 - q5 / q2 * Math.Exp(q6 * Math.Log(u) / Math.Pow(u, q9));
        return zL * Math.Sqrt(eL / eeffO) / (1.0 - zL / Eta0 * Math.Sqrt(eL) * q10);
    }

    /// <summary>Dispersive even/odd εeff at the normalised frequency <paramref name="fn"/> (GHz·mm).</summary>
    public static (double EeffE, double EeffO) DispersiveEeff(double u, double g, double epsR,
        double eeffE0, double eeffO0, double fn)
    {
        double p1 = 0.27488 + (0.6315 + 0.525 / Math.Pow(1.0 + 0.0157 * fn, 20.0)) * u
                    - 0.065683 * Math.Exp(-8.7513 * u);
        double p2 = 0.33622 * (1.0 - Math.Exp(-0.03442 * epsR));
        double p3 = 0.0363 * Math.Exp(-4.6 * u) * (1.0 - Math.Exp(-Math.Pow(fn / 38.7, 4.97)));
        double p4 = 1.0 + 2.751 * (1.0 - Math.Exp(-Math.Pow(epsR / 15.916, 8.0)));
        double p5 = 0.334 * Math.Exp(-3.3 * Math.Pow(epsR / 15.0, 3.0)) + 0.746;
        double p6 = p5 * Math.Exp(-Math.Pow(fn / 18.0, 0.368));
        double p7 = 1.0 + 4.069 * p6 * Math.Pow(g, 0.479) * Math.Exp(-1.347 * Math.Pow(g, 0.595) - 0.17 * Math.Pow(g, 2.5));
        double fe = p1 * p2 * Math.Pow((p3 * p4 + 0.1844 * p7) * fn, 1.5763);

        double p8 = 0.7168 * (1.0 + 1.076 / (1.0 + 0.0576 * (epsR - 1.0)));
        double p9 = p8 - 0.7913 * (1.0 - Math.Exp(-Math.Pow(fn / 20.0, 1.424))) * Math.Atan(2.481 * Math.Pow(epsR / 8.0, 0.946));
        double p10 = 0.242 * Math.Pow(epsR - 1.0, 0.55);
        double p11 = 0.6366 * (Math.Exp(-0.3401 * fn) - 1.0) * Math.Atan(1.263 * Math.Pow(u / 3.0, 1.629));
        double p12 = p9 + (1.0 - p9) / (1.0 + 1.183 * Math.Pow(u, 1.376));
        double p13 = 1.695 * p10 / (0.414 + 1.605 * p10);
        double p14 = 0.8928 + 0.1072 * (1.0 - Math.Exp(-0.42 * Math.Pow(fn / 20.0, 3.215)));
        double p15 = Math.Abs(1.0 - 0.8928 * (1.0 + p11) * p12 * Math.Exp(-p13 * Math.Pow(g, 1.092)) / p14);
        double fo = p1 * p2 * Math.Pow((p3 * p4 + 0.1844) * fn * p15, 1.5763);

        return (epsR - (epsR - eeffE0) / (1.0 + fe), epsR - (epsR - eeffO0) / (1.0 + fo));
    }

    /// <summary>
    /// Dispersive even/odd impedance. <paramref name="zL"/>/<paramref name="eL0"/>/<paramref name="eLf"/>
    /// are the SINGLE line's static impedance, static εeff and εeff dispersed at <paramref name="fn"/>, at
    /// the same width; <paramref name="zLf"/> is the single line's dispersed impedance.
    /// </summary>
    public static (double Ze, double Zo) DispersiveZ(double u, double g, double epsR, double fn,
        double ze0, double zo0, double eeffO0, double eeffOf, double eL0, double eLf, double zLf)
    {
        double er1 = epsR - 1.0;

        // Even mode.
        double q11 = 0.893 * (1.0 - 0.3 / (1.0 + 0.7 * er1));
        double f20 = Math.Pow(fn / 20.0, 4.91);
        double q12 = 2.121 * (f20 / (1.0 + q11 * f20)) * Math.Exp(-2.87 * g) * Math.Pow(g, 0.902);
        double q13 = 1.0 + 0.038 * Math.Pow(epsR / 8.0, 5.1);
        double e15 = Math.Pow(epsR / 15.0, 4.0);
        double q14 = 1.0 + 1.203 * e15 / (1.0 + e15);
        double q15 = 1.887 * Math.Exp(-1.5 * Math.Pow(g, 0.84)) * Math.Pow(g, q14)
                   / (1.0 + 0.41 * Math.Pow(fn / 15.0, 3.0) * Math.Pow(u, 2.0 / q13) / (0.125 + Math.Pow(u, 1.626 / q13)));
        double q16 = q15 * (1.0 + 9.0 / (1.0 + 0.403 * er1 * er1));
        double q17 = 0.394 * (1.0 - Math.Exp(-1.47 * Math.Pow(u / 7.0, 0.672))) * (1.0 - Math.Exp(-4.25 * Math.Pow(fn / 20.0, 1.87)));
        double q18 = 0.61 * (1.0 - Math.Exp(-2.13 * Math.Pow(u / 8.0, 1.593))) / (1.0 + 6.544 * Math.Pow(g, 4.17));
        double q19 = 0.21 * Math.Pow(g, 4.0) / ((1.0 + 0.18 * Math.Pow(g, 4.9)) * (1.0 + 0.1 * u * u) * (1.0 + Math.Pow(fn / 24.0, 3.0)));
        double q20 = q19 * (0.09 + 1.0 / (1.0 + 0.1 * Math.Pow(er1, 2.7)));
        double u25 = Math.Pow(u, 2.5);
        double q21 = Math.Abs(1.0 - 42.54 * Math.Pow(g, 0.133) * Math.Exp(-0.812 * g) * u25 / (1.0 + 0.033 * u25));
        double re = Math.Pow(fn / 28.843, 12.0);
        double qe = 0.016 + Math.Pow(0.0514 * epsR * q21, 4.524);
        double pe = 4.766 * Math.Exp(-3.228 * Math.Pow(u, 0.641));
        double er16 = Math.Pow(er1, 6.0);
        double de = 5.086 * qe * re / (0.3838 + 0.386 * qe) * Math.Exp(-22.2 * Math.Pow(u, 1.92)) / (1.0 + 1.2992 * re)
                  * er16 / (1.0 + 10.0 * er16);
        double ce = 1.0 + 1.275 * (1.0 - Math.Exp(-0.004625 * pe * Math.Pow(epsR, 1.674) * Math.Pow(fn / 18.365, 2.745)))
                  - q12 + q16 - q17 + q18 + q20;
        double q0 = KirschningJansen.ImpedanceDispersionExponent(u, epsR, fn);
        double ze = ze0 * Math.Pow((0.9408 * Math.Pow(eLf, ce) - 0.9603) / ((0.9408 - de) * Math.Pow(eL0, ce) - 0.9603), q0);

        // Odd mode.
        double r13 = Math.Pow(er1 / 13.0, 12.0);
        double q29 = 15.16 / (1.0 + 0.196 * er1 * er1);
        double q28 = 0.149 * Math.Pow(er1, 3.0) / (94.5 + 0.038 * Math.Pow(er1, 3.0));
        double q27 = 0.4 * Math.Pow(g, 0.84) * (1.0 + 2.5 * Math.Pow(er1, 1.5) / (5.0 + Math.Pow(er1, 1.5)));
        double q26 = 30.0 - 22.2 * r13 / (1.0 + 3.0 * r13) - q29;
        double q25 = 0.3 * fn * fn / (10.0 + fn * fn) * (1.0 + 2.333 * er1 * er1 / (5.0 + er1 * er1));
        double q24 = 2.506 * q28 * Math.Pow(u, 0.894) / (3.575 + Math.Pow(u, 0.894))
                   * Math.Pow((1.0 + 1.3 * u) * fn / 99.25, 4.29);
        double q23 = 1.0 + 0.005 * fn * q27 / ((1.0 + 0.812 * Math.Pow(fn / 15.0, 1.9)) * (1.0 + 0.025 * u * u));
        double q22 = 0.925 * Math.Pow(fn / q26, 1.536) / (1.0 + 0.3 * Math.Pow(fn / 30.0, 1.536));
        double zo = zLf + (zo0 * Math.Pow(eeffOf / eeffO0, q22) - zLf * q23) / (1.0 + q24 + Math.Pow(0.46 * g, 2.2) * q25);

        return (ze, zo);
    }

    /// <summary>The thickness-widened strip both modes are evaluated at — see the class comment.</summary>
    public static double ThicknessCorrectedWidth(double u, double g, double tOverH, double epsR)
    {
        if (!(tOverH > 0.0)) return u;
        HammerstadJensen.ThicknessCorrectedWidths(u, tOverH, epsR, out _, out double uR);
        double du = uR - u;
        double dt = tOverH / (epsR * g);
        return u + du * (1.0 - 0.5 * Math.Exp(-0.69 * du / dt));
    }

    /// <summary>
    /// Static modes WITH the thickness corrections of the class comment: the widened strip's
    /// Kirschning-Jansen values, then the εeff reduction and the odd mode's side-wall capacitance.
    /// </summary>
    public static Modes StaticModes(double u, double g, double tOverH, double epsR)
    {
        double ut = ThicknessCorrectedWidth(u, g, tOverH, epsR);
        var (ee, eo) = StaticEeff(ut, g, epsR);
        double ze = StaticZe(ut, g, epsR, ee), zo = StaticZo(ut, g, epsR, eo);
        if (!(tOverH > 0.0)) return new Modes(ze, zo, ee, eo);

        const double c = MicrostripLoss.SpeedOfLight;
        double c0e = 1.0 / (c * ze * Math.Sqrt(ee)), c0o = 1.0 / (c * zo * Math.Sqrt(eo));
        double de = (epsR - 1.0) * tOverH / (4.6 * Math.Sqrt(u));
        double ce = (ee - de) * c0e, co = (eo - de) * c0o;
        double wall = 2.0 * Coupled.QuasiStaticSolver2D.Eps0 * tOverH / g;
        c0o += wall; co += wall;
        return new Modes(1.0 / (c * Math.Sqrt(ce * c0e)), 1.0 / (c * Math.Sqrt(co * c0o)), ce / c0e, co / c0o);
    }

    /// <summary>
    /// The pair's Z₀e, Z₀o, εeff,e and εeff,o at <paramref name="freqHz"/> (0 = static) for strips of
    /// width W and edge gap S, thickness T, on a substrate of height H and permittivity εr. Reports any
    /// parameter outside the published range through <paramref name="reporter"/>.
    /// </summary>
    public static Modes Compute(double freqHz, double wMeters, double sMeters, double hMeters, double tMeters,
        double epsR, MicrostripValidityReporter reporter)
    {
        const string model = "Kirschning-Jansen coupled";
        double u = wMeters / hMeters, g = sMeters / hMeters;
        reporter.CheckRange(model, "W/h", u, WOverHRange.Min, WOverHRange.Max);
        reporter.CheckRange(model, "S/h", g, GapOverHRange.Min, GapOverHRange.Max);
        reporter.CheckRange(model, "er", epsR, EpsRRange.Min, EpsRRange.Max);

        double ut = ThicknessCorrectedWidth(u, g, tMeters / hMeters, epsR);
        var st = StaticModes(u, g, tMeters / hMeters, epsR);
        if (!(freqHz > 0.0)) return st;

        double fn = KirschningJansen.NormalizedFreqGhzMm(freqHz, hMeters);
        reporter.CheckRange(model + " (dispersion, provisional range)", "f*h", fn,
            NormalizedFreqRangeProvisional.Min, NormalizedFreqRangeProvisional.Max, NormalizedFreqRangeProvisional.Units);

        var (eeEf, eeOf) = DispersiveEeff(ut, g, epsR, st.EeffE, st.EeffO, fn);

        // The single line at the same width: static and dispersed εeff, and dispersed Z (for the odd mode).
        double eL0 = HammerstadJensen.StaticEeff(ut, epsR);
        double eLf = KirschningJansen.DispersiveEeff(ut, epsR, eL0, fn);
        double zL0 = HammerstadJensen.StaticZ0Air(ut) / Math.Sqrt(eL0);
        double zLf = KirschningJansen.DispersiveZ0(ut, epsR, zL0, eL0, eLf, fn);

        var (ze, zo) = DispersiveZ(ut, g, epsR, fn, st.Ze, st.Zo, st.EeffO, eeOf, eL0, eLf, zLf);
        return new Modes(ze, zo, eeEf, eeOf);
    }
}
