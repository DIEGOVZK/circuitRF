using System.Globalization;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Design.Modeling;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// The <c>synthetic-gan</c> document built from <see cref="SyntheticGan"/>'s raw files with no hand edits — every step
/// through the functions the Model Composer window and <c>circuitrf model</c> call: New on the stock GaN network at
/// N0 = 4 × 125 µm, the open/short pads, one import per data set with its conditions, then the settings a user chooses
/// (which network values to fit, self-heating from the pulsed sets, drain lag), and Fit all.
/// </summary>
public static class SyntheticGanDocument
{
    public const string Name = "gan_4x125.cmc";

    private static readonly ColumnMap[] Columns = [new("Vgs", "vgs", "V"), new("Vds", "vds", "V"), new("Ig", "ig", "A"), new("Id", "id", "A")];

    /// <summary>Creates <see cref="Name"/> in <paramref name="dir"/>, which holds the raw files, and imports every set.</summary>
    public static string Create(string dir)
    {
        string cmc = Path.Combine(dir, Name);
        ModelCommands.New(cmc, CmcKind.Active, "gan-two-shell", new BasePeriphery(SyntheticGan.N0, "125 um"));
        var doc = CmcFile.Read(cmc);
        doc = CmcExtrinsic.With(doc, new PadsSettings { Kind = PadsKind.OpenShort, Open = "pads/open.s2p", Short = "pads/short.s2p" });
        CmcFile.Write(cmc, doc);

        ModelCommands.Import(cmc, new CmcDataSet
        {
            Name = "dc_25C", Kind = DataSetKind.DcIv, Sources = ["dc/dc_25C.csv"],
            Import = new ImportSettings { Format = ImportSettings.Csv, Columns = Columns },
            Conditions = new DataConditions { ReferencePlane = ReferencePlane.Probe, AmbientC = 25, Note = SyntheticGan.Note },
        });
        foreach (var (name, t, vq, dp) in SyntheticGan.PulsedSets)
            ModelCommands.Import(cmc, new CmcDataSet
            {
                Name = name, Kind = DataSetKind.PulsedIv, Sources = [$"pulsed/{name}.csv"],
                Import = new ImportSettings { Format = ImportSettings.Csv, Columns = Columns },
                Conditions = new DataConditions
                {
                    ReferencePlane = ReferencePlane.Probe, AmbientC = t,
                    Quiescent = new QuiescentBias(SyntheticGan.QuiescentVgs, vq, 0, 0),
                    PrePulse = dp ? new PrePulse(SyntheticGan.QuiescentVgs, SyntheticGan.PreVds, SyntheticGan.PreWidth, SyntheticGan.PreGap, 0) : null,
                    PulseWidth = SyntheticGan.PulseWidth, Period = SyntheticGan.Period, SampleDelay = SyntheticGan.SampleDelay,
                    // From a 0 V quiescent drain every pulse captures, and these sets carry the chuck temperatures: their
                    // points are taken at the state the pulse captures to.
                    TrapState = vq == 0 && !dp ? TrapStateMode.Extreme : null,
                    Note = SyntheticGan.Note,
                },
            });
        foreach (double t in SyntheticGan.STemperatures)
        {
            string folder = $"s_{t.ToString(CultureInfo.InvariantCulture)}C";
            ModelCommands.Import(cmc, new CmcDataSet
            {
                Name = folder, Kind = DataSetKind.SParams, Sources = ModelCommands.Sources(cmc, [folder]),
                Import = new ImportSettings { Format = ImportSettings.Touchstone, Pattern = "Vgs{vgs}_Vds{vds}_Id{id:mA}", PatternSource = "name" },
                Conditions = new DataConditions { ReferencePlane = ReferencePlane.Probe, AmbientC = t, Note = SyntheticGan.Note },
            });
        }
        return cmc;
    }

    /// <summary>
    /// The settings a user chooses before Fit all: the three inductances free (the rest held at the stock values), self-
    /// heating extracted from the 0 V-quiescent pulses at both chuck temperatures with the truth's τth typed, and drain lag
    /// at the CaptureEmission defaults. <paramref name="quick"/> trims the ANN iterations and restarts for a test run.
    /// </summary>
    public static void Configure(string cmc, bool quick = false)
    {
        var doc = CmcFile.Read(cmc);
        var n = CmcExtrinsic.Network(doc);
        string[] free = ["Lg", "Ld", "Ls"];
        doc = CmcExtrinsic.With(doc, n with
        {
            Variables = n.Variables.ToDictionary(kv => kv.Key, kv => kv.Value with { Lock = !free.Contains(kv.Key) }, StringComparer.Ordinal),
            Fit = NetworkFitSettings.Default with { Method = NetworkFitMethod.MultiBias },
        });
        doc = ThermalFit.WithSettings(doc, ThermalFitSettings.Default with { SelfHeating = true, Rth = SyntheticGan.Rth, TauTh = SyntheticGan.TauTh });
        doc = TrapFit.WithSettings(doc, TrapFitSettings.Default with { Model = CaptureEmissionTraps.ModelName, States = [CaptureEmissionTraps.DrainLag] });
        // The gate current spans ten decades and is fitted as asinh(Ig/I0), I0 = IgFloor·max|Ig|. At the default 1e-4 the
        // network's ordinary ripple is microamps of gate current at pinch-off, and its slope a few tens of ohms across the
        // gate; at 1e-7 the same ripple is nanoamps.
        // Id has four inputs here (vgs, vds, T, phi2); the default 2 × 8 network left the small-signal gain 0.1–0.3 dB
        // off the truth, 2 × 16 within 0.1 dB.
        var cs = CurrentFit.Settings(doc);
        doc = CurrentFit.WithSettings(doc, cs with { IgFloor = 1e-7, Id = cs.Id with { Hidden = [16, 16] } });
        if (quick)
        {
            var c = CurrentFit.Settings(doc);
            doc = CurrentFit.WithSettings(doc, c with { Restarts = 1 });
            var q = ChargeFit.Settings(doc);
            doc = ChargeFit.WithSettings(doc, q with { Restarts = 1 });
        }
        CmcFile.Write(cmc, doc);
    }
}
