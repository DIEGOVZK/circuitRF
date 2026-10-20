using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using static CircuitRF.Ui.Tests.ModelComposer.SyntheticTrapData;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-10 R-mc10-1, R-mc10-5: trap models are registered by name, and a <c>.cmodel</c> naming one this build
/// does not know is refused by name; <c>Traps: None</c> leaves a model — and the current step's record — byte-identical to
/// one made before traps existed.
/// </summary>
public sealed class TrapRegistryTests
{
    private static byte[] Bytes(ComposedModel m)
    {
        using var s = new MemoryStream();
        CmodelFile.Write(m, s);
        return s.ToArray();
    }

    /// <summary>The shared model with a <c>Traps</c> block spliced into its model.json, read back.</summary>
    private static ComposedModel WithBlock(string traps)
    {
        var root = JsonNode.Parse(CmodelFile.ModelJson(ComposedTestModels.Model()))!.AsObject();
        root["Traps"] = JsonNode.Parse(traps);
        using var s = new MemoryStream();
        using (var zip = new ZipArchive(s, ZipArchiveMode.Create, leaveOpen: true))
        using (var e = zip.CreateEntry(CmodelFile.ModelEntry).Open())
            e.Write(Encoding.UTF8.GetBytes(root.ToJsonString()));
        s.Position = 0;
        return CmodelFile.Read(s);
    }

    [Fact]
    public void AnUnknownTrapModel_IsRefusedByName()
    {
        var ex = Assert.Throws<CmodelFormatException>(() => WithBlock("""{ "Model": "Bogus", "States": ["phi2"] }"""));
        Assert.Equal("Traps.Model", ex.Field);
        Assert.Contains("'Bogus' is not a known trap model", ex.Message);
        Assert.Contains("CaptureEmission", ex.Message);
        Assert.Equal([ModelTraps.None, CaptureEmissionTraps.ModelName, CustomTraps.ModelName], TrapModels.Names.Take(3));
    }

    [Fact]
    public void TrapsNone_IsByteIdenticalToNoTraps()
    {
        var plain = ComposedTestModels.Model();
        var none = WithBlock("""{ "Model": "None" }""");
        Assert.Null(none.Traps);
        Assert.Equal(Bytes(plain), Bytes(none));

        // A trapped model round-trips, its block included.
        var trapped = plain with
        {
            Traps = new ModelTraps
            {
                Model = CaptureEmissionTraps.ModelName, States = ["phi2"], Parameters = [new("TauEmit2", 2e-3)],
                Levels = [new("phi2", [10.0, 20])],
            },
            Core = plain.Core with { Id = Constituent.Equation("0.01*vds+0.001*phi2", ["vgs", "vds", "phi2"]) },
        };
        byte[] first = Bytes(trapped);
        Assert.Equal(first, Bytes(CmodelFile.Read(new MemoryStream(first))));
    }

    [Fact]
    public void TheCurrentStepUnderTrapsNone_RecordsWhatItDidWithoutATrapStep()
    {
        var settings = new CurrentFitSettings
        {
            Id = SyntheticFetData.ThermalIdEquation with
            {
                Expression = ComposedTestModels.IdEq,
                Parameters = SyntheticFetData.ThermalIdEquation.Parameters.Where(p => p.Key != "kT").ToDictionary(),
            },
            Ig = SyntheticFetData.ThermalIgEquation,
        };
        var (doc, data, intrinsic) = Document(null, Dc());
        var (docNone, _, _) = Document(new TrapFitSettings(), Dc());
        Assert.NotNull(docNone.Steps);
        string before = CurrentFit.Run(doc, data, intrinsic, settings).Record.ToJson().ToJsonString();
        string after = CurrentFit.Run(docNone, data, intrinsic, settings).Record.ToJson().ToJsonString();
        Assert.Equal(before, after);
        Assert.DoesNotContain("Traps", after);
    }
}
