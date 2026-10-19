// brief-schimp-4-binary-dsn-reader.md R-si4-3/5/6 — the binary .DSN reader, against containers built by the tests:
// the refusals.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CircuitRF.Design.Schematic.Import;
using CircuitRF.Design.Schematic.Import.BinaryDsn;
using Xunit;

namespace CircuitRF.Ui.Tests.ImportSchematic;

public sealed class BinaryDsnReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-schimp4-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, IReadOnlyDictionary<string, byte[]> streams)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, CfbTestWriter.Build(streams));
        return path;
    }

    /// <summary>A Library stream stating version <paramref name="major"/>.<paramref name="minor"/>.</summary>
    private static byte[] Library(int major, int minor)
    {
        var b = new byte[64];
        Encoding.ASCII.GetBytes("Windows Design".PadRight(31)).CopyTo(b, 0);
        b[32] = (byte)major;
        b[34] = (byte)minor;
        return b;
    }

    /// <summary>R-si4-3: a version not read is refused by number, with the EDIF route — and classified as the binary
    /// design it is, never as something else.</summary>
    [Fact]
    public void AnUnreadVersion_IsRefusedByNumber_WithTheEdifRoute()
    {
        string path = Write("future.dsn", new Dictionary<string, byte[]>
        {
            ["Library"] = Library(9, 1),
            ["Cache"] = new byte[8],
            ["Views/SCHEMATIC1/Pages/PAGE1"] = new byte[8],
        });
        var read = SchematicImport.Read(path);
        Assert.Equal(SchematicFileKind.BinaryDsn, read.Kind);
        Assert.False(read.Ok);
        Assert.Contains("version (9.1) is not readable", read.Refusal);
        Assert.Contains("EDIF 2.0.0", read.Refusal);
    }

    /// <summary>A compound file that is not a design — a spreadsheet, a symbol library — is said to be one, not misread.</summary>
    [Fact]
    public void ACompoundFileThatIsNotADesign_IsRefusedAsSuch()
    {
        string path = Write("book.dsn", new Dictionary<string, byte[]> { ["Workbook"] = new byte[600] });
        var read = BinaryDsnReader.Read(path);
        Assert.False(read.Ok);
        Assert.Contains("not a schematic design", read.Refusal);
    }
}
