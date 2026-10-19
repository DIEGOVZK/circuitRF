// brief-schimp-6-acceptance-and-docs.md R-si6-2 — an imported schematic of native parts is a circuit: a committed
// matching network, imported, checked and run by the CLI as a process, has the S-parameters of the same circuit
// written as a .cnl.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Import;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.ImportSchematic;

public sealed class SchematicImportAcceptanceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-schimp6-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;

    /// <summary>R-si6-2: an imported schematic of native parts simulates as the circuit it draws. The CLI imports the
    /// committed <c>matching.kicad_sch</c> with both connectors as Ports, <c>check</c> finds nothing, and its S-parameters
    /// equal those of the same network written by hand as a <c>.cnl</c>, to 1e-9.</summary>
    [Fact]
    public void AnImportedMatchingNetwork_ChecksClean_AndSimulatesAsTheSameCircuitWritten()
    {
        string fixture = Path.Combine(FixturePaths.Find("testdata/schematic-import/sexpr") ?? throw new InvalidOperationException("fixtures missing"),
                                      "matching.kicad_sch");
        string into = Folder("ws");
        var import = RunCli("import", "schematic", fixture, "--into", into, "--port", "J1", "--port", "J2");
        Assert.True(import.ExitCode == 0, import.StdErr);
        string csch = Path.Combine(into, "matching", "schematic", "matching.csch");
        Assert.True(File.Exists(csch), import.StdOut);

        var check = RunCli("check", Path.Combine(into, "matching"));
        Assert.True(check.ExitCode == 0, check.StdOut + check.StdErr);
        Assert.Contains(" 0 error(s), 0 warning(s)", check.StdOut);

        string native = Path.Combine(into, "matching.cnl");
        File.WriteAllText(native, """
            Port:P1 in  0 Num=1 Z=50 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            L:L1 in  mid L=3.3 nH
            C:C1 mid 0   C=1.5 pF
            R:R1 mid out R=4.7 Ohm
            """);
        string importedSnp = Path.Combine(_dir, "imported.s2p"), nativeSnp = Path.Combine(_dir, "native.s2p");
        foreach (var (source, snp) in new[] { (csch, importedSnp), (native, nativeSnp) })
        {
            var run = RunCli("sparam", source, "--freq", "0.1GHz:6GHz:0.1GHz", "-o", snp);
            Assert.True(run.ExitCode == 0, run.StdErr);
        }

        var a = TouchstoneIO.ReadFile(importedSnp);
        var b = TouchstoneIO.ReadFile(nativeSnp);
        Assert.Equal(2, a.Ports);
        Assert.Equal(b.Frequencies, a.Frequencies);
        Assert.Equal(60, a.Frequencies.Length);
        for (int k = 0; k < a.Frequencies.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True((a.Matrices[k][i, j] - b.Matrices[k][i, j]).Magnitude <= 1e-9,
                                $"S{i + 1}{j + 1} at {a.Frequencies[k]}: {a.Matrices[k][i, j]} against {b.Matrices[k][i, j]}");
        // Not a trivially equal pair: the network is a low-pass — it passes at 100 MHz and not at 6 GHz.
        Assert.True(a.Matrices[0][1, 0].Magnitude > 0.9 && a.Matrices[^1][1, 0].Magnitude < 0.5);
    }

    // ── the CLI as a process (AuthoringCliVerbTests' way) ─────────────────────────────────────────

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _dir,
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
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(SchematicImportAcceptanceTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }
}
