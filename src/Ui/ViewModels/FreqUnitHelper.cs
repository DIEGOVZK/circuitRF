using System;
using System.Globalization;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Shared frequency-unit helpers for SP and HB body VMs.
/// Converts between Hz expression strings and (coefficient, unit) display pairs.
/// </summary>
internal static class FreqUnitHelper
{
    internal static readonly string[] Units = ["Hz", "kHz", "MHz", "GHz"];

    internal static double Multiplier(string unit) => unit switch
    {
        "kHz" => 1e3,
        "MHz" => 1e6,
        "GHz" => 1e9,
        _     => 1.0,
    };

    /// <summary>
    /// Splits a Hz expression into a (coefficient, unit) pair for display, in the preferred unit
    /// (Settings ▸ General ▸ Units) or, under Automatic, the best one for its magnitude.
    /// Non-numeric expressions are returned as-is with unit "Hz".
    /// </summary>
    internal static (string Coeff, string Unit) Split(string hzExpr)
        => Split(hzExpr, CircuitRF.Ui.Theming.PreferredUnits.Frequency);

    /// <summary>
    /// <see cref="Split(string)"/> with the preference given: a unit expresses the coefficient in it
    /// (<c>1e8</c> → <c>0.1</c> GHz); null is Automatic, the magnitude rule.
    /// </summary>
    internal static (string Coeff, string Unit) Split(string hzExpr, string? preferredUnit)
    {
        if (NumericText.TryParseDouble(hzExpr.Trim(), out double hz))
        {
            if (preferredUnit is { } pu && Array.IndexOf(Units, pu) >= 0)
                return (Fmt(hz / Multiplier(pu)), pu);

            double abs = Math.Abs(hz);
            if (abs >= 1e9) return (Fmt(hz / 1e9), "GHz");
            if (abs >= 1e6) return (Fmt(hz / 1e6), "MHz");
            if (abs >= 1e3) return (Fmt(hz / 1e3), "kHz");
            return (Fmt(hz), "Hz");
        }
        return (hzExpr.Trim(), "Hz");
    }

    /// <summary>
    /// Rescales a numeric coefficient from one unit to another, keeping Hz value constant.
    /// Returns the original string unchanged when the input is a symbolic expression.
    /// </summary>
    internal static string Rescale(string coeff, string fromUnit, string toUnit)
    {
        if (fromUnit == toUnit) return coeff;
        if (!NumericText.TryParseDouble(coeff.Trim(), out double v)) return coeff;
        double scale = Multiplier(fromUnit) / Multiplier(toUnit);
        return Fmt(v * scale);
    }

    private static string Fmt(double v)
        => v.ToString("G6", CultureInfo.InvariantCulture);
}
