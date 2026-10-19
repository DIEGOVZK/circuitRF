// brief-schimp-5-entry-points.md §3 — the gates of SI-5: the CLI as a process against the in-process import, byte for
// byte but the provenance timestamp; the dialog's preview against the drawing the import writes; a schematic file and a
// picture dropped on an open schematic, and Undo after the drop; and the source scan proving the entry points hold no
// reader or build logic of their own (overview D1).

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Import;
using CircuitRF.Render;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Recognition;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.ImportSchematic;

public sealed class SchematicImportEntryPointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-schimp5-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Fixture(string name) =>
        Path.Combine(FixturePaths.Find("testdata/schematic-import/sexpr") ?? throw new InvalidOperationException("fixtures missing"), name);

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;

    /// <summary>Every file under <paramref name="root"/>, relative, with the provenance timestamp blanked — the one line
    /// the file carries by design that two runs cannot share.</summary>
    private static string[] Tree(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(root, f) + "\n"
                       + Regex.Replace(File.ReadAllText(f), "\"ImportedUtc\":\\s*\"[^\"]*\"", "\"ImportedUtc\": \"\""))];

    /// <summary>R-si5-5: the verb is the in-process call — the cell, its schematic and the part cell written beside it.</summary>
    [Fact]
    public void TheCli_WritesWhatTheInProcessImportWrites_ByteForByte()
    {
        string fixture = Fixture("opamp_units.kicad_sch");
        string inProcess = Folder("a"), viaCli = Folder("b");

        var read = SchematicImport.Read(fixture);
        Assert.True(read.Ok, read.Refusal);
        var result = SchematicImport.Import(read.Schematic!, inProcess, "opamp_units");
        Assert.True(result.Ok, result.Refusal);
        Assert.NotEmpty(result.CreatedCells);

        var run = RunCli("import", "schematic", fixture, "--into", viaCli);
        Assert.True(run.ExitCode == 0, run.StdErr);
        Assert.Equal(Tree(inProcess), Tree(viaCli));
        // The paths created ARE the result.
        Assert.Contains(Path.Combine(viaCli, "opamp_units"), run.StdOut.Split('\n').Select(l => l.Trim()));
    }

    /// <summary>R-si5-2: the preview is the drawing Import writes — the same pixels — and nothing is written until Import.</summary>
    [Fact]
    public async Task ThePreview_IsTheImportedCellsDrawing_AndNothingIsWrittenBeforeImport()
    {
        string target = Folder("ws");
        using var vm = new ImportSchematicViewModel(Fixture("opamp_units.kicad_sch"), target);
        await vm.Loaded;
        Assert.Null(vm.ReadRefusal);
        Assert.True(vm.CanImport, vm.ImportRefusal ?? vm.NameError);
        Assert.True(vm.ConnectivityMatched, vm.ReportText);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));

        var theme = SchematicRenderTheme.FromTheme(ColorTheme.BuiltIn, ColorVariant.Light);
        using var preview = ComponentPreviewRaster.RasterSchematic(vm.Preview!.Drawing, theme, 640, 480)!;

        var result = vm.Import()!;
        Assert.True(result.Ok, result.Refusal);
        var (imported, _, _) = SchematicPersistence.LoadFromFile(result.SchematicPath!);
        using var written = ComponentPreviewRaster.RasterSchematic(imported, theme, 640, 480)!;
        Assert.Equal(written.Bytes, preview.Bytes);
    }

    /// <summary>A saved, empty schematic in a cell of its own — what a drop builds into.</summary>
    private SchematicDocument SavedSchematic()
    {
        string path = CellCreate.Create(Folder("ws"), "Top", CellViews.Schematic).SchematicPath!;
        var (model, _, _) = SchematicPersistence.LoadFromFile(path);
        return new SchematicDocument("Top", new SchematicViewModel(model), path);
    }

    /// <summary>R-si5-3: a dropped design is built at the drop point as ONE undo step — components and no bitmap — and
    /// Undo restores the document exactly.</summary>
    [Fact]
    public async Task ADroppedDesign_IsBuiltAsOneUndoStep_AndUndoRestoresTheDocument()
    {
        var messages = new RecordingMessages();
        var workspace = new WorkspaceViewModel { MessagesOverride = messages };
        var doc = SavedSchematic();
        var vm = doc.ActiveViewModel;
        string before = SchematicPersistence.Serialize(vm.EditModel);

        await workspace.BuildSchematicFileIntoAsync(doc, Fixture("opamp_units.kicad_sch"), (1000, 1000));

        var row = Assert.Single(messages.Rows);
        Assert.Matches(@"^Built opamp_units\.kicad_sch into Top\.csch: \d+ parts? \(\d+ native, \d+ symbol cells?, 0 reused\), connectivity matched\.$", row.Text);
        Assert.Equal("Details…", row.ActionLabel);
        Assert.NotEmpty(vm.EditModel.Components);
        Assert.DoesNotContain(vm.EditModel.CanvasObjects, o => o is EditableBitmap);
        Assert.True(Directory.Exists(Path.Combine(_dir, "ws", "opamp_units_parts")));

        vm.UndoRedo.Undo();
        Assert.Equal(before, SchematicPersistence.Serialize(vm.EditModel));
    }

    /// <summary>R-si5-3: a picture stays a picture — one bitmap, no components — and is never taken for a design, by its
    /// extension (the drag-over) or by its content (the drop).</summary>
    [Fact]
    public void ADroppedPicture_IsOneBitmap_AndNeverADesign()
    {
        string png = Path.Combine(_dir, "board.png");
        using (var bitmap = new SkiaSharp.SKBitmap(8, 8))
        using (var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(png, data.ToArray());
        Assert.False(SchematicImport.HasSchematicExtension(png));
        Assert.False(SchematicFileClassifier.Classify(png).IsSchematic);
        Assert.True(SchematicImport.HasSchematicExtension(Fixture("divider.kicad_sch")));

        var vm = SavedSchematic().ActiveViewModel;
        vm.DropBitmap(png, 0, 0);
        Assert.Single(vm.EditModel.CanvasObjects.OfType<EditableBitmap>());
        Assert.Empty(vm.EditModel.Components);
    }

    /// <summary>Overview D1: the entry points call SchematicImport and hold no reader, build, merge or check of their own —
    /// the rule a comment-stripped scan holds, as it does for `import part`.</summary>
    [Theory]
    [InlineData("src/Cli/Authoring.cs")]
    [InlineData("src/Cli/Serve/ToolCatalog.cs")]
    [InlineData("src/Ui/ViewModels/ImportSchematicViewModel.cs")]
    [InlineData("src/Ui/ViewModels/WorkspaceViewModel.ImportSchematic.cs")]
    [InlineData("src/Ui/Views/Dialogs/ImportSchematicDialog.axaml.cs")]
    [InlineData("src/Ui/Controls/SchematicCanvas.cs")]
    [InlineData("src/Ui/Views/ProjectTree/ProjectTreeView.axaml.cs")]
    public void TheEntryPoints_HoldNoImportLogicOfTheirOwn(string file)
    {
        string code = File.ReadAllText(Path.Combine(RepoRoot(), file));
        code = Regex.Replace(code, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        code = Regex.Replace(code, @"//[^\n]*", " ");
        foreach (string forbidden in new[]
                 {
                     "SchematicSexprReader", "EdifReader", "SchematicFileClassifier.Classify(path).Kind", "SchematicImportBuild",
                     "NetlistSchematic", "ImportConnectivity", "SchematicMerge",
                 })
            Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{file} reaches past SchematicImport into {forbidden}.");
    }

    // ── the CLI as a process (AuthoringCliVerbTests' way) ─────────────────────────────────────────

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment[CircuitRF.Design.UserStateDirectory.EnvironmentVariable] = Folder("state");
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(SchematicImportEntryPointTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
