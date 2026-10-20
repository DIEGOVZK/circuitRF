using System.Globalization;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Harmonica.Tests.Support;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// brief-modelcomposer-24 R-mc24-4 — the DCIV family at the DUT's own terminals: a composed DUT's against the DC engine on
/// a bench of its own, a native FET's as the intrinsic family with nothing computed, and each plane computed once however
/// often the plane is toggled.
/// </summary>
public sealed class DcivPlaneTests(ITestOutputHelper output)
{
    private static string N(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

    /// <summary>A small sweep, so the composed family is a handful of DC solves.</summary>
    private static CircuitModel Small(CircuitModel m) => m with
    {
        Settings = m.Settings with
        {
            DcivVgsMin = -1.5, DcivVgsMax = 0.5, DcivVgsSteps = 3,
            DcivVdsMin = 2.0,  DcivVdsMax = 20.0, DcivVdsSteps = 4,
        },
    };

    /// <summary>
    /// Three points of the composed DUT's extrinsic family — two reached by continuation along Vds — equal a cold
    /// <c>NonlinearDcEngine</c> solve of the <c>.cmodel</c> between two supplies, its drain current read through a probe:
    /// the network, the traps and the self-heated junction all at the DC engine's own answer.
    /// </summary>
    [Fact]
    public void ComposedExtrinsicFamily_AtThreePoints_EqualsTheDcEngineOnTheDutBench()
    {
        var ctx = HarmonicaContext.Create(Small(ComposedFixtures.Model()));
        var key = DcivFamily.ResolvedKey(ctx.Model, LoadlinePlane.Extrinsic);
        var family = DcivFamily.Compute(ctx, key);
        var intrinsic = DcivFamily.Compute(ctx, key with { Plane = LoadlinePlane.Intrinsic });

        (double Vgs, double Vds, double Id, double Intr)[] points =
        [
            (family[0].Vgs, family[0].Vds[3], family[0].Ids[3], intrinsic[0].Ids[3]),
            (family[1].Vgs, family[1].Vds[0], family[1].Ids[0], intrinsic[1].Ids[0]),
            (family[2].Vgs, family[2].Vds[2], family[2].Ids[2], intrinsic[2].Ids[2]),
        ];
        foreach (var (vgs, vds, id, intr) in points)
        {
            string bench = $"""
                Vdc:VG g 0 Vdc={N(vgs)}
                Vdc:VD dd 0 Vdc={N(vds)}
                IProbe:IX dd d
                ComposedFET:X g d 0 File="{ComposedFixtures.ExampleModel}"
                """;
            var (lib, tb) = new CnlReader().Read(bench);
            using var nl = new Elaborator(lib).Elaborate(tb);
            var dc = NonlinearDcEngine.Run(nl, new AnalysisSettings { NonlinearAbsTol = 1e-13 });
            Assert.True(dc.Converged, $"the DC bench at ({vgs}, {vds}) did not converge");
            double want = dc.ProbeCurrents["IX"];

            double rel = Math.Abs(id - want) / Math.Abs(want);
            output.WriteLine($"Vgs {vgs:F2} Vds {vds:F1}: extrinsic {id:G10} A, DC engine {want:G10} A, rel {rel:E2}; " +
                             $"intrinsic {intr:G10} A");
            Assert.True(rel <= 1e-9, $"Id at ({vgs}, {vds}): {rel:E3}");
        }

        // The network is between the planes: Rd and Rs drop part of Vds, so the terminal family is not the core's.
        Assert.True(Math.Abs(points[0].Id - points[0].Intr) > 1e-4 * Math.Abs(points[0].Intr));
    }

    /// <summary>A native FET's terminals are its device's ports at DC: the extrinsic family is the intrinsic one, the
    /// same object, with nothing computed for it.</summary>
    [Fact]
    public void NativeFetExtrinsicFamily_IsTheIntrinsicOne_WithNoExtraComputation()
    {
        var ctx = HarmonicaContext.Create(new CircuitModel
        {
            Dut = new DutSpec { Kind = DutKind.NativeFet, TypeName = "FET_Angelov" },
        });
        Assert.True(DcivFamily.PlanesCoincideAtDc(ctx.Model.Dut));

        var cache = new DcivFamilyCache();
        var intrinsic = cache.Get(ctx, LoadlinePlane.Intrinsic);
        var extrinsic = cache.Get(ctx, LoadlinePlane.Extrinsic);

        Assert.Same(intrinsic, extrinsic);
        Assert.Equal(1, cache.ComputeCountFor(LoadlinePlane.Intrinsic));
        Assert.Equal(0, cache.ComputeCountFor(LoadlinePlane.Extrinsic));
        Assert.Equal(1, cache.ComputeCount);
    }

    /// <summary>R-mc24-5 — the plane round-trips through a <c>.charm</c>; a document written without it, or left at the
    /// default, opens intrinsic and carries no appearance block for it.</summary>
    [Fact]
    public void ThePlane_RoundTripsThroughTheCharm_AndADocumentWithoutItOpensIntrinsic()
    {
        var model = new CircuitModel { Dut = new DutSpec { Kind = DutKind.NativeFet, TypeName = "FET_Angelov" } };
        var terms = new TerminationSet(model.Settings.HarmonicCount);

        var extrinsic = CharmAppearance.Default with
        {
            LoadlinePlane = CharmAppearance.LoadlinePlaneValue(LoadlinePlane.Extrinsic),
        };
        string json = CharmIo.Write(model, terms, extrinsic);
        Assert.Contains("\"LoadlinePlane\": \"extrinsic\"", json, StringComparison.Ordinal);
        Assert.Equal(LoadlinePlane.Extrinsic, CharmIo.ReadAll(json, null).Appearance.LoadlinePlaneOf);

        // Toggled back: the default is stored as nothing, so the file is the one an untouched document writes.
        var back = extrinsic with { LoadlinePlane = CharmAppearance.LoadlinePlaneValue(LoadlinePlane.Intrinsic) };
        Assert.True(back.IsDefault);
        Assert.Equal(CharmIo.Write(model, terms), CharmIo.Write(model, terms, back));

        // A document from before this brief has no such field, and opens intrinsic.
        string older = CharmIo.Write(model, terms);
        Assert.DoesNotContain("LoadlinePlane", older, StringComparison.Ordinal);
        Assert.Equal(LoadlinePlane.Intrinsic, CharmIo.ReadAll(older, null).Appearance.LoadlinePlaneOf);
    }

    /// <summary>Toggling the plane twice computes each family once: the planes are separate tier-C entries, and asking
    /// for one does not evict the other.</summary>
    [Fact]
    public void TogglingThePlaneTwice_ComputesEachFamilyOnce()
    {
        var ctx = HarmonicaContext.Create(Small(ComposedFixtures.Model()));
        Assert.False(DcivFamily.PlanesCoincideAtDc(ctx.Model.Dut));

        var cache = new DcivFamilyCache();
        var first = cache.Get(ctx, LoadlinePlane.Intrinsic);
        var ext   = cache.Get(ctx, LoadlinePlane.Extrinsic);
        Assert.Same(first, cache.Get(ctx, LoadlinePlane.Intrinsic));
        Assert.Same(ext,   cache.Get(ctx, LoadlinePlane.Extrinsic));

        Assert.NotSame(first, ext);
        Assert.Equal(1, cache.ComputeCountFor(LoadlinePlane.Intrinsic));
        Assert.Equal(1, cache.ComputeCountFor(LoadlinePlane.Extrinsic));
    }
}
