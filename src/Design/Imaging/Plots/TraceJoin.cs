// Pieces of one curve joined — brief-ptu-5-one-row-per-colour.md R-ptu5-1.
//
// The reader returns a drawn curve in pieces wherever it crosses a grid line, a marker or another curve, and the
// assembly takes one magnitude trace per entry. So the pieces the user assigned alike are joined first, here, after the
// reader and before the assembly: pure, no reader state, no picture. A gap between pieces is left as it is — the
// assembly's own interpolation answers it, as it answers a gap inside one trace.

using System.Globalization;

namespace CircuitRF.Design.Imaging.Plots;

public static class TraceJoin
{
    /// <summary>The pieces as one trace: their points in x order, the longer piece's kept where two overlap in x. One
    /// piece is returned unchanged. The joined trace is the longest piece's — its colour and assignment — named after the
    /// first piece along x, with the coarsest resolution of them all.</summary>
    public static PlotTraceRead Join(IReadOnlyList<PlotTraceRead> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (pieces.Count == 0) throw new ArgumentException("Nothing to join.", nameof(pieces));
        if (pieces.Count == 1) return pieces[0];

        var taken = new List<(double Lo, double Hi)>();
        var samples = new List<PlotSample>();
        foreach (var p in pieces.OrderByDescending(Span).ThenBy(p => p.Number))
        {
            var xs = p.Samples.Where(s => !double.IsNaN(s.X)).ToList();
            if (xs.Count == 0) continue;
            samples.AddRange(xs.Where(s => !taken.Any(t => s.X >= t.Lo && s.X <= t.Hi)));
            taken.Add((xs.Min(s => s.X), xs.Max(s => s.X)));
        }
        var longest = pieces.OrderByDescending(Span).ThenBy(p => p.Number).First();
        var first = pieces.OrderBy(Start).ThenBy(p => p.Number).First();
        return longest with { Number = first.Number, Samples = [.. samples.OrderBy(s => s.X)], Resolution = pieces.Max(p => p.Resolution) };
    }

    /// <summary>The session with every set of rectangular traces on one plot assigned the same entry and quantity joined
    /// into one, and a line for each join (<c>S21 db: T37 + T39 + T42 + T50</c>). A chart trace is never joined: its x is
    /// frequency spread along the curve, not an order the pieces can be put in.</summary>
    public static (PlotSession Session, IReadOnlyList<string> Lines) Session(PlotSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var sets = session.Traces.Where(t => t.Assignment.Assigned && !t.Parametric)
                                 .GroupBy(t => (t.Plot, t.Assignment.I, t.Assignment.J, t.Assignment.Quantity))
                                 .Where(g => g.Count() > 1)
                                 .ToList();
        if (sets.Count == 0) return (session, []);

        var lines = new List<string>();
        var joined = new Dictionary<PlotTraceRead, PlotTraceRead?>(ReferenceEqualityComparer.Instance);
        foreach (var set in sets)
        {
            var pieces = set.OrderBy(Start).ThenBy(t => t.Number).ToList();
            var one = Join(pieces);
            foreach (var p in pieces) joined[p] = null;
            joined[pieces[0]] = one;
            string plot = session.Plots.Count > 1 ? $" (plot {set.Key.Plot.ToString(CultureInfo.InvariantCulture)})" : "";
            lines.Add($"{one.Assignment.Entry} {PlotQuantity.Name(set.Key.Quantity)}{plot}: {string.Join(" + ", pieces.Select(p => p.Name))}");
        }
        var traces = new List<PlotTraceRead>();
        foreach (var t in session.Traces)
        {
            if (!joined.TryGetValue(t, out var into)) traces.Add(t);
            else if (into is not null) traces.Add(into);
        }
        return (session with { Traces = traces }, lines);
    }

    private static double Span(PlotTraceRead t)
    {
        var xs = t.Samples.Where(s => !double.IsNaN(s.X)).Select(s => s.X).ToList();
        return xs.Count == 0 ? 0 : xs.Max() - xs.Min();
    }

    private static double Start(PlotTraceRead t)
    {
        var xs = t.Samples.Where(s => !double.IsNaN(s.X)).Select(s => s.X).ToList();
        return xs.Count == 0 ? double.MaxValue : xs.Min();
    }
}
