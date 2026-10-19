// Which schematic format a file is, by its CONTENT — brief-schimp-1 R-si1-10, overview §0 finding 5.
//
// Never by extension: `.dsn` names three unrelated formats (a binary schematic design inside a compound file, an ASCII
// record-based kit symbol, a board-routing exchange file) and only the first is a schematic design. The binary one is
// told apart by its compound-file signature; the other two are "not a schematic design" from here and are never routed
// to DsnSymbolReader by this classifier.

using System.Text;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Schematic.Import;

/// <summary>The schematic formats Import Schematic reads.</summary>
public enum SchematicFileKind
{
    Unknown,
    /// <summary>The binary <c>.DSN</c> design, inside a compound file (CFBF). SI-4 confirms it by its root streams.</summary>
    BinaryDsn,
    /// <summary><c>.kicad_sch</c>.</summary>
    KicadSch,
    /// <summary>EDIF 2.0.0.</summary>
    Edif200,
}

/// <summary>A classification, with the reason when it is not a format read here.</summary>
public sealed record SchematicFileClass(SchematicFileKind Kind, string? Reason = null)
{
    public bool IsSchematic => Kind != SchematicFileKind.Unknown;
}

public static class SchematicFileClassifier
{
    /// <summary>The compound-file signature (MS-CFB): the first eight bytes of every CFBF container.</summary>
    private static readonly byte[] CfbfSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>How much of a text file is read to classify it — the version statement sits in an EDIF file's header.</summary>
    private const int Head = 64 * 1024;

    public static SchematicFileClass Classify(string path)
    {
        byte[] head;
        try
        {
            using var stream = File.OpenRead(path);
            head = new byte[Math.Min(Head, stream.Length)];
            stream.ReadExactly(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SchematicFileClass(SchematicFileKind.Unknown, $"It could not be read: {ex.Message}");
        }
        return Classify(head);
    }

    /// <summary>Classifies the first bytes of a file (<see cref="Head"/> is enough).</summary>
    public static SchematicFileClass Classify(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual(CfbfSignature))
            return new SchematicFileClass(SchematicFileKind.BinaryDsn);

        string text = Encoding.Latin1.GetString(head).TrimStart('﻿', 'ï', '»', '¿', ' ', '\t', '\r', '\n');
        if (text.StartsWith("(kicad_sch", StringComparison.Ordinal)) return new SchematicFileClass(SchematicFileKind.KicadSch);
        if (Regex.IsMatch(text, @"^\(\s*edif\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var version = Regex.Match(text, @"\(\s*edifVersion\s+(\d+)\s+(\d+)\s+(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!version.Success) return new SchematicFileClass(SchematicFileKind.Unknown, "It is an EDIF file that states no edifVersion.");
            string v = $"{version.Groups[1].Value} {version.Groups[2].Value} {version.Groups[3].Value}";
            return v == "2 0 0"
                ? new SchematicFileClass(SchematicFileKind.Edif200)
                : new SchematicFileClass(SchematicFileKind.Unknown, $"It is EDIF version {v}; only EDIF 2 0 0 is read.");
        }
        return new SchematicFileClass(SchematicFileKind.Unknown, "It is not a schematic design.");
    }
}
