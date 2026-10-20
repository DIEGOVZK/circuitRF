using System.Diagnostics;
using System.Globalization;
using System.Text;
using CircuitRF.Design.Modeling;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-13's raw files and its CLI: the synthetic device of <see cref="SyntheticFetData"/> inside the GaN
/// network written as an agent would find it — a folder of Touchstone files, one per bias, named by bias and drain current,
/// and a DC I–V CSV — and the built CLI run as a process.
/// </summary>
public static class ModelCliFixture
{
    public static readonly double[] SVgs = [-1.6, -1.1, -0.6, -0.1];
    public static readonly double[] SVds = [1, 3, 6];
    public static readonly double[] Freqs = [0.5e9, 2e9, 5e9, 10e9];
    public static readonly double[] IvVgs = [-2.0, -1.6, -1.2, -0.8, -0.4, 0.0, 0.4];
    public static readonly double[] IvVds = [0, 0.4, 0.8, 1.6, 3.2, 6.4, 10];

    public const string Pattern = "Vgs{vgs}_Vds{vds}_Id{id:mA}";

    /// <summary>The raw files in <paramref name="dir"/>: <c>s/*.s2p</c> and <c>dc.csv</c>. The same bytes every call.</summary>
    public static void WriteRaw(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "s"));
        var s = SyntheticFetData.BiasedS(SyntheticFetData.NetworkDevice, SVgs, SVds, Freqs);
        for (int p = 0; p < s.Count; p++)
        {
            var b = s.Points[p];
            string name = $"Vgs{F(b.Vgs)}_Vds{F(b.Vds)}_Id{(1e3 * b.Id).ToString("F9", CultureInfo.InvariantCulture)}.s2p";
            File.WriteAllText(Path.Combine(dir, "s", name), ExportFixture.Touchstone(s, p));
        }
        var iv = SyntheticFetData.Iv(IvVgs, IvVds);
        var csv = new StringBuilder("Vgs,Vds,Ig,Id\n");
        foreach (var p in iv.Points)
            csv.Append(string.Join(',', new[] { p.Vgs, p.Vds, p.Ig, p.Id }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))).Append('\n');
        File.WriteAllText(Path.Combine(dir, "dc.csv"), csv.ToString());
    }

    /// <summary>The <c>model import</c> arguments of the S set and the DC set, after <c>model import &lt;cmc&gt;</c>.</summary>
    public static readonly string[] ImportS =
        ["s", "--as", "SParams", "--name", "bias", "--pattern", Pattern, "--condition", "ReferencePlane=device", "--condition", "AmbientC=25"];
    public static readonly string[] ImportDc =
        ["dc.csv", "--as", "DcIv", "--name", "dc", "--map", "Vgs=vgs:V", "--map", "Vds=vds:V", "--map", "Ig=ig:A", "--map", "Id=id:A",
         "--condition", "ReferencePlane=device", "--condition", "AmbientC=25"];

    /// <summary>The same two sets as the in-process importer is handed them.</summary>
    public static CmcDataSet SSet(string cmcPath) => new()
    {
        Name = "bias", Kind = DataSetKind.SParams, Sources = ModelCommands.Sources(cmcPath, [Path.Combine(Path.GetDirectoryName(cmcPath)!, "s")]),
        Conditions = new DataConditions { ReferencePlane = ReferencePlane.Device, AmbientC = 25 },
        Import = new ImportSettings { Format = ImportSettings.Touchstone, Pattern = Pattern, PatternSource = "name" },
    };

    public static CmcDataSet DcSet => new()
    {
        Name = "dc", Kind = DataSetKind.DcIv, Sources = ["dc.csv"],
        Conditions = new DataConditions { ReferencePlane = ReferencePlane.Device, AmbientC = 25 },
        Import = new ImportSettings
        {
            Format = ImportSettings.Csv,
            Columns = [new("Vgs", "vgs", "V"), new("Vds", "vds", "V"), new("Ig", "ig", "A"), new("Id", "id", "A")],
        },
    };

    /// <summary>
    /// What an agent writes into the <c>.cmc</c> before fitting to keep the run short: the network held at its stock values
    /// (the truth's), one start, and small networks. Written through the document's own setters, as the window would.
    /// </summary>
    public static void Quick(string cmcPath)
    {
        var doc = CmcFile.Read(cmcPath);
        var n = CmcExtrinsic.Network(doc);
        doc = CmcExtrinsic.With(doc, n with
        {
            Variables = n.Variables.ToDictionary(kv => kv.Key, kv => kv.Value with { Lock = true }, StringComparer.Ordinal),
        });
        var c = CurrentFit.Settings(doc);
        doc = CurrentFit.WithSettings(doc, c with
        {
            Restarts = 1,
            Id = c.Id with { Hidden = [6], MaxIterations = 1500 },
            Ig = c.Ig with { Hidden = [4], MaxIterations = 500 },
        });
        var q = ChargeFit.Settings(doc);
        doc = ChargeFit.WithSettings(doc, q with
        {
            Restarts = 1,
            Qg = q.Qg with { Hidden = [6], MaxIterations = 800 },
            Qd = q.Qd with { Hidden = [6], MaxIterations = 800 },
        });
        CmcFile.Write(cmcPath, doc);
    }

    /// <summary>The built CLI as a process, from <paramref name="workingDirectory"/>.</summary>
    public static (int ExitCode, string StdOut, string StdErr) Run(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    public static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ModelCliFixture).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    public static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    /// <summary>Every file under <paramref name="root"/> that is a result rather than a cache index, relative, sorted.</summary>
    public static IReadOnlyList<string> Files(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                     .Where(f => !f.EndsWith("index.json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal)];

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The fundamental's magnitude at <paramref name="node"/> in an HB result file's voltage cube.</summary>
    public static double Fundamental(string npy, string node)
    {
        var ds = RfCore.Export.DataSetImporter.Import(npy).DataSet;
        var v = ds.Cubes.First(kv => kv.Key == "V" || kv.Key.EndsWith(".V", StringComparison.Ordinal)).Value;
        int row = Array.FindIndex(v.Axes[0].Labels!, l => l == node);
        return ((System.Numerics.Complex)v[row, 1]).Magnitude;
    }
}
