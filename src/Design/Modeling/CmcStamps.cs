// What a fitted step was computed from, and whether it still holds (brief-modelcomposer-11 R-mc11-1).
//
// A step is STALE when anything it read has changed since it ran: the network (MC-5 R-mc5-5, NetworkFit.Digest) or the
// data. Each fit stamps its block in Steps with both digests as it writes; StaleSteps compares them with the document as
// it now stands. Export refuses a stale step, because a model whose constituents were fitted through another network or
// to other data is a model of nothing the document describes.
//
// The data digest covers what the steps read: the pads, and every data set the fits can take (a LargeSignal set is
// validation only and adding one stales nothing) — its definition as the .cmc writes it (sources, conditions, import,
// exclusions) and the SHA-256 of each source as the cache index recorded it. A step written before stamps existed carries
// none and is not called stale by the data: there is nothing to compare.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CircuitRF.Design.Modeling;

public static class CmcStamps
{
    /// <summary>The key a step block records the data digest under.</summary>
    public const string DataKey = "Data";

    /// <summary>The key a step block records the network digest under (<see cref="NetworkFit.Digest"/>).</summary>
    public const string NetworkKey = "Network";

    /// <summary>Whether a data set is read by the fits — everything but validation-only large-signal data.</summary>
    public static bool FeedsFits(CmcDataSet set) => set.Kind != DataSetKind.LargeSignal;

    /// <summary>
    /// The digest of the data the steps of <paramref name="doc"/> read. Source hashes come from the cache index when it has
    /// them (an open has just made it current), else from the files themselves.
    /// </summary>
    public static string DataDigest(string cmcPath, CmcDocument doc)
    {
        var known = new CmcCache(cmcPath).SourceHashes();
        string dir = Path.GetDirectoryName(Path.GetFullPath(cmcPath))!;
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (doc.Pads is { } pads) h.AppendData(Encoding.UTF8.GetBytes(pads.ToJsonString()));
        foreach (var set in doc.DataSets.Where(FeedsFits))
        {
            h.AppendData(CmcFile.ToBytes(new CmcDocument { Kind = doc.Kind, DataSets = [set] }));
            foreach (string src in set.Sources)
            {
                string sha = known.TryGetValue((set.Name, src), out var s) ? s : HashOf(DataImport.ResolveSource(dir, src));
                h.AppendData(Encoding.UTF8.GetBytes($"{src}\n{sha}\n"));
            }
        }
        return Convert.ToHexStringLower(h.GetHashAndReset())[..16];
    }

    private static string HashOf(string path)
    {
        if (!File.Exists(path)) return "missing";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    /// <summary>
    /// <paramref name="doc"/> with <c>Steps.&lt;step&gt;</c> stamped with the current network digest and
    /// <paramref name="dataDigest"/> — what each step's <c>FitFile</c> calls as it writes.
    /// </summary>
    public static CmcDocument Stamp(CmcDocument doc, string step, string dataDigest)
    {
        var steps = doc.Steps is JsonObject s ? (JsonObject)JsonNode.Parse(s.ToJsonString())! : new JsonObject();
        if (steps[step] is not JsonObject o) return doc;
        o[NetworkKey] = NetworkFit.Digest(CmcExtrinsic.Network(doc));
        o[DataKey] = dataDigest;
        return doc with { Steps = steps };
    }

    /// <summary>The network block with its fit's record stamped with <paramref name="dataDigest"/>.</summary>
    public static CmcDocument StampNetwork(CmcDocument doc, string dataDigest)
    {
        var n = CmcExtrinsic.Network(doc);
        return n.Result is { } r ? CmcExtrinsic.With(doc, n with { Result = r with { Data = dataDigest } }) : doc;
    }

    /// <summary>
    /// Every stale step, by name, in document order (R-mc11-1): a step through another network, or one whose data digest
    /// differs from the data's now — the network fit itself included, as <c>Network</c>.
    /// </summary>
    public static IReadOnlyList<string> StaleSteps(string cmcPath, CmcDocument doc)
    {
        var stale = new List<string>();
        string? data = null;
        string Data() => data ??= DataDigest(cmcPath, doc);
        if (CmcExtrinsic.Network(doc).Result is { Data: { } nd } && nd != Data()) stale.Add("Network");
        foreach (string name in NetworkFit.StaleSteps(doc)) if (!stale.Contains(name)) stale.Add(name);
        if (doc.Steps is JsonObject steps)
            foreach (var (name, node) in steps)
                if (node is JsonObject o && o[DataKey] is JsonValue v && v.TryGetValue<string>(out var d) && d != Data()
                    && !stale.Contains(name))
                    stale.Add(name);
        if (TrapFit.CurrentsDisagree(doc) && !stale.Contains(TrapFit.StepName)) stale.Add(TrapFit.StepName);
        return stale;
    }

    /// <summary>One line per stale step, naming what moved under it.</summary>
    public static string Describe(IReadOnlyList<string> stale) =>
        stale.Count == 1 ? $"{stale[0]} was fitted before the network, the data or the trap model last changed; run it again."
                         : $"{string.Join(", ", stale)} were fitted before the network, the data or the trap model last changed; run them again.";
}
