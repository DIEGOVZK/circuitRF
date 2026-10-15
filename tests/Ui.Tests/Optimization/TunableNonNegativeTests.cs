using CircuitRF.Design.Optimization;
using Xunit;

namespace CircuitRF.Ui.Tests.Optimization;

/// <summary>
/// Two field defects in one design: a resistor written <c>27k</c> in a column that says Ω was never
/// offered as a tunable, and Widen / Re-centre walked capacitor ranges below zero.
/// </summary>
public sealed class TunableNonNegativeTests
{
    [Theory]
    [InlineData("27k Ohm", 27.0, "kOhm")]      // the prefix joins the unit when that is a unit
    [InlineData("27k Ω", 27.0, "kOhm")]
    [InlineData("47 pF", 47.0, "pF")]          // a prefixed unit is not split
    [InlineData("2k deg", 2000.0, "deg")]      // no "kdeg": the prefix scales the number
    public void APrefixGluedToTheNumber_BeforeASpacedUnit_IsAPlainNumber(string text, double number, string unit)
    {
        Assert.True(TunableValue.TryParse(text, out double n, out string u, out _));
        Assert.Equal((number, unit), (n, u));
    }

    [Fact]
    public void AnRlcRange_IsFlooredAtZero_UnlessItAlreadyReachesBelowIt()
    {
        var c = Make("C1.C", "pF");
        Assert.True(c.IsNonNegative);
        Assert.Equal(0, TunableValue.FloorAtZero(c, currentMin: 50, lo: -100));
        Assert.Equal(-200, TunableValue.FloorAtZero(c, currentMin: -100, lo: -200));   // the user opted in

        var v = Make("V1.Vdc", "V");
        Assert.False(v.IsNonNegative);
        Assert.Equal(-1, TunableValue.FloorAtZero(v, currentMin: 1, lo: -1));
    }

    private static Tunable Make(string key, string unit) => new(
        key, "top", null, 1, key.Split('.')[0], key.Split('.')[1], TunableKind.Parameter,
        $"1 {unit}", 1, unit, false, false, null, null, $"0.5 {unit}", $"2 {unit}", false);
}
