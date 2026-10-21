using System.Numerics;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.Passive;
using NumFlat;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer.Passive;

/// <summary>
/// brief-modelcomposer-12 R-mc12-1, R-mc12-6, R-mc12-7 through the document: a family of Touchstone files with the
/// geometry in their names imports with each block's {geometry}, FitFile writes Steps.Passive (which the next read
/// validates), and ExportFile creates the family cell from the recorded laws where Settings.Export says.
/// </summary>
public sealed class PassiveStepFileTests
{
    [Fact]
    public void FamilyFiles_FitIntoTheDocument_AndExportFromIt()
    {
        string root = Directory.CreateTempSubdirectory("crf-mc12-doc-").FullName;
        try
        {
            double[] f = SyntheticPassiveData.Log(0.1e9, 10e9, 21);
            var sources = new List<string>();
            foreach (double t in new[] { 1.0, 2, 3, 4, 5 })
            {
                var v = new Dictionary<string, double> { ["Rv"] = 0.02 * t, ["Lv"] = 0.1e-9 * t, ["Cv"] = 0.04e-12 * t };
                var s = PassiveTopologies.Find("via").Load().S(f, 50, v);
                var mats = s.Select(m => { var a = new Mat<Complex>(2, 2); for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) a[i, j] = m[i, j]; return a; }).ToArray();
                string name = $"via_{t}t.s2p";
                TouchstoneIO.WriteFile(new SNP(f, mats, MatrixType.S, MatrixFormat.RI, new Complex(50, 0)), Path.Combine(root, name), precision: "R");
                sources.Add(name);
            }
            var doc = new CmcDocument
            {
                Kind = CmcKind.Passive,
                DataSets =
                [
                    new CmcDataSet
                    {
                        Name = "vias", Kind = DataSetKind.PassiveSParams, Sources = sources,
                        Import = new ImportSettings { Format = ImportSettings.Touchstone, Pattern = "via_{geometry}t", PatternSource = "name" },
                    },
                ],
            };
            var settings = new PassiveStepSettings
            {
                Topology = "via", Family = new FamilySettings { Geometry = "height" },
                Export = new PassiveExportSettings("ViaFamily", "lib"),
            };
            string cmc = Path.Combine(root, "vias.cmc");
            CmcFile.Write(cmc, PassiveStep.WithSettings(doc, settings));

            var fitted = PassiveStep.FitFile(cmc);

            var back = CmcFile.Read(cmc);
            var record = PassiveStep.Record(back)!;
            Assert.Equal("via", record.Topology);
            Assert.Equal((1.0, 5.0), (record.Family!.Lo, record.Family.Hi));
            Assert.Equal(3.0, record.Family.HeldOutGeometry);
            Assert.True(record.Family.HeldOutMax < 1e-6, $"held-out max |ΔS| {record.Family.HeldOutMax:E2}");
            Assert.Equal(fitted.Family!.ValuesAt(2.5)["Lv"], record.Family.ValuesAt(2.5)["Lv"], 1e-24);

            var export = PassiveStep.ExportFile(cmc);
            Assert.Equal(Path.Combine(root, "lib", "ViaFamily"), export.Cell.CellDir);
            // The geometry is the cell's one parameter; the element values are its schematic's variables, laws of it.
            Assert.Equal(["height"], export.Parameters.Select(p => p.Name));
            var block = CircuitRF.Design.Schematic.SchematicPersistence.LoadFromFile(export.Cell.SchematicPath!).model.Components
                .Single(c => c.Symbol == CircuitRF.Design.Schematic.SymbolKind.Var);
            Assert.Equal(["x", "Rv", "Lv", "Cv"], block.Parameters.Select(p => p.Name).OrderBy(n => n switch { "x" => 0, "Rv" => 1, "Lv" => 2, _ => 3 }));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
