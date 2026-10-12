using System.Globalization;
using CircuitRF.Design.Schematic;

namespace CircuitRF.SchematicSynth;

/// <summary>
/// A shown parameter in another editor's spelling (brief-iml3 R-iml3-1, the label stack's value line): named
/// (<c>R=5 kOhm</c>, <c>C = 1 pF</c>), bare (<c>2.62 pF</c>, <c>50Ω</c>), compact engineering (<c>10k</c>, <c>18p</c>,
/// <c>0.01µ</c>) or letter-as-point (<c>4k7</c>, <c>2p2</c>); <c>Ω</c> or <c>Ohm</c>, <c>µ</c> or <c>u</c>; now and then a
/// trailing tuning tag (<c>{t}</c>, <c>{o}</c>). The circuit's own value is never changed — only how it is written.
/// </summary>
public static class ForeignValues
{
    private static readonly (string Prefix, double Scale)[] Prefixes =
        [("f", 1e-15), ("p", 1e-12), ("n", 1e-9), ("u", 1e-6), ("µ", 1e-6), ("m", 1e-3), ("", 1), ("k", 1e3), ("M", 1e6), ("G", 1e9)];

    /// <summary>The word for one shown parameter, and — when the word is a value the grammar cannot read (<c>100</c> for
    /// a resistor) — the value in base SI for the label's <c>params</c>.</summary>
    public static (string Text, string? Si) Format(EditableParameter p, Look look, Random r)
    {
        string unit = Respell(p.Unit, look);
        string expr = p.Expression.Trim();
        bool rlc = p.Name is "R" or "L" or "C";
        double? si = rlc && double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && ScaleOf(p.Unit) is { } sc ? v * sc : null;

        string form = rlc && si is not null ? look.ValueForm : "named";
        string text = form switch
        {
            "bare" => expr + (r.NextDouble() < 0.6 ? " " : "") + unit,
            "spice" => Compact(si!.Value, p.Name, look, letter: false, r),
            "letter" => Compact(si!.Value, p.Name, look, letter: true, r),
            _ => Named(p.Name, expr, unit, look, r),
        };
        if (look.Tags && r.NextDouble() < 0.4) text += r.NextDouble() < 0.6 ? " {t}" : " {o}";
        string? siText = si is not null && !ValueText.Reads(text) ? si.Value.ToString("R", CultureInfo.InvariantCulture) : null;
        return (text, siText);
    }

    private static string Named(string name, string expr, string unit, Look look, Random r)
    {
        string eq = look.Spaced ? " = " : "=";
        string gap = unit.Length == 0 ? "" : r.NextDouble() < 0.85 ? " " : "";
        return name.Length == 0 ? expr + gap + unit : name + eq + expr + gap + unit;
    }

    /// <summary><c>10k</c>, <c>4.7k</c>, <c>18p</c>, <c>0.56n</c>, <c>1µ</c> — or with the letter as the point: <c>4k7</c>,
    /// <c>2p2</c>, <c>2R2</c>. A resistor under 1 kΩ is a bare number or one ending in <c>R</c>.</summary>
    private static string Compact(double si, string name, Look look, bool letter, Random r)
    {
        var options = name == "R"
            ? Prefixes.Where(x => x.Prefix is "m" or "" or "k" or "M")
            : Prefixes.Where(x => x.Prefix is "f" or "p" or "n" or "u" or "m");
        var (prefix, scale) = options.Where(x => si / x.Scale >= 1).DefaultIfEmpty(options.First()).MaxBy(x => x.Scale);
        double m = si / scale;
        string mant = Math.Round(m, 3).ToString("0.###", CultureInfo.InvariantCulture);
        if (prefix == "u") prefix = look.Micro;
        if (name == "R" && prefix == "")
        {
            if (letter && mant.Contains('.')) return mant.Replace(".", "R");
            return r.NextDouble() < 0.5 ? mant : mant + "R";
        }
        if (letter && mant.Contains('.') && mant.Split('.')[1].Length == 1) return mant.Replace(".", prefix);
        string unitSuffix = name == "R" ? "" : r.NextDouble() < 0.3 ? (name == "C" ? "F" : "H") : "";
        return mant + prefix + unitSuffix;
    }

    /// <summary>A circuitRF unit in the pack's spelling: <c>Ω</c>/<c>Ohm</c>/<c>ohm</c>, <c>µ</c>/<c>u</c>.</summary>
    public static string Respell(string unit, Look look)
    {
        if (unit.Length == 0) return unit;
        foreach (string ohm in new[] { "Ohm", "Ω", "ohm" })
            if (unit.EndsWith(ohm, StringComparison.Ordinal))
                return unit[..^ohm.Length] + look.Ohm;
        if ((unit[0] == 'u' || unit[0] == 'µ') && unit.Length > 1 && char.IsLetter(unit[1]))
            return look.Micro + unit[1..];
        return unit;
    }

    /// <summary>The SI scale of a circuitRF unit spelling (<c>pF</c>, <c>kΩ</c>, <c>uH</c>, <c>Ohm</c>), or null when it is
    /// not an R, L or C unit.</summary>
    public static double? ScaleOf(string unit)
    {
        string u = unit.Trim();
        foreach (string ohm in new[] { "Ohm", "Ω", "ohm" })
            if (u.EndsWith(ohm, StringComparison.Ordinal)) return PrefixScale(u[..^ohm.Length]);
        if (u.Length >= 1 && u[^1] is 'F' or 'H') return PrefixScale(u[..^1]);
        return null;
    }

    private static double? PrefixScale(string prefix)
    {
        foreach (var (p, s) in Prefixes) if (p == prefix) return s;
        return null;
    }
}
