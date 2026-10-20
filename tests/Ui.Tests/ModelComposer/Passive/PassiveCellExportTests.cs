using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.Passive;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer.Passive;

/// <summary>
/// brief-modelcomposer-12 §4, R-mc12-7: a fitted π, exported as a cell through CellCreate, placed in a schematic and
/// simulated the way Simulate does (extraction, the .cnl round trip, the elaborator, the S-parameter engine) equals the
/// fitted network to 1e-12; exporting over an existing cell is refused and leaves it as it was; and a family cell
/// evaluates its laws at the geometry an instance binds, warning once outside the fitted range.
/// </summary>
public sealed class PassiveCellExportTests : IDisposable
{
    private static readonly double[] F = SyntheticPassiveData.Log(0.1e9, 10e9, 31);
    private readonly string _lib = Path.Combine(Path.GetTempPath(), "mc12-export-" + Guid.NewGuid().ToString("N")[..8]);

    public PassiveCellExportTests() => Directory.CreateDirectory(_lib);
    public void Dispose() { try { Directory.Delete(_lib, true); } catch (IOException) { } }

    private static readonly Dictionary<string, double> Pi = new() { ["Rs"] = 1.5, ["Ls"] = 1.2e-9, ["Cp1"] = 0.15e-12, ["Cp2"] = 0.25e-12 };

    /// <summary>The cell placed once, its two pins labelled, with <paramref name="overrides"/> on the instance.</summary>
    private (Complex[][,] S, IReadOnlyList<string> Warnings) Place(string cell, params (string Name, string Value)[] overrides)
    {
        var model = new SchematicEditModel { SchematicDirectory = _lib };
        var x1 = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = cell, X = 0, Y = 0 };
        foreach (var (n, v) in overrides) x1.Parameters.Add(new EditableParameter { Name = n, Expression = v });
        model.Components.Add(x1);
        var pins = AutoSymbolGenerator.Generate(cell, 2).Pins.OrderBy(p => p.PortIndex).ToList();
        model.NetLabels.Add(new EditableNetLabel { X = pins[0].LocalX, Y = pins[0].LocalY, Name = "a" });
        model.NetLabels.Add(new EditableNetLabel { X = pins[1].LocalX, Y = pins[1].LocalY, Name = "b" });

        var (lib, tb) = SchematicCircuit.FromSchematic(model, "top", _lib);
        tb.Instances.Add(new Instance("P1", "Port", ["a", "0"], [new ParameterAssignment("Num", "1"), new ParameterAssignment("Z", "50")]));
        tb.Instances.Add(new Instance("P2", "Port", ["b", "0"], [new ParameterAssignment("Num", "2"), new ParameterAssignment("Z", "50")]));
        using var nl = new Elaborator(lib).Elaborate(tb);
        var s = SParameterEngine.Run(nl, F)["S"];
        var result = F.Select((_, k) => new Complex[,] { { (Complex)s[k, 0, 0], (Complex)s[k, 0, 1] }, { (Complex)s[k, 1, 0], (Complex)s[k, 1, 1] } }).ToArray();
        return (result, nl.Warnings.ToList());
    }

    private static double MaxDelta(Complex[][,] a, Complex[][,] b) =>
        a.Zip(b).Max(p => new[] { (0, 0), (0, 1), (1, 0), (1, 1) }.Max(ij => (p.First[ij.Item1, ij.Item2] - p.Second[ij.Item1, ij.Item2]).Magnitude));

    [Fact]
    public void ExportedCell_PlacedAndSimulated_EqualsTheFittedNetwork()
    {
        var block = SyntheticPassiveData.FromTopology("pi", Pi, F);
        var fit = PassiveFit.Fit(block, PassiveTopologies.Find("pi").Load(), starts: Pi.ToDictionary(kv => kv.Key, kv => kv.Value * 1.2), quick: true);

        var export = PassiveCellExport.Export(_lib, "PiModel", "pi", fit.Values);

        Assert.True(File.Exists(export.Cell.SchematicPath) && File.Exists(export.Cell.SymbolPath));
        Assert.Equal(["Rs", "Ls", "Cp1", "Cp2"], export.Parameters.Select(p => p.Name).OrderBy(n => n == "Rs" ? 0 : n == "Ls" ? 1 : n == "Cp1" ? 2 : 3));
        var fitted = PassiveTopologies.Find("pi").Load().S(F, 50, fit.Values);
        var (placed, _) = Place("PiModel");
        Assert.True(MaxDelta(placed, fitted) < 1e-12, $"max |ΔS| {MaxDelta(placed, fitted):E2}");
    }

    [Fact]
    public void ExistingCellName_Refused_AndLeftAsItWas()
    {
        PassiveCellExport.Export(_lib, "Taken", "pi", Pi);
        string ccell = Path.Combine(_lib, "Taken", ".ccell");
        string before = File.ReadAllText(ccell);

        var ex = Assert.Throws<ModelingException>(() => PassiveCellExport.Export(_lib, "Taken", "via", new Dictionary<string, double>
            { ["Rv"] = 0.1, ["Lv"] = 1e-10, ["Cv"] = 1e-13 }));

        Assert.Contains("already exists", ex.Message);
        Assert.Equal(before, File.ReadAllText(ccell));
    }

    [Fact]
    public void FamilyCell_EvaluatesItsLaws_AndWarnsOutsideItsRange()
    {
        double[] turns = [1, 2, 3, 4, 5];
        Dictionary<string, double> Truth(double t) => new() { ["Rs"] = 0.5 + 0.4 * t, ["Ls"] = 0.3e-9 * t * t, ["Cp1"] = 0.05e-12 * t, ["Cp2"] = 0.07e-12 * t };
        var blocks = turns.Select(t => SyntheticPassiveData.FromTopology("pi", Truth(t), F, $"pi_{t}", t)).ToList();
        var family = FamilyFit.Run(blocks, PassiveTopologies.Find("pi"), new FamilySettings { Geometry = "turns" });

        PassiveCellExport.ExportFamily(_lib, "PiFamily", "pi", family);

        var (inside, quiet) = Place("PiFamily", ("turns", "2.5"));
        var expected = PassiveTopologies.Find("pi").Load().S(F, 50, family.ValuesAt(2.5));
        Assert.True(MaxDelta(inside, expected) < 1e-12, $"max |ΔS| {MaxDelta(inside, expected):E2}");
        Assert.DoesNotContain(quiet, w => w.Contains("outside"));

        var (_, warnings) = Place("PiFamily", ("turns", 7.ToString(CultureInfo.InvariantCulture)));
        Assert.Single(warnings, w => w.Contains("turns = 7 lies outside 1 to 5"));
    }
}
