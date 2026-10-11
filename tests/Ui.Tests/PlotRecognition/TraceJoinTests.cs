// brief-ptu-5-one-row-per-colour.md R-ptu5-1: pieces of one curve joined — in x order, the longer piece's points kept
// where two overlap, one piece returned unchanged. Synthetic traces: the join reads only their samples.

using System.Linq;
using System.Numerics;
using CircuitRF.Design.Imaging.Plots;
using Xunit;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class TraceJoinTests
{
    private static PlotTraceRead Piece(int number, double from, double to, double y, double step = 1)
    {
        var samples = Enumerable.Range(0, (int)((to - from) / step) + 1)
                                .Select(k => new PlotSample(from + k * step, y, Complex.Zero, 0, 0)).ToList();
        var curve = new TraceCurve(0, 0xC8463A, TraceStyle.Solid, false, [], 0, [], [], 1);
        return new PlotTraceRead(number, 1, curve, new PlotAssignment(2, 1, PlotQuantityKind.Db, "user"), samples, 0.1 * number, "dB", null);
    }

    [Fact]
    public void ThreeDisjointPieces_JoinInXOrder()
    {
        var joined = TraceJoin.Join([Piece(3, 20, 30, -3), Piece(1, 0, 5, -1), Piece(2, 10, 15, -2)]);
        var xs = joined.Samples.Select(s => s.X).ToList();
        Assert.Equal(xs.OrderBy(x => x), xs);
        Assert.Equal(6 + 6 + 11, xs.Count);
        Assert.Equal(1, joined.Number);                  // named after the first piece along x
        Assert.Equal(-3, joined.Samples[^1].Y);
        Assert.Equal(0.3, joined.Resolution, 12);        // the coarsest resolution of them all
    }

    [Fact]
    public void AnOverlap_KeepsTheLongerPiecesPoints()
    {
        // The long piece at −1 dB over 0..20, a short one at −9 dB over 15..25: over 15..20 only −1 dB survives.
        var joined = TraceJoin.Join([Piece(1, 0, 20, -1), Piece(2, 15, 25, -9)]);
        Assert.All(joined.Samples.Where(s => s.X <= 20), s => Assert.Equal(-1, s.Y));
        Assert.All(joined.Samples.Where(s => s.X > 20), s => Assert.Equal(-9, s.Y));
        Assert.Equal(26, joined.Samples.Count);
    }

    [Fact]
    public void OnePiece_IsReturnedUnchanged()
    {
        var one = Piece(4, 0, 10, -2);
        Assert.Same(one, TraceJoin.Join([one]));
    }
}
