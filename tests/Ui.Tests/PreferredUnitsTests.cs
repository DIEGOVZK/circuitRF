using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-dfr22-1: Settings ▸ General ▸ Units — a preferred length and frequency unit for anything NEW,
/// stored by name, Automatic when unset, and never a change to a stored value. Redirects the per-user
/// state directory, so it runs alone.
/// </summary>
[Collection(UserStateDirectoryCollection.Name)]
public sealed class PreferredUnitsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-pref-units-" + Guid.NewGuid().ToString("N")[..8]);

    public PreferredUnitsTests()
    {
        Directory.CreateDirectory(_root);
        AppDataRoot.RedirectTo(Path.Combine(_root, "state"));
    }

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    [Fact]
    public void Preferences_RoundTripByName_AndAnUnknownNameIsAutomatic()
    {
        Assert.Null(PreferredUnits.Length);
        Assert.Null(PreferredUnits.Frequency);

        AppPreferencesIo.Update(p => { p.PreferredLengthUnit = "Mil"; p.PreferredFrequencyUnit = "MHz"; });
        AppPreferencesIo.InvalidateCache();                      // read back off disk
        string json = File.ReadAllText(Path.Combine(_root, "state", "preferences.json"));
        Assert.Contains("\"preferred_length_unit\": \"Mil\"", json);
        Assert.Contains("\"preferred_frequency_unit\": \"MHz\"", json);
        Assert.Equal(LayoutUnit.Mil, PreferredUnits.Length);
        Assert.Equal("MHz", PreferredUnits.Frequency);

        AppPreferencesIo.Update(p => { p.PreferredLengthUnit = "Inch"; p.PreferredFrequencyUnit = "mhz"; });
        Assert.Null(PreferredUnits.Length);
        Assert.Null(PreferredUnits.Frequency);
    }

    [Theory]
    [InlineData("0",       "0",       "Hz")]
    [InlineData("500",     "500",     "Hz")]
    [InlineData("2.5e3",   "2.5",     "kHz")]
    [InlineData("1e8",     "100",     "MHz")]
    [InlineData("2.45e9",  "2.45",    "GHz")]
    [InlineData("RFfreq",  "RFfreq",  "Hz")]
    public void Automatic_SplitsAsBefore(string hz, string coeff, string unit)
    {
        Assert.Equal((coeff, unit), FreqUnitHelper.Split(hz, null));
        Assert.Equal((coeff, unit), FreqUnitHelper.Split(hz));   // no preference recorded
    }

    [Fact]
    public void WorkspaceCreate_WritesThePreferredUnit_AndWithoutOneCopiesTheShippedBytes()
    {
        string id = WorkspaceCreate.DefaultTechnologyId;
        var plain = WorkspaceCreate.Create(Dir("plain"), "Amp", id);
        Assert.Equal(TechnologyCatalog.LoadRawJson(TechnologyCatalog.Find(id)!), File.ReadAllText(plain.TechPath!));

        var mm = WorkspaceCreate.Create(Dir("mm"), "Amp", id, LayoutUnit.Mm);
        Assert.Equal(LayoutUnit.Mm, TechPersistence.LoadFromFile(mm.TechPath!).DefaultDisplayUnit);
        // The one value, nothing else: the copy is the shipped file with one line changed.
        var diff = File.ReadAllLines(plain.TechPath!).Zip(File.ReadAllLines(mm.TechPath!)).Count(p => p.First != p.Second);
        Assert.Equal(1, diff);
    }

    [Fact]
    public void ANewLayoutAndANewMlin_FollowTheWorkspaceTechnology()
    {
        var ws = WorkspaceCreate.Create(Dir("ws"), "Amp", "mmic-GaAs_2LM_100um", LayoutUnit.Mil);
        var tech = TechPersistence.LoadFromFile(ws.TechPath!);

        var cell = CellCreate.Create(ws.WorkspaceDir, "Line", CellViews.Layout, tech: tech);
        Assert.Equal(LayoutUnit.Mil, LayoutPersistence.LoadFromFile(cell.LayoutPath!).DisplayUnit);

        var mlin = ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 2)
            .Select(d => new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension })
            .ToList();
        MicrostripSubstrateInjection.ApplyTechnologyDefaults(mlin, tech, SymbolKind.Mlin);
        Assert.Equal("mil", mlin.Single(p => p.Name == "W").Unit);
        Assert.Equal("mil", mlin.Single(p => p.Name == "L").Unit);
    }

    [Fact]
    public void GhzPreferred_ShowsAStoredHzStartInGhz_AndAStatedUnitStillWins()
    {
        AppPreferencesIo.Update(p => p.PreferredFrequencyUnit = "GHz");
        var model = new SchematicEditModel();

        var hz = new FrequencySpecViewModel(model, new FrequencySpec("1e8", "6e9", 51));
        Assert.Equal(("0.1", "GHz"), (hz.StartCoeff, hz.StartUnit));

        var stated = new FrequencySpecViewModel(model, new FrequencySpec("100", "6000", 51, startUnit: "MHz", stopUnit: "MHz"));
        Assert.Equal(("100", "MHz"), (stated.StartCoeff, stated.StartUnit));

        AppPreferencesIo.Update(p => p.PreferredFrequencyUnit = "MHz");
        var fresh = new FrequencySpecViewModel(model);            // a new analysis: 1 GHz, in MHz
        Assert.Equal(("1000", "MHz"), (fresh.StartCoeff, fresh.StartUnit));

        var tlin = ComponentTypeRegistry.DefaultParameters(SymbolKind.Tline, 2)
            .Select(d => new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension })
            .ToList();
        PreferredUnits.ApplyToNewFrequencyRows(tlin, PreferredUnits.Frequency);
        var f = tlin.Single(p => p.Name == "F");
        Assert.Equal(("1000", "MHz"), (f.Expression, f.Unit));
    }

    [Fact]
    public void ChangingEitherPreference_LeavesASavedDocumentByteIdenticalOnReSave()
    {
        string dir = Dir("docs");
        string csch = Path.Combine(dir, "a.csch"), clay = Path.Combine(dir, "a.clay");

        var model = new SchematicEditModel();
        var tlin = new EditableComponent { InstanceName = "TL1", Symbol = SymbolKind.Tline };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.Tline, 2))
            tlin.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit, Dimension = d.Dimension });
        model.Components.Add(tlin);
        model.Analyses.Add(new SParameterAnalysis("SP1", new FrequencySpec("1e8", "6e9", 51)));
        SchematicPersistence.SaveToFile(csch, model, "a");
        LayoutPersistence.SaveToFile(clay, new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um });
        byte[] schBefore = File.ReadAllBytes(csch), layBefore = File.ReadAllBytes(clay);

        AppPreferencesIo.Update(p => { p.PreferredLengthUnit = "Mil"; p.PreferredFrequencyUnit = "MHz"; });

        var (loaded, _, name) = SchematicPersistence.LoadFromFile(csch);
        SchematicPersistence.SaveToFile(csch, loaded, name);
        LayoutPersistence.SaveToFile(clay, LayoutPersistence.LoadFromFile(clay));
        Assert.Equal(schBefore, File.ReadAllBytes(csch));
        Assert.Equal(layBefore, File.ReadAllBytes(clay));
    }
}
