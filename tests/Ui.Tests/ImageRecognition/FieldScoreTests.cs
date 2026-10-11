// brief-imf-1-corpus-and-scorer.md §4 — the scorer's topology: a picture's net names are its own, so two netlists that
// differ only in them are the same circuit; the same parts connected differently are not.

using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

public sealed class FieldScoreTests
{
    private static Core.Design.TestBench Cnl(string text) => new CnlReader().Read(text).Item2;

    private const string Filter = """
        Port:P1 in 0 Num=1 Z=50 Ohm
        R:R1 in mid R=50 Ohm
        C:C1 mid 0 C=1 pF
        L:L1 mid out L=1 nH
        Port:P2 out 0 Num=2 Z=50 Ohm
        """;

    [Fact]
    public void TwoNetlists_DifferingOnlyInNetNames_AreTheSameTopology()
    {
        var renamed = Cnl("""
            Port:P2 b 0 Num=2 Z=50 Ohm
            L:L7 n9 b L=3 nH
            C:C4 n9 0 C=2 pF
            R:R9 a n9 R=10 Ohm
            Port:P1 a 0 Num=1 Z=50 Ohm
            """);
        Assert.True(NetlistShape.SameTopology(Cnl(Filter), renamed));
    }

    [Fact]
    public void ASwappedRAndC_IsNotTheSameTopology()
    {
        var swapped = Cnl("""
            Port:P1 in 0 Num=1 Z=50 Ohm
            C:C1 in mid C=1 pF
            R:R1 mid 0 R=50 Ohm
            L:L1 mid out L=1 nH
            Port:P2 out 0 Num=2 Z=50 Ohm
            """);
        Assert.False(NetlistShape.SameTopology(Cnl(Filter), swapped));
    }
}
