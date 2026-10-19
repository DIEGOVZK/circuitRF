// ================================================================
//  CylindricalGridSarTests.cs — brief-sar-8b's pieces on their own, with no reference data and no openEMS process: a sheet and a
//  lumped gap on a cylinder through the .c3d, the cylindrical grid and the writer; the lowerings that cannot state one; a
//  placement's turn; MultiGrid's line counts; B1's turn from (ρ, α) onto the world's axes; and the Sar group on that grid.
//  The birdcage itself is BirdcageReplicationTests.
// ================================================================

using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Sar;

public sealed class CylindricalGridSarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar8b-cyl-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const long Mm = 1_000_000;

    /// <summary>A small coil on the cylinder r = 20 mm about z: two strips of one rung, 20° wide, with a 2 mm gap between them that is a
    /// 50 Ω lumped port 10° wide, and a lossy block inside; <paramref name="openEms"/> is the setup's openEMS section.</summary>
    private (C3dDocument Doc, string Path) Workspace(string solver, string? openEms)
    {
        Directory.CreateDirectory(Path.Combine(_root, "Coil", "3d"));
        File.WriteAllText(Path.Combine(_root, ".cws"), "{ \"DefaultTechRef\": \"tech.ctech\" }");
        File.WriteAllText(Path.Combine(_root, "Coil", ".ccell"), "{}");
        File.WriteAllText(Path.Combine(_root, "tech.ctech"),
            "{ \"FormatVersion\": 1, \"Name\": \"t\", \"Materials\": [ { \"Name\": \"PEC\", \"Sigma20\": 1e30 }, " +
            "{ \"Name\": \"Gel\", \"Epsr\": 60, \"Sigma20\": 0.5, \"DensityKgM3\": 1000 } ] }");
        string Strip(string name, double z0, double z1) =>
            $"{{ \"$type\": \"CylinderSheet\", \"Name\": \"{name}\", \"Material\": \"PEC\", \"Axis\": \"Z\", \"Base\": [0, 0, {(long)(z0 * Mm)}], " +
            $"\"Radius\": {20 * Mm}, \"Length\": {(long)((z1 - z0) * Mm)}, \"StartDeg\": -10, \"SweepDeg\": 20 }}";
        string json =
            "{ \"FormatVersion\": 1, \"DbuPerMicron\": 1000, \"DisplayUnit\": \"Mm\", \"SnapDbu\": 1000, \"Objects\": [ " +
            Strip("lower", -10, -1) + ", " + Strip("upper", 1, 10) + ", " +
            $"{{ \"$type\": \"Box\", \"Name\": \"gel\", \"Material\": \"Gel\", \"Role\": \"Dielectric\", \"Min\": [{-8 * Mm}, {-8 * Mm}, {-8 * Mm}], \"Size\": [{16 * Mm}, {16 * Mm}, {16 * Mm}] }} ], " +
            "\"Ports\": [ { \"Number\": 1, \"Name\": \"feed\", \"Kind\": \"Lumped\", \"Z0\": \"50\", " +
            $"\"Cylinder\": {{ \"Axis\": \"Z\", \"Base\": [0, 0, {-1 * Mm}], \"Radius\": {20 * Mm}, \"Length\": {2 * Mm}, \"StartDeg\": -5, \"SweepDeg\": 10 }} }} ], " +
            "\"Setups\": [ { \"FormatVersion\": 1, \"Name\": \"S\", " +
            "\"Frequency\": { \"StartExpr\": \"0.5\", \"StopExpr\": \"3\", \"StepExpr\": \"\", \"NumPoints\": 11, \"Mode\": \"PointCount\", \"Kind\": \"Linear\", " +
            "\"StartUnit\": \"GHz\", \"StopUnit\": \"GHz\", \"StepUnit\": \"GHz\" }, " +
            $"\"Solver3D\": \"{solver}\", " +
            "\"AirBox\": { \"XMin\": { \"PaddingUm\": 30000, \"Boundary\": \"Pec\" }, \"XMax\": { \"PaddingUm\": 30000, \"Boundary\": \"Pec\" }, " +
            "\"YMin\": { \"PaddingUm\": 30000, \"Boundary\": \"Pec\" }, \"YMax\": { \"PaddingUm\": 30000, \"Boundary\": \"Pec\" }, " +
            "\"ZMin\": { \"PaddingUm\": 10000, \"Boundary\": \"Absorbing\" }, \"ZMax\": { \"PaddingUm\": 10000, \"Boundary\": \"Absorbing\" } }" +
            (openEms is null ? "" : ", \"OpenEms\": " + openEms) + " } ] }";
        string path = Path.Combine(_root, "Coil", "3d", "Coil.c3d");
        File.WriteAllText(path, json);
        return (C3dPersistence.LoadFromFile(path), path);
    }

    private (Em3dGenerationResult G, EmSetup Setup) Assemble(C3dDocument doc, string path)
    {
        var (embedded, why) = C3dSetups.Select(doc, "S");
        Assert.True(embedded is not null, why);
        var setup = C3dSetups.ForRun(embedded, path);
        return (C3dProblemAssembly.Assemble(setup, doc, path, Path.Combine(_root, ".cws"), new C3dElaborator()), setup);
    }

    /// <summary>
    /// R-sar8b-1/-2: on circuitRF's own cylindrical grid, a port drawn as a patch of the cylinder finds its two strips by its arcs, as a
    /// planar port finds its conductors by its edges; the grid puts a ρ line on the cylinder and z lines at the gap's and the strips'
    /// ends; the writer gives each strip one box of zero radial extent and the port openEMS's lumped port along z, its radius and its
    /// two azimuth edges on the grid's own lines — and says the azimuth edges were snapped onto uniform lines.
    /// </summary>
    [Fact]
    public void AGapOnTheCylinder_IsALumpedPortInRhoAlphaZ_OnTheGridsOwnLines()
    {
        var (doc, path) = Workspace("OpenEms", "{ \"Grid\": \"Cylindrical\", \"Axis\": \"Z\", \"SaveFieldsGHz\": [] }");
        var (g, setup) = Assemble(doc, path);
        Assert.True(g.Ok, g.Refusal);
        var port = Assert.Single(g.Problem!.Ports);
        Assert.Equal(Em3dCylinderGapAxis.Axial, port.Cylinder!.Along);
        Assert.Equal(new[] { "lower", "upper" }, new[] { port.NegativeObject, port.PositiveObject }.Order());

        var gs = CemOpenEms.ResolveGrid(setup.OpenEms, path);
        var grid = FdtdGrid.Build(g.Problem, gs, long.MaxValue);
        Assert.Null(grid.Refusal);
        Assert.True(grid.X.Lines.Any(r => Math.Abs(r - 0.020) < 1e-12), string.Join(", ", grid.X.Lines.Select(r => (r * 1e3).ToString("G8"))));
        foreach (double z in new[] { -0.010, -0.001, 0.001, 0.010 }) Assert.Contains(grid.Z.Lines, l => Math.Abs(l - z) < 1e-12);
        Assert.Contains(grid.Warnings, w => w.Contains("snaps each to the nearest azimuth line", StringComparison.Ordinal));

        var low = CsxcadWriter.Write(g.Problem, grid, gs, CemOpenEms.ResolveRun(setup.OpenEms));
        Assert.True(low.Ok, low.Refusal);
        var xml = XDocument.Parse(low.PortFiles[0]);
        // the gap crossed 0°, so the grid's circle starts where no gap is, and each strip is one box
        Assert.NotEqual(0, grid.Y.Lines[0]);
        foreach (var metal in xml.Descendants("Metal"))
        {
            var box = BirdcageReplication.Boxes(metal).Single();
            Assert.Equal(0.020, box.Lo[0], 12);
            Assert.Equal(0.020, box.Hi[0], 12);
        }
        var resist = xml.Descendants("LumpedElement").Single(e => (string?)e.Attribute("Name") == "port1_resist");
        Assert.Equal("2", (string?)resist.Attribute("Direction"));
        var (lo, hi) = BirdcageReplication.Boxes(resist).Single();
        Assert.Contains(grid.Y.Lines, a => a == lo[1]);
        Assert.Contains(grid.Y.Lines, a => a == hi[1]);
        Assert.Contains(grid.X.Lines, r => r == lo[0]);
        Assert.Equal((-0.001, 0.001), (lo[2], hi[2]));
        Assert.Single(xml.Descendants("Excitation"), e => (string?)e.Attribute("Name") == "port1_excite");
    }

    /// <summary>
    /// sar.md §10a.6: on given lines, a cylinder sheet's edges that lie on a line to rounding are written as the lines' own values.
    /// openEMS tests a box against each edge's position with no tolerance, and here, as in the birdcage, every edge is off its line
    /// by ulps — the azimuths are degrees turned to radians against the file's fifteen digits, the lower strip's upper end a base plus
    /// a length in metres against the file's millimetres — so a strip written as drawn holds none of that line's edges.
    /// </summary>
    [Fact]
    public void ACylinderSheet_OnGivenLines_IsWrittenOnTheLinesExactly()
    {
        string Lines(IEnumerable<double> v, string fmt) => string.Join(",", v.Select(x => x.ToString(fmt, CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(Path.Combine(_root, "Coil", "3d"));
        File.WriteAllText(Path.Combine(_root, "Coil", "3d", "lines.xml"),
            "<openEMS><ContinuousStructure CoordSystem=\"1\"><RectilinearGrid DeltaUnit=\"0.001\" CoordSystem=\"1\">" +
            $"<XLines>{Lines(Enumerable.Range(0, 21).Select(i => 2.5 * i), "R")}</XLines>" +
            $"<YLines>{Lines(Enumerable.Range(0, 73).Select(i => (-180 + 5 * i) * Math.PI / 180), "G15")}</YLines>" +
            $"<ZLines>{Lines(Enumerable.Range(0, 41).Select(i => -20.0 + i), "R")}</ZLines></RectilinearGrid></ContinuousStructure></openEMS>");
        var (doc, path) = Workspace("OpenEms", "{ \"Grid\": \"Cylindrical\", \"Axis\": \"Z\", \"GridLines\": { \"File\": \"lines.xml\" }, \"SaveFieldsGHz\": [] }");
        var (g, setup) = Assemble(doc, path);
        Assert.True(g.Ok, g.Refusal);
        var gs = CemOpenEms.ResolveGrid(setup.OpenEms, path);
        var grid = FdtdGrid.Build(g.Problem!, gs, long.MaxValue);
        Assert.Null(grid.Refusal);
        var low = CsxcadWriter.Write(g.Problem!, grid, gs, CemOpenEms.ResolveRun(setup.OpenEms));
        Assert.True(low.Ok, low.Refusal);
        var xml = XDocument.Parse(low.PortFiles[0]);
        var boxes = xml.Descendants("Metal").Select(m => BirdcageReplication.Boxes(m).Single()).ToList();
        Assert.Equal(2, boxes.Count);
        foreach (var (lo, hi) in boxes)
        {
            Assert.Equal(0.020, lo[0], 12);
            Assert.Equal(-10 * Math.PI / 180, lo[1], 12);
            Assert.Equal(10 * Math.PI / 180, hi[1], 12);
            foreach (var (lines, v) in new[] { (grid.X.Lines, lo[0]), (grid.X.Lines, hi[0]), (grid.Y.Lines, lo[1]), (grid.Y.Lines, hi[1]), (grid.Z.Lines, lo[2]), (grid.Z.Lines, hi[2]) })
                Assert.True(lines.Contains(v), $"an edge at {v:R} is not a grid line's own value");
        }
        Assert.Equal([-0.010, -0.001, 0.001, 0.010], boxes.SelectMany(b => new[] { b.Lo[2], b.Hi[2] }).Order().Select(v => Math.Round(v, 9)));
    }

    /// <summary>R-sar8b-2: a sheet or gap on a cylinder is a box of openEMS's cylindrical grid only — a Cartesian openEMS grid and Palace
    /// each refuse it by name rather than solve something flat in its place.</summary>
    [Fact]
    public void ACylinderSheet_IsRefusedByEveryOtherLowering()
    {
        var (doc, path) = Workspace("OpenEms", "{ \"SaveFieldsGHz\": [] }");
        var (g, setup) = Assemble(doc, path);
        Assert.True(g.Ok, g.Refusal);
        var gs = CemOpenEms.ResolveGrid(setup.OpenEms, path);
        var cartesian = CsxcadWriter.Write(g.Problem!, FdtdGrid.Build(g.Problem!, gs, long.MaxValue), gs, CemOpenEms.ResolveRun(setup.OpenEms));
        Assert.False(cartesian.Ok);
        Assert.Contains("'lower'", cartesian.Refusal);
        var palace = GmshGeoWriter.Write(g.Problem!, PalaceSettings.Default);
        Assert.False(palace.Ok);
        Assert.Contains("'lower'", palace.Refusal);
    }

    /// <summary>R-sar8b-3: openEMS's MultiGrid takes every second azimuth line inside each radius and refuses a level whose count is even,
    /// so a grid that would reach openEMS with one is refused first, naming the level.</summary>
    [Fact]
    public void MultiGrid_WithAnEvenAzimuthCountAtALevel_IsRefused()
    {
        var (doc, path) = Workspace("OpenEms", "{ \"Grid\": \"Cylindrical\", \"Axis\": \"Z\", \"AzimuthLines\": 303, \"MultiGridRadiiUm\": [5000, 10000], \"SaveFieldsGHz\": [] }");
        var (g, setup) = Assemble(doc, path);
        Assert.True(g.Ok, g.Refusal);
        var grid = FdtdGrid.Build(g.Problem!, CemOpenEms.ResolveGrid(setup.OpenEms, path), long.MaxValue);
        Assert.Contains("level 2 of 2 has 152", grid.Refusal);
    }

    /// <summary>A placement may turn a cylindrical sheet about its own axis — its azimuths turn with it — and move it; a turn that tips the
    /// axis off x, y and z is refused.</summary>
    [Fact]
    public void APlacement_TurnsTheAzimuths_AndATippedAxisIsRefused()
    {
        var rect = new C3dCylinderRect { Axis = C3dAxis.Z, Base = new C3dPoint3(0, 0, 0), Radius = 20 * Mm, Length = 5 * Mm, StartDeg = 10, SweepDeg = 20 };
        var turned = C3dTransform.Rotation(C3dAxis.Z, 90).Then(C3dTransform.Translation(new C3dPoint3(0, 0, 3)));
        var (p, why) = C3dLowering.CylinderPatch(rect, turned, 1000);
        Assert.Null(why);
        Assert.Equal(100 * Math.PI / 180, p!.Alpha0, 12);
        Assert.Equal(120 * Math.PI / 180, p.Alpha1, 12);
        Assert.Equal((3, 0.005 + 3), (p.Lo, p.Hi));
        var (_, tipped) = C3dLowering.CylinderPatch(rect, C3dTransform.Rotation(C3dAxis.X, 30), 1000);
        Assert.Contains("along none of x, y and z", tipped);
    }

    /// <summary>R-sar8b-4: B1 on a cylindrical grid turns (Hρ, Hα) onto the world's axes at each cell's own azimuth — a uniform Hx seen at
    /// every azimuth is Hx again — and on a grid about x the axis's own cyclic pair takes the place of (x, y).</summary>
    [Fact]
    public void B1_TurnsRhoAndAlpha_OntoTheWorldsAxes()
    {
        var aboutZ = new FdtdCylinder(FdtdAxis.Z, default, 0, null, 1, Em3dBoundaryKind.Pec, [], 8, "", 1, 1);
        foreach (double a in new[] { 0.3, 2.0, -2.7 })
        {
            var (hx, hy, hz) = aboutZ.WorldComponents(Math.Cos(a), -Math.Sin(a), 0, a);
            Assert.Equal(1, hx.Real, 12);
            Assert.Equal(0, hy.Magnitude, 12);
            Assert.Equal(0, hz.Magnitude, 12);
        }
        var aboutX = aboutZ with { Axis = FdtdAxis.X };
        var (x, y, z) = aboutX.WorldComponents(Complex.One, 0, 5, 0);       // ρ along y at α = 0 (y → z is the cyclic pair), axial along x
        Assert.Equal((5.0, 1.0, 0.0), (x.Real, y.Real, z.Real));
    }

    /// <summary>R-sar8b-4: on a cylindrical grid the Sar group is local SAR and B1 only — a stated averaging mass or sar_calc's cross-check is
    /// refused, naming it, and an unstated mass list becomes none.</summary>
    [Fact]
    public void TheSarGroup_OnACylindricalGrid_AveragesNothing()
    {
        var stated = new CemSar { FrequenciesGHz = [1], MassesGrams = [10] };
        var (none, refused) = Em3dSar.OnCylindricalGrid(SarSettings.Resolve(stated), stated);
        Assert.Null(none);
        Assert.Contains("Sar.MassesGrams", refused);
        var unstated = new CemSar { FrequenciesGHz = [1] };
        var (settings, ok) = Em3dSar.OnCylindricalGrid(SarSettings.Resolve(unstated), unstated);
        Assert.Null(ok);
        Assert.Empty(settings!.MassesGrams);
    }
}
