using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Tests.Composed;

/// <summary>brief-modelcomposer-21 R-mc21-1: the one reader and writer of a composed FET's instance switches.</summary>
public sealed class ComposedSwitchesTests
{
    private static Value NotEvaluated(ParameterAssignment ov) => throw new InvalidOperationException($"evaluated {ov.Name}");

    [Fact]
    public void WriteThenRead_RoundTrips_AndTheDefaultWritesNothing()
    {
        Assert.Empty(ComposedSwitches.Default.Write());

        var s = new ComposedSwitches { LinearC = true, DrainLag = false, SelfHeating = false };
        var written = s.Write();
        Assert.Equal(["LinearC", "DrainLag", "SelfHeating"], written.Select(kv => kv.Key));
        var back = ComposedSwitches.Read(written.Select(kv => new ParameterAssignment(kv.Key, kv.Value)), NotEvaluated);
        Assert.Equal(s, back);
        Assert.Equal("linear C · no drain lag · isothermal", s.Describe());
        Assert.Equal("linear C at the bias point, no drain lag, isothermal", s.Sentence());
        Assert.Equal("no intrinsic charge", (s with { Charge = false, DrainLag = true, SelfHeating = true }).Describe());
    }

    public static TheoryData<string> Spellings =>
        [.. BooleanParameter.TrueSpellings, .. BooleanParameter.FalseSpellings, "OFF", "Yes"];

    [Theory]
    [MemberData(nameof(Spellings))]
    public void EveryBooleanSpelling_IsReadAsWritten(string spelling)
    {
        bool expected = BooleanParameter.TrueSpellings.Contains(spelling, StringComparer.OrdinalIgnoreCase);
        var s = ComposedSwitches.Read([new ParameterAssignment("GateLag", spelling)], NotEvaluated);
        Assert.Equal(expected, s.GateLag);
    }

    [Fact]
    public void AnExpression_IsEvaluated_AndAnUnknownWordIsRefusedByName()
    {
        var s = ComposedSwitches.Read([new ParameterAssignment("GateLag", "gl")], _ => new Value(0.0));
        Assert.False(s.GateLag);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ComposedSwitches.Read([new ParameterAssignment("DrainLag", "maybe")], _ => throw new KeyNotFoundException("Unresolved name 'maybe'")));
        Assert.Contains("DrainLag", ex.Message);
        Assert.Contains("True:", ex.Message);
        Assert.Contains("Unresolved name 'maybe'", ex.Message);
    }

    [Fact]
    public void Annotate_ShowsAnExpressionAsWritten_AndNothingAtTheDefaults()
    {
        var raw = new Dictionary<string, string> { ["LinearC"] = "true", ["GateLag"] = "gl", ["Charge"] = "true" };
        Assert.Equal("linear C · GateLag=gl", ComposedSwitches.Annotate(n => raw.GetValueOrDefault(n)));
        Assert.Equal("", ComposedSwitches.Annotate(n => ComposedSwitches.DefaultOf(n) ? "on" : "off"));
    }
}
