using CircuitRF.Core;
using CircuitRF.Core.Devices.Composed;
using RfCore.Data;
using static CircuitRF.Engine.Tests.Composed.TrapTestModels;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-10 R-mc10-1 (D10, [X10] eq. 1.5): the trap states are inputs of <c>Id</c> and <c>Qd</c> only. A gate
/// current or gate charge that names one is refused; and with MC-8's drift charge the gate charge still depends on them —
/// through the channel current alone, <c>∂Qg/∂phi2 = ∂Qdrift/∂Id · ∂Id/∂phi2</c>.
/// </summary>
public sealed class TrapInputsTests
{
    private static readonly string[] Trapped = ["vgs", "vds", "phi2"];

    [Fact]
    public void TheGateCurrentAndTheGateCharge_TakeNoTrapInput()
    {
        var m = DrainLag();
        var ig = m with { Core = m.Core with { Ig = Constituent.GateEquations("0*phi2", "0", Trapped) } };
        var e1 = Assert.Throws<CmodelFormatException>(() => CmodelFile.Validate(ig));
        Assert.Equal("Core.Constituents.Ig.Inputs", e1.Field);
        Assert.Contains("never of the gate current or charge", e1.Message);

        var qg = m with { Core = m.Core with { Qg = Constituent.Equation("1e-12*vgs+0*phi2", Trapped) } };
        var e2 = Assert.Throws<CmodelFormatException>(() => CmodelFile.Validate(qg));
        Assert.Equal("Core.Constituents.Qg.Inputs", e2.Field);

        // Qd may take one.
        CmodelFile.Validate(m with { Core = m.Core with { Qd = Constituent.Equation("1e-13*vds+1e-15*phi2", Trapped) } });
    }

    // One-input pieces, increasing, and a drift network over (vgd, Id) that depends on Id.
    private static readonly TestNet Piece1 = new([0.0], [0.5], [new double[,] { { 1.0 }, { 0.6 } }, new double[,] { { 0.7, 0.4 } }],
                                                 [[0.2, -0.3], [0.0]], 0.0, 1e-13);
    private static readonly TestNet DriftNet = new([-3.0, 0.02], [0.4, 40.0],
                                                   [new double[,] { { 0.5, 1.0 }, { -0.3, 0.8 } }, new double[,] { { 0.9, 0.6 } }],
                                                   [[0.1, 0.2], [0.0]], 0.0, 2e-13);

    [Fact]
    public void WithTheDriftCharge_TheGateChargeSeesTheTrapStateThroughTheChannelCurrentOnly()
    {
        var arrays = new SortedDictionary<string, DataCube>(StringComparer.Ordinal);
        foreach (string key in (string[])["QGS", "QGD", "QDS"]) foreach (var kv in Piece1.Arrays(key)) arrays[kv.Key] = kv.Value;
        foreach (var kv in DriftNet.Arrays("Qdrift")) arrays[kv.Key] = kv.Value;
        var domain = TrainingDomain.Rectangle(-3, 1, 0, 30) with
        {
            Box = [new("QGS", (-3, 1)), new("QGD", (-31, 1)), new("QDS", (0, 30)), new("Qdrift.vgd", (-31, 1)), new("Qdrift.Id", (0, 0.2))],
        };
        var m = DrainLag();
        m = m with
        {
            Core = m.Core with { Qg = Constituent.Drift(true, domain), Qd = Constituent.Drift(false, domain, 0.3) },
            Arrays = arrays,
        };
        CmodelFile.Validate(m);
        var program = ComposedCoreProgram.Compile(m, "test");
        Assert.Equal(["vgs", "vds", "phi2"], program.Seeds);

        foreach (var (vgs, vds, phi2) in new[] { (-0.5, 5.0, 8.0), (0.0, 2.0, 12.0) })
        {
            Span<double> s = [vgs, vds, phi2];
            double[] gId = new double[3], gQg = new double[3], gQd = new double[3], gP = new double[2];
            double id = program.Channel(s, gId);
            program.Charges(s, gQg, gQd);
            program.Drift!.Piece("Qdrift", [vgs - vds, id], gP);
            double chain = gP[1] * gId[2];
            Assert.True(Math.Abs(gId[2]) > 1e-4, "not vacuous: the drain current depends on the state");
            Assert.True(Math.Abs(gQg[2] - chain) <= 1e-12 * Math.Abs(chain), $"∂Qg/∂phi2 {gQg[2]:R} vs {chain:R}");

            // The core carries it into the state port's column of the gate charge's row.
            var loaded = ComposedModelCache.Load(Core.Tests.Composed.ComposedTestModels.WriteTemp(m));
            var core = new ComposedCoreModel(loaded, loaded.N0, 25);
            var r = core.Evaluate(new PortVoltages([vgs, vds, phi2]));
            Assert.Equal(gQg[2], r.Dc[0, 2], 1e-12 * Math.Abs(chain));
        }
    }
}
