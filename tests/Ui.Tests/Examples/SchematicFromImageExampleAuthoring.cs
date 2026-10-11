// ================================================================
//  SchematicFromImageExampleAuthoring.cs — the Schematic from Image example (brief-img-12 R-im12-5), BUILT with the
//  functions the application's own commands call. Skipped unless CRF_AUTHOR_IMAGE=1, because it WRITES
//  examples/Schematic from Image (the folder is replaced; its README.md is kept).
//
//  Two pictures of one small network — a capacitor and an inductor in series on a line — drawn here, in the faces
//  circuitRF ships, as the pictures a user brings would be:
//    pictures/matching-network.png        the schematic as a datasheet prints it, the inductor's value left off;
//    pictures/matching-network-board.png  its board as a Gerber viewer shows it — the copper and the silkscreen's
//                                         designators — at 20 µm a pixel.
//  Each is made into a cell by Create Schematic from Image, as the dialog makes it (SchematicImageRecognition.Run and
//  ImageRecognition.Run): "Matching network" from the schematic, "Matching board" from the board — a layout and a
//  schematic, both over their picture. The inductor's missing value, and both values on the board (a board prints no
//  values), arrive as variables with tune entries. Both parts stand in the line: a part on a stub to a via is not read
//  from copper alone (design note §13).
// ================================================================

using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Layout.Recognition.Silkscreen;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Tests.Em3d;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class SchematicFromImageExampleAuthoring
{
    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_IMAGE") != "1")
                Skip = "re-builds examples/Schematic from Image only with CRF_AUTHOR_IMAGE=1";
        }
    }

    public const string TechnologyId = "pcb-2layer_RO4350B_20mil_1oz";
    public const double BoardUmPerPx = 20;
    private static readonly DateTime When = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    // ── the schematic picture ────────────────────────────────────────────────────────────────────────────────

    // DejaVu Sans's cap height is 0.729 em: a 14 px cap height, over a 2 px pen.
    private const float Size = 14f / 0.729f;

    public static RasterImage SchematicPicture() => Pictures.Draw(560, 240, c =>
    {
        using var pen = SchematicPictures.Ink();
        // P1, and the capacitor's plates across the line.
        c.DrawCircle(40, 120, 8, pen);
        SchematicPictures.Text(c, "P1", 30, 98, Size);
        c.DrawLine(48, 120, 180, 120, pen);
        c.DrawLine(180, 106, 180, 134, pen);
        c.DrawLine(188, 106, 188, 134, pen);
        SchematicPictures.Text(c, "C1", 168, 92, Size);
        SchematicPictures.Text(c, "1.5pF", 160, 162, Size);
        c.DrawLine(188, 120, 330, 120, pen);
        // The inductor, four humps; its value left off.
        using (var humps = new SKPath())
        {
            humps.MoveTo(330, 120);
            for (int k = 0; k < 4; k++) humps.ArcTo(new SKRect(330 + 12 * k, 114, 342 + 12 * k, 126), 180, 180, false);
            c.DrawPath(humps, pen);
        }
        SchematicPictures.Text(c, "L1", 342, 98, Size);
        c.DrawLine(378, 120, 512, 120, pen);
        // P2.
        c.DrawCircle(520, 120, 8, pen);
        SchematicPictures.Text(c, "P2", 510, 98, Size);
    });

    // ── the board picture ────────────────────────────────────────────────────────────────────────────────────

    public static readonly SKColor Copper = new(0xc8, 0x75, 0x33), Legend = new(0x20, 0x30, 0x90);

    public static RasterImage BoardPicture() => Pictures.Draw(640, 240, c =>
    {
        var r = LandPatternMatch.References.All.First(r => r.Case.Code == "0603" && r.Density == DensityLevel.Nominal);
        float along = (float)(r.AlongUm / BoardUmPerPx), across = (float)(r.AcrossUm / BoardUmPerPx), gap = (float)(r.GapUm / BoardUmPerPx);
        const float y = 140, w = 450f / (float)BoardUmPerPx;   // a 0.45 mm line along y
        using var cu = Pictures.Fill(Copper);

        // The line in from the left edge, C1's pair in it, the line on, L1's pair in it, the line out to the right edge.
        float x = 0;
        void Line(float to) { c.DrawRect(SKRect.Create(x, y - w / 2, to - x, w), cu); x = to; }
        void Pair()
        {
            c.DrawRect(SKRect.Create(x, y - across / 2, along, across), cu);
            c.DrawRect(SKRect.Create(x + along + gap, y - across / 2, along, across), cu);
            x += 2 * along + gap;
        }
        Line(160);
        float c1 = x;
        Pair();
        Line(340);
        float l1 = x;
        Pair();
        Line(640);

        // The silkscreen's designators, in the stroke font a board's legend is plotted in.
        using var pen = Pictures.Stroke(Legend, 3);
        pen.StrokeCap = SKStrokeCap.Round;
        pen.StrokeJoin = SKStrokeJoin.Round;
        void Label(string text, float x0, float baseline)
        {
            foreach (var stroke in GlyphTemplates.BuiltIn.Draw(text, 36))
            {
                using var path = new SKPath();
                path.MoveTo(x0 + (float)stroke[0], baseline - (float)stroke[1]);
                for (int i = 2; i + 1 < stroke.Length; i += 2) path.LineTo(x0 + (float)stroke[i], baseline - (float)stroke[i + 1]);
                c.DrawPath(path, pen);
            }
        }
        Label("C1", c1, y - across / 2 - 16);
        Label("L1", l1, y - across / 2 - 16);
    });

    // ── the workspace ────────────────────────────────────────────────────────────────────────────────────────

    [AuthorFact]
    public void BuildTheWorkspace()
    {
        string examples = Path.Combine(PalaceBackendTests.RepoRoot(), "examples");
        string ws = Path.Combine(examples, "Schematic from Image");
        string readme = Path.Combine(ws, "README.md");
        string? keptReadme = File.Exists(readme) ? File.ReadAllText(readme) : null;
        if (Directory.Exists(ws)) Directory.Delete(ws, true);

        var made = WorkspaceCreate.Create(examples, "Schematic from Image", TechnologyId);
        WorkspacePolicyFiles.Ensure(ws);
        string pictures = Path.Combine(ws, "pictures");
        Directory.CreateDirectory(pictures);
        string schematicPng = Path.Combine(pictures, "matching-network.png"), boardPng = Path.Combine(pictures, "matching-network-board.png");
        File.WriteAllBytes(schematicPng, Pictures.Png(SchematicPicture()));
        File.WriteAllBytes(boardPng, Pictures.Png(BoardPicture()));

        // The schematic picture, as the dialog makes it: a new cell, drawn as the picture draws it, over it.
        var schematic = SchematicImageRecognition.Run(new SchematicImageInput { Source = ImageSource.FromFile(schematicPng).Source! },
                                                      SchematicImageTarget.NewCell(ws, "Matching network"),
                                                      new SchematicImageRunOptions { Now = When });
        Assert.True(schematic.Ok, schematic.Refusal);

        // The board picture: its scale stated, its legend's colour mapped to the silkscreen, as the user maps it.
        var tech = TechPersistence.LoadFromFile(made.TechPath!);
        var input = new ImageTraceInput
        {
            Source = ImageSource.FromFile(boardPng).Source!, Technology = tech, TechnologyPath = made.TechPath,
            Scale = ImageScaleStatement.Stated(BoardUmPerPx * 1e-6),
        };
        var auto = ImageTrace.Trace(input);
        int legend = Pictures.ClusterOf(auto.Clusters!, (Legend.Red << 16) | (Legend.Green << 8) | Legend.Blue);
        var board = ImageRecognition.Run(new ImageRecognitionInput
        {
            Trace = input with { LayerMap = auto.LayerMap!.WithRole(tech, legend, ImageLayerRole.Silkscreen) },
        }, ImageRecognitionTarget.NewCell(ws, "Matching board"), new ImageRecognitionRunOptions { Now = When });
        Assert.True(board.Ok, board.Refusal + "\n" + string.Join("\n", board.Report.Lines()));

        if (keptReadme is not null) File.WriteAllText(readme, keptReadme);
    }
}
