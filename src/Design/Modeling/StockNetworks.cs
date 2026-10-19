// The stock six-pin networks of overview D5 (brief-modelcomposer-4 R-mc4-2). They ship as embedded .csch resources
// (StockNetworks/*.csch, listed in CircuitRF.Design.csproj); choosing one COPIES it beside the .cmc, so from then on
// it is an ordinary user file that the user edits like any schematic and the document references by relative path.
// The resources were drawn from their netlists by NetlistSchematic, the drawing `netlist --to-schematic` makes, with
// six numbered pins added.

using CircuitRF.Core.Devices.Composed;

namespace CircuitRF.Design.Modeling;

/// <summary>One stock network: its resource id, its title, and its D9 scaling defaults by variable.</summary>
public sealed record StockNetwork(string Id, string Title, IReadOnlyDictionary<string, string> Scaling);

public static class StockNetworks
{
    private const string Prefix = "CircuitRF.Design.Modeling.StockNetworks.";

    private static Dictionary<string, string> Rules(string inverse, string perFinger) =>
        inverse.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => (v, PeripheryScaling.PerFingerInverse))
               .Concat(perFinger.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => (v, PeripheryScaling.PerFinger)))
               .ToDictionary(p => p.v, p => p.Item2, StringComparer.Ordinal);

    /// <summary>The stock networks, in the order a chooser lists them. "None" — data already intrinsic — is the
    /// absence of a network, not an entry here.</summary>
    public static IReadOnlyList<StockNetwork> All { get; } =
    [
        // D9's defaults: the series resistors scale inversely, the inner capacitors with the fingers, the rest fixed.
        new("gan-two-shell", "GaN two-shell", Rules("rg rd rs", "Cpgi Cpdi Cpdgi")),
        new("classic-shell", "Classic single shell", Rules("Rg Rd Rs", "")),
        new("series-only", "Series only", Rules("Rg Rd Rs", "")),
    ];

    public static StockNetwork Find(string id) =>
        All.FirstOrDefault(s => s.Id == id)
        ?? throw new ArgumentException($"'{id}' is not a stock network; the stock networks are {string.Join(", ", All.Select(s => s.Id))}.", nameof(id));

    /// <summary>The resource's bytes.</summary>
    public static byte[] Bytes(string id)
    {
        Find(id);
        using var s = typeof(StockNetworks).Assembly.GetManifestResourceStream(Prefix + id + ".csch")
                      ?? throw new InvalidOperationException($"The stock network '{id}' is missing from this build.");
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>
    /// Copies stock network <paramref name="id"/> into <paramref name="folder"/> as <c>&lt;id&gt;.csch</c> — or
    /// <c>&lt;id&gt;-2.csch</c> and so on, never over a file already there — and returns the new file's path.
    /// </summary>
    public static string CopyInto(string folder, string id)
    {
        byte[] bytes = Bytes(id);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, id + ".csch");
        for (int k = 2; File.Exists(path); k++) path = Path.Combine(folder, $"{id}-{k}.csch");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) fs.Write(bytes);
        return path;
    }

    /// <summary>
    /// Copies stock network <paramref name="id"/> beside the <c>.cmc</c> and returns the document with a network block
    /// naming it, every variable at the defaults of R-mc4-2 and the stock D9 scaling rules.
    /// </summary>
    public static CmcDocument Choose(CmcDocument doc, string cmcPath, string id)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(cmcPath))!;
        string path = CopyInto(folder, id);
        var network = SixPinNetwork.Load(path);
        string relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
        var settings = network.DefaultSettings(relative, Find(id).Scaling);
        var keep = CmcExtrinsic.Network(doc);
        return CmcExtrinsic.With(doc, settings with { FlatnessBand = keep.FlatnessBand });
    }
}
