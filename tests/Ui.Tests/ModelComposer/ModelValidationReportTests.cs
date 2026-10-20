using System.Numerics;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-11 R-mc11-6: a PA class's extrinsic loads present harmonicaRF's own intrinsic targets through the
/// model's network and its linearized output capacitance; and a report runs every section it can and writes its folder.
/// </summary>
public sealed class ModelValidationReportTests(ITestOutputHelper output)
{
    [Fact]
    public void ClassLoads_PresentHarmonicaRFsIntrinsicTargets_ThroughTheNetwork()
    {
        string cmc = ExportFixture.Document();
        string path = Path.Combine(Path.GetDirectoryName(cmc)!, "fet.cmodel");
        CmodelExport.Export(cmc, path);
        const double f0 = 2e9, c22 = 0.1e-12, ropt = 40;
        var model = CmodelFile.Read(path);
        int n0 = model.BasePeriphery.Fingers;
        var drain = ComposedClassLoads.DrainTwoPort(ComposedNetworkTemplate.Build(model.Network), n0, n0, 1.0, f0, 5);
        int exact = 0;
        foreach (var cls in (PaClass[])[PaClass.J, PaClass.F, PaClass.FInverse])
        {
            var notes = new List<string>();
            var loads = ComposedClassLoads.ExtrinsicLoads(cls, ropt, f0, c22, drain, notes);
            for (int k = 1; k <= 5; k++)
            {
                // A target the network's own loss puts out of a passive load's reach keeps its reactance, and is named.
                if (notes.Any(n => n.StartsWith($"band {k} ", StringComparison.Ordinal)))
                {
                    Assert.Equal(1e-6, loads[k - 1].Real);
                    continue;
                }
                // Forward: the network terminated by the extrinsic load, seen from the intrinsic drain, beside C22.
                var y = drain[k - 1];
                Complex yd = y.D - y.B * y.C / (y.A + 1 / loads[k - 1]);
                Complex zi = 1 / (yd + new Complex(0, 2 * Math.PI * f0 * k * c22));
                Complex want = PaClassPresets.IntrinsicLoad(cls, k, ropt);
                Assert.True((zi - want).Magnitude <= 1e-9 * want.Magnitude, $"{cls} band {k}: {zi} vs {want}");
                exact++;
            }
        }
        output.WriteLine($"{exact} of 15 bands reachable");
        Assert.True(exact >= 5, "every class's fundamental and open bands are reachable");
    }

    /// <summary>
    /// brief-modelcomposer-23 R-mc23-10 — the transform moved to src/Harmonica, built from the network as an instance
    /// simulates it. At the model's own finger count and m = 1 it is the drain two-port the report built from the raw
    /// network before the move, bit for bit.
    /// </summary>
    [Fact]
    public void TheMovedDrainTwoPort_IsTheReportsOwn_BitForBit()
    {
        string cmc = ExportFixture.Document();
        string path = Path.Combine(Path.GetDirectoryName(cmc)!, "fet.cmodel");
        CmodelExport.Export(cmc, path);
        var model = CmodelFile.Read(path);
        int n0 = model.BasePeriphery.Fingers;
        var moved = ComposedClassLoads.DrainTwoPort(ComposedNetworkTemplate.Build(model.Network), n0, n0, 1.0, 2e9, 5);

        // The report's construction before the move: the raw define block, both gates tied to their sources.
        string net = model.Network.Netlist;
        string name = System.Text.RegularExpressions.Regex.Match(net, @"define\s+(\S+)").Groups[1].Value;
        string cnl = net + $"\n{name}:XN 0 ed 0 is id is\nPort:P1 ed 0 Num=1 Z=50 Ohm\nPort:P2 id is Num=2 Z=50 Ohm\n";
        var (lib, tb) = new CircuitRF.Core.Netlist.CnlReader().Read(cnl);
        using var nl = new CircuitRF.Core.Elaboration.Elaborator(lib).Elaborate(tb);
        var s = CircuitRF.Engine.SParameterEngine.Run(nl, [.. Enumerable.Range(1, 5).Select(k => k * 2e9)])["S"];
        for (int k = 0; k < 5; k++)
        {
            var old = CircuitRF.Engine.Modeling.NetworkAlgebra.SToY(new CircuitRF.Engine.Modeling.C2(
                (Complex)s[k, 0, 0], (Complex)s[k, 0, 1], (Complex)s[k, 1, 0], (Complex)s[k, 1, 1]), 50);
            Assert.Equal(old, moved[k]);
        }
    }

    [Fact]
    public void Report_RunsEverySection_AndWritesItsFolder()
    {
        string cmc = ExportFixture.Document();
        var options = ReportOptions.Default with
        {
            Modes = [PaClass.B, PaClass.F], Rings = 1, Spokes = 6, PinStart = 0, PinStep = 3, PinMax = 30, Compression = 1,
        };
        var report = ModelValidationReport.Generate(cmc, options: options);
        foreach (string line in report.Lines) output.WriteLine(line);

        var names = report.Sections.Select(s => s.Name).ToList();
        Assert.Contains("S", names);
        Assert.Contains("C-V", names);
        foreach (string mode in (string[])["Class B", "Class F"])
        {
            var s = report.Sections.Single(x => x.Name == mode);
            Assert.Equal(7, s.Data["StopCode"].RealValues.Length);
            Assert.Contains("MXP Pout", s.Lines[0]);
            Assert.Equal(5, s.Data["ZLoad.extrinsic"].ComplexValues.Length);
        }

        string folder = Path.Combine(Path.GetDirectoryName(cmc)!, "fet.report");
        var files = ModelValidationReport.Write(report, folder).Select(Path.GetFileName).ToList();
        Assert.Contains("report.npy", files);
        Assert.Contains("summary.txt", files);
        Assert.Contains("Class_F.cnl", files);
    }
}
