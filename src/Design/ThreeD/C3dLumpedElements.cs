// brief-sar-3 R-sar3-3 — a .c3d's lumped elements, turned into the neutral problem's Em3dLumpedElement list.
//
// THE GAP IS A PORT'S. A lumped element's rectangle, plane and stated ends are resolved by C3dPorts' own lumped rule — the
// contact inference, the corner inset, the stated-ends override, the not-modelled-conductor refusal — on a port record built
// from it, so the two can never disagree about what a gap is. Only the port's polarity is dropped: an R, L or C has none.
//
// THE VALUES ARE EXPRESSIONS in the document's scope, each in the unit it was typed in (var-unit-wins, expressions.md §8), so
// `C = Ctune` with Ctune a VAR in pF is tuned by re-running, as every other .c3d value is.

using CircuitRF.Core.Expressions;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>One lumped element as a run resolves it: the neutral element, or why there is none.</summary>
public sealed record C3dLumpedResult(C3dLumpedElement Element, Em3dLumpedElement? Resolved, string? Refusal);

public static class C3dLumpedElements
{
    /// <summary>The unit a value is in when its record states none: the base SI unit.</summary>
    public const string OhmUnit = "Ohm", HenryUnit = "H", FaradUnit = "F";

    /// <summary>Every modelled lumped element of <paramref name="doc"/>, resolved against <paramref name="context"/> in
    /// <paramref name="resolution"/>'s scope.</summary>
    public static IReadOnlyList<C3dLumpedResult> Resolve(C3dDocument doc, C3dPortContext context, C3dResolution? resolution)
        => [.. doc.LumpedElements.Where(l => l.Model).Select(l => Resolve(l, doc.DbuPerMicron, context, resolution))];

    /// <summary>One lumped element.</summary>
    public static C3dLumpedResult Resolve(C3dLumpedElement le, int dbuPerMicron, C3dPortContext context, C3dResolution? resolution)
    {
        string who = $"Lumped element '{le.Name}'";
        double? r = null, l = null, c = null;
        foreach (var (key, text, unit, set) in new (string, string?, string, Action<double>)[]
                 {
                     ("R", le.R, le.RUnit ?? OhmUnit, v => r = v), ("L", le.L, le.LUnit ?? HenryUnit, v => l = v),
                     ("C", le.C, le.CUnit ?? FaradUnit, v => c = v),
                 })
        {
            if (text is null) continue;
            if (Value(resolution, text, unit, out string? why) is { } v) set(v);
            else return new(le, null, $"{who}'s {key} '{text}' does not resolve: {why}.");
        }

        var shim = new C3dPort
        {
            Number = 1, Name = le.Name, Kind = Em3dPortKind.Lumped, Plane = le.Plane, Offset = le.Offset, Rect = le.Rect,
            Positive = le.Positive, Negative = le.Negative,
        };
        var port = C3dPorts.Resolve(shim, dbuPerMicron, context);
        if (port.Resolved is not { } gap) return new(le, null, $"{who}: {port.Refusal}");
        var element = new Em3dLumpedElement(le.Name, gap.Min, gap.Max, gap.Direction, r, l, c, le.Topology);
        return element.Inexpressible() is { } cannot ? new(le, null, cannot) : new(le, element, null);
    }

    /// <summary>A value as an element states it — an expression, in <paramref name="unit"/> — in base SI, or null with
    /// <paramref name="why"/>. With no scope (a document that did not resolve) only a plain number is read.</summary>
    public static double? Value(C3dResolution? resolution, string text, string unit, out string? why)
    {
        why = null;
        try
        {
            if (resolution is null)
            {
                if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double plain))
                    return plain * (Units.Scale(unit) ?? 1);
                why = "the document's names did not resolve";
                return null;
            }
            var v = resolution.Evaluate(text, unit);
            if (v.Kind != ValueKind.Real) { why = $"it is {v.Kind.ToString().ToLowerInvariant()}; a lumped value is a real number"; return null; }
            return v.AsReal();
        }
        catch (ExpressionException e) { why = e.Message; return null; }
    }
}
