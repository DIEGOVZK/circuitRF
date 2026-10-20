using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Cli;
using CircuitRF.Design.Modeling;
using CircuitRF.Ui.Tests.Lvs;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>brief-modelcomposer-3 R-mc3-1/2: the <c>.cmc</c> writes the same bytes it reads, carries later
/// phases' blocks verbatim, and every refusal names its field.</summary>
[Collection(LvsCliConsoleCollection.Name)]
public sealed class CmcFormatTests
{
    internal static CmcDocument Sample() => new()
    {
        Kind = CmcKind.Active,
        BasePeriphery = new BasePeriphery(8, "125 um"),
        DataSets =
        [
            new CmcDataSet
            {
                Name = "pulsed S, Q1", Kind = DataSetKind.PulsedSParams, Sources = ["sp/q1_a.s2p", "sp/q1_b.s2p"],
                Conditions = new DataConditions
                {
                    AmbientC = 25, ReferencePlane = ReferencePlane.Instrument, BiasPathOhms = new BiasPathOhms(1.5, 0.8),
                    PulseWidth = 2e-6, Period = 1e-3, SampleDelay = 1.5e-6, TrapState = TrapStateMode.Auto,
                    Quiescent = new QuiescentBias(-3, 28, Id: 0.05),
                    PrePulse = new PrePulse(-6, 40, 1e-6, 2e-7), SoakTime = 60,
                    Compliance = new ComplianceLimits(0.01, 1.2), Note = "wafer 3",
                },
                Import = new ImportSettings
                {
                    Format = ImportSettings.Touchstone, Pattern = "Vgs={vgs} Vds={vds} Id={id:mA}", PatternSource = "comment",
                },
                Exclusions = [new PointExclusion("sp/q1_b.s2p", "oscillating")],
            },
            new CmcDataSet
            {
                Name = "DC", Kind = DataSetKind.DcIv, Sources = ["iv.csv"],
                Import = new ImportSettings
                {
                    Format = ImportSettings.Csv, Header = true,
                    Columns = [new ColumnMap("Vg", "vgs"), new ColumnMap("Vd", "vds"), new ColumnMap("Id", "id", "mA")],
                },
            },
        ],
        // Current is MC-7's (CurrentFit validates it); Charge is a later phase's, carried opaquely.
        Steps = JsonNode.Parse("""{"Current":{"Settings":{"Id":{"Method":"Table","GridStep":0.05}}},"Charge":{"Method":"ANN","Layers":[8,8]}}"""),
        Panels = new CmcPanels { Hidden = ["Plots", "Data"] },
    };

    [Fact]
    public void ARoundTrip_IsByteIdentical()
    {
        byte[] first = CmcFile.ToBytes(Sample());
        byte[] second = CmcFile.ToBytes(CmcFile.Parse(Encoding.UTF8.GetString(first)));
        Assert.Equal(first, second);
    }

    [Fact]
    public void PadsNetworkAndLaterPhaseBlocks_ArePreserved()
    {
        // Pads and Network are MC-4's (CmcExtrinsic validates them); Steps is carried as written.
        const string pads = """{"Kind":"OpenShort","Open":"pads/open.s2p","Short":"pads/short.s2p"}""";
        const string network = """{"Schematic":"gan-two-shell.csch","Variables":{"Lg":{"Min":0,"Lock":true},"rg":{"Tie":{"To":"rd","Factor":0.5},"Scaling":"perFingerInverse"}},"FlatnessBand":[1000000000,5000000000]}""";
        string json = Encoding.UTF8.GetString(CmcFile.ToBytes(Sample())).TrimEnd().TrimEnd('}')
                    + $",\"Pads\":{pads},\"Network\":{network}}}";

        var back = CmcFile.Parse(Encoding.UTF8.GetString(CmcFile.ToBytes(CmcFile.Parse(json))));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(pads), back.Pads));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(network), back.Network));
        Assert.True(JsonNode.DeepEquals(Sample().Steps, back.Steps));
    }

    [Theory]
    [InlineData("\"AmbientC\"", "\"AmbiantC\"", "DataSets[0].Conditions.AmbiantC")]
    [InlineData("\"Vgs\": -3", "\"Vgs\": \"-3\"", "DataSets[0].Conditions.Quiescent.Vgs")]
    [InlineData("\"Name\": \"DC\"", "\"Name\": \"pulsed S, Q1\"", "DataSets[1].Name")]
    [InlineData("\"GridStep\"", "\"GridStepp\"", "Steps.Current.Settings.Id.GridStepp")]
    public void ARefusal_NamesItsField(string find, string replace, string field)
    {
        string json = Encoding.UTF8.GetString(CmcFile.ToBytes(Sample()));
        Assert.Contains(find, json);
        var ex = Assert.Throws<CmcFormatException>(() => CmcFile.Parse(ReplaceFirst(json, find, replace)));
        Assert.Equal(field, ex.Field);
        Assert.StartsWith(field + ":", ex.Message);
    }

    [Fact]
    public void Check_ReportsTheValidatorsSentence()
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc3-check-").FullName;
        string path = Path.Combine(dir, "bad.cmc");
        File.WriteAllText(path, ReplaceFirst(Encoding.UTF8.GetString(CmcFile.ToBytes(Sample())), "\"AmbientC\"", "\"AmbiantC\""));
        string expected = Assert.Throws<CmcFormatException>(() => CmcFile.Read(path)).Message;

        var (realOut, realErr) = (Console.Out, Console.Error);
        var buffer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(buffer);
            Console.SetError(buffer);
            JsonRun.Reset();
            exit = CliEntry.Run(["check", path]);
        }
        finally { Console.SetOut(realOut); Console.SetError(realErr); }

        Assert.Equal(1, exit);
        Assert.Contains(expected, buffer.ToString());
    }

    private static string ReplaceFirst(string s, string find, string replace)
    {
        int i = s.IndexOf(find, StringComparison.Ordinal);
        return s[..i] + replace + s[(i + find.Length)..];
    }
}
