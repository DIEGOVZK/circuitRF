// Values as the Model Composer window shows and reads them: engineering notation with the element's unit ("39.5 pH",
// "5.2 Ω"), and the same spelling read back. A typed trailing unit is honoured when it is the element's own and refused
// otherwise — "5 nH" in a capacitance cell is a mistake worth saying, never a number to keep.

using System;
using System.Globalization;
using CircuitRF.Design.Modeling;

namespace CircuitRF.Ui.ModelComposer;

public static class ModelValueFormat
{
    private static readonly (string Prefix, double Scale)[] Prefixes =
        [("T", 1e12), ("G", 1e9), ("M", 1e6), ("k", 1e3), ("", 1), ("m", 1e-3), ("µ", 1e-6), ("u", 1e-6), ("n", 1e-9), ("p", 1e-12), ("f", 1e-15), ("a", 1e-18)];

    /// <summary>The unit an element kind's value is written in.</summary>
    public static string UnitOf(NetworkElementKind kind) => kind switch
    {
        NetworkElementKind.Resistance => "Ω",
        NetworkElementKind.Inductance or NetworkElementKind.Mutual => "H",
        NetworkElementKind.Capacitance => "F",
        _ => "",
    };

    /// <summary>A short general number, invariant culture.</summary>
    public static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);

    /// <summary><paramref name="v"/> in engineering notation with <paramref name="unit"/>: <c>39.5 pH</c>.</summary>
    public static string Eng(double v, string unit)
    {
        if (!double.IsFinite(v)) return "—";
        if (v == 0) return unit.Length == 0 ? "0" : $"0 {unit}";
        double a = Math.Abs(v);
        foreach (var (p, s) in Prefixes)
        {
            if (p == "u") continue;
            if (a >= s * 0.9995)
                return (v / s).ToString("G4", CultureInfo.InvariantCulture) + (p.Length + unit.Length == 0 ? "" : " " + p + unit);
        }
        return v.ToString("G4", CultureInfo.InvariantCulture) + (unit.Length == 0 ? "" : " " + unit);
    }

    /// <summary>
    /// Reads <c>"39.5 pH"</c>, <c>"39.5p"</c>, <c>"3.95e-11"</c> or <c>"5.2 Ohm"</c> against <paramref name="unit"/>. False for an
    /// empty, non-numeric or wrongly-united text.
    /// </summary>
    public static bool TryParse(string? text, string unit, out double value)
    {
        value = 0;
        string s = (text ?? "").Trim().Replace(" ", "", StringComparison.Ordinal);
        if (s.Length == 0) return false;
        int end = 0;
        while (end < s.Length && (char.IsAsciiDigit(s[end]) || s[end] is '.' or '-' or '+'
                                  || ((s[end] is 'e' or 'E') && end + 1 < s.Length && (char.IsAsciiDigit(s[end + 1]) || s[end + 1] is '-' or '+'))))
            end++;
        if (!double.TryParse(s[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return false;
        string rest = s[end..];
        foreach (string u in new[] { unit, "Ohm", "ohm", "Ω" })
            if (u.Length > 0 && rest.EndsWith(u, StringComparison.Ordinal) && (u == unit || unit == "Ω"))
            {
                rest = rest[..^u.Length];
                break;
            }
        double scale = 1;
        if (rest.Length > 0)
        {
            bool found = false;
            foreach (var (p, sc) in Prefixes)
                if (p.Length > 0 && rest == p) { scale = sc; found = true; break; }
            if (!found) return false;
        }
        value = number * scale;
        return double.IsFinite(value);
    }
}
