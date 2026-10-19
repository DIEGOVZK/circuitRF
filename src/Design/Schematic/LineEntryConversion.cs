using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Text;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// The Electrical / Physical entry switch for MLIN, CPWG and SLIN (designer feedback round 21), in the style of
/// TLIN's (<see cref="TlinEntryConversion"/>) and MKLOPF's: one button, converting rather than resetting. The
/// electrical form states the line's length as <c>E</c> at <c>F</c> in place of <c>L</c>; the models take either
/// (<c>ComponentModelFactory</c>, L authoritative when both are present) and the conversion is the model's own,
/// through its dispersive εeff at F (<see cref="PlanarLineAngle"/>, <see cref="LineTypeSwap.ModelOf"/>), so a
/// switch never changes the line a run simulates.
///
/// <para>Everything that draws a line reads its PHYSICAL length — Schematic → Layout asks
/// <see cref="LengthMeters(SymbolKind, IReadOnlyDictionary{string, double}, string?, string?, Technology?)"/>.</para>
/// </summary>
public static class LineEntryConversion
{
    /// <summary>The line types the switch is for.</summary>
    public static bool Applies(SymbolKind kind) => kind is SymbolKind.Mlin or SymbolKind.Cpwg or SymbolKind.Slin;

    /// <summary>True when the line states E (at F) and no L.</summary>
    public static bool IsElectrical(IEnumerable<EditableParameter> parameters)
    {
        bool e = false;
        foreach (var p in parameters)
        {
            if (p.Name == "L") return false;
            if (p.Name == "E") e = true;
        }
        return e;
    }

    /// <summary>
    /// The frequency a schematic states its electrical lengths at — the one most of its TLINs and electrically
    /// stated lines name, so a line switched on a design drawn at its operating frequency reads at that frequency;
    /// else the bench's top S-parameter frequency (<see cref="LineTypeSwap.BenchTopFrequencyHz"/>); else 0.
    /// </summary>
    public static double ReferenceFrequencyHz(SchematicEditModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var stated = new List<double>();
        foreach (var c in model.Components)
        {
            bool angleForm = c.Symbol == SymbolKind.Tline ? !TlinEntryConversion.IsPhysical(c.Parameters)
                           : Applies(c.Symbol) && IsElectrical(c.Parameters);
            if (angleForm && Si(c.Parameters.FirstOrDefault(p => p.Name == "F")) is { } f && f > 0) stated.Add(f);
        }
        if (stated.Count > 0)
            return stated.GroupBy(f => f).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        return LineTypeSwap.BenchTopFrequencyHz(model);
    }

    /// <summary>
    /// <paramref name="component"/>'s parameters in the other form, and what the switch has to say; null
    /// parameters with the reason when it cannot convert (W, G, L or E not a plain number). Physical → electrical
    /// is made at <paramref name="frequencyHz"/> (1 GHz when that is 0, and the note says so); electrical →
    /// physical at the line's own F.
    /// </summary>
    public static (List<EditableParameter>? Parameters, string Note) Switch(
        EditableComponent component, Technology? tech, double frequencyHz)
    {
        ArgumentNullException.ThrowIfNull(component);
        string name = component.InstanceName;
        var result = component.Parameters.Select(p => p.Clone()).ToList();
        var notes = new List<string>();

        if (!IsElectrical(result))
        {
            var lRow = result.FirstOrDefault(p => p.Name == "L");
            if (Si(lRow) is not { } lM)
                return (null, lRow is null ? $"'{name}' has no L." : $"'{name}': L is not a plain number, so it has no electrical length until run time. Enter a number in L.");
            double f = frequencyHz;
            if (!(f > 0))
            {
                f = 1e9;
                notes.Add("Nothing on this schematic states a frequency, so E is the line's electrical length at 1 GHz.");
            }
            if (LineTypeSwap.ModelOf(component, tech, f).Model is not { } at)
                return (null, LineTypeSwap.ModelOf(component, tech, f).Refusal ?? $"'{name}' could not be modelled.");

            double eDeg = PlanarLineAngle.AngleRad(at.Line.Eeff, lM, f) * 180.0 / Math.PI;
            int i = result.IndexOf(lRow!);
            result.RemoveAt(i);
            result.Insert(i, Row("E", Fmt(eDeg), "deg", UnitDimension.Angle, true));
            result.RemoveAll(p => p.Name == "F");
            result.Insert(i + 1, Row("F", Fmt(f / 1e9), "GHz", UnitDimension.Frequency, true));
        }
        else
        {
            var eRow = result.First(p => p.Name == "E");
            if (Si(eRow) is not { } eRad)
                return (null, $"'{name}': E is not a plain number, so it has no physical length until run time. Enter a number in E.");
            double f = Si(result.FirstOrDefault(p => p.Name == "F")) ?? 1e9;
            if (!(f > 0))
                return (null, $"'{name}': F is not a positive frequency, so E has no length. Enter a frequency in F.");
            if (LineTypeSwap.ModelOf(component, tech, f).Model is not { } at)
                return (null, LineTypeSwap.ModelOf(component, tech, f).Refusal ?? $"'{name}' could not be modelled.");

            double lM = PlanarLineAngle.LengthMeters(at.Line.Eeff, eRad, f);
            string unit = result.FirstOrDefault(p => p.Name == "W")?.Unit is { Length: > 0 } wu ? wu : MicrostripSubstrateInjection.LengthUnitFor(tech);
            int i = result.IndexOf(eRow);
            result.RemoveAll(p => p.Name is "E" or "F");
            result.Insert(Math.Min(i, result.Count), Row("L", Fmt(lM / (Units.Scale(UnitNormalizer.ToEngineUnit(unit)) ?? 1e-3)), unit, UnitDimension.Length, true));
        }
        return (result, string.Join(" ", notes));
    }

    /// <summary>
    /// The <c>E</c> (degrees, at the line's own <c>F</c>) that <paramref name="lengthMeters"/> of an electrically stated
    /// <paramref name="component"/> is on <paramref name="tech"/> — what a writer arriving with a physical length (the
    /// layout's, on Update Schematic from Layout) puts in E, so the line keeps the form its user chose and never carries
    /// an L beside a stale E. Null when the line does not state E, or F, W or G is not a plain number.
    /// </summary>
    public static string? AngleDegreesFor(EditableComponent component, Technology? tech, double lengthMeters)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!Applies(component.Symbol) || !IsElectrical(component.Parameters)) return null;
        double f = Si(component.Parameters.FirstOrDefault(p => p.Name == "F")) ?? 1e9;
        if (!(f > 0) || LineTypeSwap.ModelOf(component, tech, f).Model is not { } at) return null;
        return Fmt(PlanarLineAngle.AngleRad(at.Line.Eeff, lengthMeters, f) * 180.0 / Math.PI);
    }

    /// <summary>
    /// The physical length, in metres, of a line whose RESOLVED parameters (SI; E in radians, F in Hz) state it
    /// electrically, on <paramref name="tech"/> with the instance's layer choices — the length Schematic → Layout
    /// draws. Null when the parameters state L (read it), or no E, or the model cannot be built.
    /// </summary>
    public static double? LengthMeters(SymbolKind kind, IReadOnlyDictionary<string, double> resolved,
        string? signalLayer, string? groundReference, Technology? tech)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (!Applies(kind) || resolved.ContainsKey("L") || !resolved.TryGetValue("E", out double eRad)
            || !resolved.TryGetValue("W", out double wM)) return null;
        double f = resolved.TryGetValue("F", out double fr) && fr > 0 ? fr : 1e9;
        var probe = new EditableComponent { Symbol = kind, InstanceName = "line" };
        probe.Parameters.Add(Row("W", wM.ToString("R", CultureInfo.InvariantCulture), "", UnitDimension.Length, true));
        if (resolved.TryGetValue("G", out double gM))
            probe.Parameters.Add(Row("G", gM.ToString("R", CultureInfo.InvariantCulture), "", UnitDimension.Length, true));
        if (signalLayer is { Length: > 0 }) probe.Parameters.Add(Row("SignalLayer", signalLayer, "", UnitDimension.None, false));
        if (groundReference is { Length: > 0 }) probe.Parameters.Add(Row("GroundReference", groundReference, "", UnitDimension.None, false));
        return LineTypeSwap.ModelOf(probe, tech, f).Model is { } at ? PlanarLineAngle.LengthMeters(at.Line.Eeff, eRad, f) : null;
    }

    private static double? Si(EditableParameter? p)
    {
        if (p is null || !NumericText.TryParseDouble(p.Expression.Trim(), out double raw)) return null;
        if (p.Unit.Length == 0) return raw;
        return Units.Scale(UnitNormalizer.ToEngineUnit(p.Unit)) is { } s ? raw * s : null;
    }

    private static EditableParameter Row(string name, string expr, string unit, UnitDimension dim, bool show)
        => new() { Name = name, Expression = expr, Unit = unit, ShowOnSchematic = show, Dimension = dim };

    private static string Fmt(double v) => Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
