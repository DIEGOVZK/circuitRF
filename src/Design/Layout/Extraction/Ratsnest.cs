// THE RATSNEST — brief-dfr22-3-layout-round-trip.md R-dfr22-3-5, and layout-view.md §9 step 4.
//
// A guide, never artwork. R-L5h-3 removed the first ratsnest because it was written as PathShapes on a reserved
// layer — selectable, exportable, fabricatable. This computes LINES and nothing else; the layout editor hands them
// to the canvas overlay, which no exporter draws and nothing saves.
//
// What is drawn: for each net, the pins on it that copper has not yet joined, as a minimum spanning tree over the
// groups copper HAS joined — so a net drawn complete draws nothing, a net half routed draws only the missing hops,
// and a pin with a net name and no partner draws nothing at all.

using System.Linq;

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>One pin as the ratsnest reads it: its net, where it is, and the copper it sits on.</summary>
/// <param name="Net">The net the pin is on. Pins with no net are not handed in.</param>
/// <param name="Group">Which galvanically-joined copper the pin lands on (<see cref="CopperPieces.PieceAt"/>), or a
/// value no other pin shares when it lands on none — two pins in one group are already joined.</param>
public readonly record struct RatsnestPin(string Net, long X, long Y, int Group);

/// <summary>One guide line between two pins that share a net and are not joined by copper.</summary>
public readonly record struct RatsnestLine(string Net, long X0, long Y0, long X1, long Y1);

public static class Ratsnest
{
    /// <summary>
    /// The guide lines for <paramref name="pins"/>: per net, a minimum spanning tree over its copper groups, each
    /// tree edge drawn between the two closest pins of the groups it joins. Deterministic for a given input order.
    /// </summary>
    public static IReadOnlyList<RatsnestLine> Build(IReadOnlyList<RatsnestPin> pins)
    {
        var lines = new List<RatsnestLine>();
        foreach (var net in pins.GroupBy(p => p.Net, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var groups = net.GroupBy(p => p.Group).Select(g => g.ToList()).ToList();
            if (groups.Count < 2) continue;

            // Prim's, over groups: the distance between two groups is that of their closest pin pair.
            int n = groups.Count;
            var inTree = new bool[n];
            var best = new double[n];
            var via = new (RatsnestPin A, RatsnestPin B)[n];
            Array.Fill(best, double.MaxValue);
            inTree[0] = true;
            Relax(0);
            for (int added = 1; added < n; added++)
            {
                int next = -1;
                for (int k = 0; k < n; k++)
                    if (!inTree[k] && (next < 0 || best[k] < best[next])) next = k;
                inTree[next] = true;
                var (a, b) = via[next];
                lines.Add(new RatsnestLine(net.Key, a.X, a.Y, b.X, b.Y));
                Relax(next);
            }

            void Relax(int from)
            {
                for (int k = 0; k < n; k++)
                {
                    if (inTree[k]) continue;
                    foreach (var a in groups[from])
                    foreach (var b in groups[k])
                    {
                        double d = (double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y);
                        if (d < best[k]) { best[k] = d; via[k] = (a, b); }
                    }
                }
            }
        }
        return lines;
    }
}
