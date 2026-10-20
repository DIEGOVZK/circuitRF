// From any data set and any extrinsic network, the INTRINSIC voltages, currents, Y-parameters and small-signal
// elements at every bias point (brief-modelcomposer-4 R-mc4-3 … R-mc4-7; overview D5, D6, D13).
//
//   1. Pads off (R-mc4-1) — S at the instrument or probe plane moves to the device plane.
//   2. Through the network (R-mc4-3) — the network solved as a 4-port by the linear engine, then
//      Y_int = Y_ba (Y_aa − Y_ext)⁻¹ Y_ab − Y_bb at every point and frequency, with its condition numbers.
//   3. Re-referencing (R-mc4-4) — the bias moved from its stated plane to the device plane (bias-path drop, pad
//      series resistance), then a DC solve of the network with the measured currents gives the intrinsic voltages:
//      [P1] eq. 6.12 for any network.
//   4. The quantities and their flatness (R-mc4-5).
//
// The result is one DataSet per data set (R-mc4-6). Later steps and the plots read only it — nothing recomputes it
// privately — and it is cached beside the import cache, keyed by everything it depends on (the data, its conditions,
// the pads, the network file and the values its variables take), so an unchanged network reopens without a solve.
// Cancellation leaves the cache untouched and returns nothing (R-mc4-7, D13).

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Modeling;

/// <summary>Every data set's intrinsic data by name, and the one-line notes the work produced.</summary>
public sealed record IntrinsicResult(IReadOnlyDictionary<string, DataSet> Sets, IReadOnlyList<string> Notes);

public sealed class IntrinsicExtraction
{
    public const string CacheFolder = "intrinsic";

    private readonly string _cmcPath;
    private long _computations;

    public IntrinsicExtraction(string cmcPath) => _cmcPath = Path.GetFullPath(cmcPath);

    /// <summary>Data sets computed rather than read from the cache — zero on a reopen with nothing changed.</summary>
    public long Computations => Interlocked.Read(ref _computations);

    /// <summary>
    /// Every data set's intrinsic data (R-mc4-6). <paramref name="data"/> is the open document's normalised data
    /// (<see cref="CmcCache.Open"/>); <paramref name="values"/> overrides network variables (a fit's trial point) over the
    /// document's own <see cref="NetworkVariableSettings.Value"/>s.
    /// Throws <see cref="ModelingException"/> for a refused input and <see cref="OperationCanceledException"/> when
    /// cancelled; in both cases nothing is written.
    /// </summary>
    public IntrinsicResult Run(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                               IReadOnlyDictionary<string, double>? values = null, RunControl? rc = null)
    {
        string dir = Path.GetDirectoryName(_cmcPath)!;
        var padsSettings = CmcExtrinsic.Pads(doc);
        var netSettings = CmcExtrinsic.Network(doc);
        var pads = PadDeembedding.Load(padsSettings, dir);
        string? netPath = netSettings.Schematic is null ? null : DataImport.ResolveSource(dir, netSettings.Schematic);
        SixPinNetwork? network = netPath is null ? null : SixPinNetwork.Load(netPath);
        // The document's own values (a typed or fitted Value, MC-5) first, then the caller's trial point over them.
        var docValues = netSettings.Values;
        if (values is not null || docValues.Count > 0)
        {
            var merged = new Dictionary<string, double>(docValues, StringComparer.Ordinal);
            if (values is not null) foreach (var (k, v) in values) merged[k] = v;
            values = merged;
        }
        var resolved = network?.Values(values);

        string shared = SharedKey(dir, padsSettings, netPath, resolved, netSettings.FlatnessBand);
        string folder = Path.Combine(CmcCache.FolderFor(_cmcPath), CacheFolder);
        var sets = new Dictionary<string, DataSet>(StringComparer.Ordinal);
        var notes = new List<string>();
        var fresh = new Dictionary<string, DataSet>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var blocks = new Dictionary<string, NetworkBlocks[]>(StringComparer.Ordinal);

        foreach (var set in doc.DataSets)
        {
            rc?.ThrowIfCancellationRequested();
            if (!data.TryGetValue(set.Name, out var nd)) continue;
            string key = Key(shared, set, nd);
            keys.Add(key);
            string file = Path.Combine(folder, key + ".npy");
            DataSet ds;
            if (File.Exists(file) && TryRead(file) is { } cached)
                ds = cached;
            else
            {
                rc?.BeginStage($"De-embedding {set.Name}", nd.Count);
                var setNotes = new List<string>();
                ds = Compute(set, nd, pads, network, values, netSettings.FlatnessBand, setNotes, rc, blocks);
                AttachNotes(ds, setNotes);
                Interlocked.Increment(ref _computations);
                fresh[key] = ds;
            }
            notes.AddRange(NotesOf(ds));
            sets[set.Name] = ds;
        }

        rc?.ThrowIfCancellationRequested();
        if (fresh.Count > 0) Write(folder, fresh, keys);
        return new IntrinsicResult(sets, notes);
    }

    // ── One data set, no cache ────────────────────────────────────────────────

    /// <summary>
    /// One data set's intrinsic data — the pure computation <see cref="Run"/> caches. <paramref name="blocks"/>, when
    /// given, shares the network's solve between sets on one frequency axis.
    /// </summary>
    public static DataSet Compute(CmcDataSet set, NormalisedData data, PadDeembedding pads, SixPinNetwork? network,
                                  IReadOnlyDictionary<string, double>? values, (double Lo, double Hi)? band,
                                  List<string> notes, RunControl? rc = null, Dictionary<string, NetworkBlocks[]>? blocks = null)
    {
        var plane = set.Conditions.ReferencePlane ?? ReferencePlane.Probe;
        bool beforePads = plane is ReferencePlane.Instrument or ReferencePlane.Probe;
        bool throughNetwork = network is not null && plane != ReferencePlane.Intrinsic;

        var ds = new DataSet();
        var point = new Axis("point", [.. Enumerable.Range(0, data.Count).Select(i => (double)i)],
                             labels: [.. data.Points.Select(p => p.PointId)]);

        // Re-referencing (R-mc4-4).
        var padDrop = beforePads ? pads.DcDrop() : null;
        var transfer = throughNetwork ? network!.Dc(values) : DcTransfer.Identity;
        bool padNeedsCurrents = beforePads && pads.DcNeedsCurrents;
        var (vgsi, vdsi) = ReReference(set, data, padDrop, transfer, padNeedsCurrents, notes);
        ds.Add("Vgs", new DataCube([point], [.. data.Points.Select(p => p.Vgs)]));
        ds.Add("Vds", new DataCube([point], [.. data.Points.Select(p => p.Vds)]));
        ds.Add("Ig", new DataCube([point], [.. data.Points.Select(p => p.Ig)]));
        ds.Add("Id", new DataCube([point], [.. data.Points.Select(p => p.Id)]));
        ds.Add("Vgsi", new DataCube([point], vgsi));
        ds.Add("Vdsi", new DataCube([point], vdsi));
        ds.Add("Excluded", new DataCube([point], [.. data.Points.Select(p => p.Excluded ? 1.0 : 0.0)]));
        if (Setting(set, padDrop, transfer, padNeedsCurrents) is { } setting) ds.Add(SettingCube, setting);

        if (data.S is null) return ds;
        if (data.Ports != 2) throw new ModelingException(set.Name, $"it is a {data.Ports}-port; a FET's data is a 2-port.");

        // Pads, then the network (R-mc4-1, R-mc4-3).
        var d = beforePads ? pads.Remove(data, set.Name, rc) : data;
        double[] f = d.FrequenciesHz!;
        int nf = f.Length, np = d.Count;
        NetworkBlocks[]? nb = null;
        if (throughNetwork)
        {
            string axisKey = string.Join(",", f.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
            if (blocks is null || !blocks.TryGetValue(axisKey, out nb))
            {
                nb = network!.Blocks(f, values, rc);
                if (blocks is not null) blocks[axisKey] = nb;
            }
        }

        var y = new Complex[np * nf * 4];
        var cond = new double[np * nf];
        var flagged = new double[np * nf];
        var q = new double[IntrinsicQuantities.Names.Count][];
        for (int i = 0; i < q.Length; i++) q[i] = new double[np * nf];
        var anyFlagged = new bool[nf];
        for (int p = 0; p < np; p++)
        {
            rc?.ThrowIfCancellationRequested();
            for (int k = 0; k < nf; k++)
            {
                int o = (p * nf + k) * 4, c = p * nf + k;
                var yext = NetworkAlgebra.SToY(new C2(d.S![o], d.S[o + 1], d.S[o + 2], d.S[o + 3]), d.Z0);
                C2 yint;
                if (nb is null) { yint = yext; cond[c] = 1; }
                else
                {
                    var r = NetworkAlgebra.Deembed(nb[k].Aa, nb[k].Ab, nb[k].Ba, nb[k].Bb, yext);
                    yint = r.YInt;
                    cond[c] = r.Condition;
                    if (r.Flagged) { flagged[c] = 1; anyFlagged[k] = true; }
                }
                (y[o], y[o + 1], y[o + 2], y[o + 3]) = (yint.A, yint.B, yint.C, yint.D);
                var values11 = IntrinsicQuantities.Of(yint, f[k]);
                for (int i = 0; i < q.Length; i++) q[i][c] = values11[i];
            }
            rc?.TickStage();
        }
        int flaggedFreqs = anyFlagged.Count(b => b);
        if (flaggedFreqs > 0)
            notes.Add($"{set.Name}: {flaggedFreqs} of {nf} frequencies flagged (condition above {NetworkAlgebra.ConditionLimit:G3}); left out of averaging.");

        var freq = new Axis("freq", f, "Hz");
        var ports = new Axis("port", [1, 2]);
        ds.Add("Y", new DataCube([point, freq, ports, ports], y));
        ds.Add("Cond", new DataCube([point, freq], cond));
        ds.Add("Flagged", new DataCube([point, freq], flagged));
        for (int i = 0; i < q.Length; i++)
            ds.Add(IntrinsicQuantities.Names[i], new DataCube([point, freq], q[i]));

        // The flatness metric per point and quantity, over the document's band (R-mc4-5).
        var b = band ?? IntrinsicQuantities.DefaultBand(f);
        var flat = new double[np * q.Length];
        for (int p = 0; p < np; p++)
        {
            var fl = Enumerable.Range(0, nf).Select(k => flagged[p * nf + k] != 0).ToArray();
            for (int i = 0; i < q.Length; i++)
                flat[p * q.Length + i] = IntrinsicQuantities.Flatness(new ArraySegment<double>(q[i], p * nf, nf), f, b, fl);
        }
        var quantity = new Axis("quantity", [.. Enumerable.Range(0, q.Length).Select(i => (double)i)], labels: [.. IntrinsicQuantities.Names]);
        ds.Add("Flatness", new DataCube([point, quantity], flat));
        ds.Add("Band", new DataCube([new Axis("edge", [0, 1])], [b.Lo, b.Hi]));
        return ds;
    }

    /// <summary>The cube of a pulsed set's trap-setting biases (MC-10 R-mc10-4).</summary>
    public const string SettingCube = "Setting";

    /// <summary>The <see cref="SettingCube"/> rows and columns.</summary>
    public static readonly IReadOnlyList<string> SettingBiases = ["Quiescent", "PrePulse"], SettingColumns = ["Vgsi", "Vdsi", "Stated"];

    /// <summary>
    /// A pulsed set's quiescent bias and pre-pulse level re-referenced to intrinsic exactly as its points are (MC-10
    /// R-mc10-4): rows <c>Quiescent</c> and <c>PrePulse</c>, columns <c>Vgsi</c>, <c>Vdsi</c> and <c>Stated</c> — 1 when the
    /// condition states its drain current, 0 when it was taken as zero (the trap step refuses that for a dissipating
    /// bias). NaN for a row the set does not have; null when it has neither.
    /// </summary>
    private static DataCube? Setting(CmcDataSet set, Func<double, double, double, double, (double Vgs, double Vds)>? padDrop,
                                     DcTransfer transfer, bool padNeedsCurrents)
    {
        var c = set.Conditions;
        if (c.Quiescent is null && c.PrePulse is null) return null;
        var values = Enumerable.Repeat(double.NaN, 6).ToArray();
        void Row(int r, double vgs, double vds, double? ig, double? id)
        {
            var one = new NormalisedData { Points = [new BiasPoint(SettingBiases[r], vgs, vds, ig ?? 0, id ?? 0)] };
            var (g, d) = ReReference(set, one, padDrop, transfer, padNeedsCurrents, []);
            (values[3 * r], values[3 * r + 1], values[3 * r + 2]) = (g[0], d[0], id is null ? 0 : 1);
        }
        if (c.Quiescent is { } q) Row(0, q.Vgs, q.Vds, q.Ig, q.Id);
        if (c.PrePulse is { } pp) Row(1, pp.Vgs, pp.Vds, null, pp.Id);
        return new DataCube([new Axis("bias", [0, 1], labels: [.. SettingBiases]), new Axis("column", [0, 1, 2], labels: [.. SettingColumns])],
                            values);
    }

    /// <summary>
    /// R-mc4-4: each point's bias moved from its stated plane to the device plane, then through the network's DC map.
    /// A set at the intrinsic plane is its own answer. A set that carries intrinsic voltages of its own gets the
    /// largest difference reported, in one line.
    /// </summary>
    public static (double[] Vgs, double[] Vds) ReReference(
        CmcDataSet set, NormalisedData data, Func<double, double, double, double, (double Vgs, double Vds)>? padDrop,
        DcTransfer transfer, bool padNeedsCurrents, List<string> notes)
    {
        var c = set.Conditions;
        var plane = c.ReferencePlane ?? ReferencePlane.Probe;
        int n = data.Count;
        double[] vgs = new double[n], vds = new double[n];
        var path = plane == ReferencePlane.Instrument ? c.BiasPathOhms : null;
        bool needs = plane != ReferencePlane.Intrinsic && (transfer.NeedsCurrents || padNeedsCurrents || path is not null);
        bool noIg = false;
        for (int k = 0; k < n; k++)
        {
            var p = data.Points[k];
            if (plane == ReferencePlane.Intrinsic) { vgs[k] = p.Vgs; vds[k] = p.Vds; continue; }
            double ig = p.Ig, id = p.Id;
            if (double.IsNaN(ig)) { ig = 0; noIg |= needs; }
            if (double.IsNaN(id))
            {
                if (needs)
                    throw new ModelingException(set.Name, $"point {p.PointId} has no drain current, and the intrinsic voltages need it " +
                                                          "(series resistance between the stated plane and the core).");
                id = 0;
            }
            double g = p.Vgs, dd = p.Vds;
            if (path is not null)
            {
                double vs = path.Source * (ig + id);
                g = g - path.Gate * ig - vs;
                dd = dd - path.Drain * id - vs;
            }
            if (padDrop is not null) (g, dd) = padDrop(g, dd, ig, id);
            (vgs[k], vds[k]) = transfer.Apply(g, dd, ig, id);
        }
        if (noIg) notes.Add($"{set.Name}: no gate current in the data; taken as 0 A.");

        double worst = double.NaN;
        for (int k = 0; k < n; k++)
        {
            var p = data.Points[k];
            if (double.IsNaN(p.VgsIntrinsic) || double.IsNaN(p.VdsIntrinsic)) continue;
            double dv = Math.Max(Math.Abs(p.VgsIntrinsic - vgs[k]), Math.Abs(p.VdsIntrinsic - vds[k]));
            worst = double.IsNaN(worst) ? dv : Math.Max(worst, dv);
        }
        if (!double.IsNaN(worst))
            notes.Add($"{set.Name}: max |ΔV| {worst.ToString("G3", CultureInfo.InvariantCulture)} V between its intrinsic voltages and the computed ones.");
        return (vgs, vds);
    }

    // ── The cache ─────────────────────────────────────────────────────────────

    /// <summary>Bumped when what an entry holds changes: mc10-1 added the trap-setting biases.</summary>
    private const string KeyVersion = "mc10-1";

    private static string SharedKey(string dir, PadsSettings pads, string? netPath, IReadOnlyDictionary<string, double>? values,
                                     (double Lo, double Hi)? band)
    {
        var sb = new StringBuilder(KeyVersion).Append('\n');
        sb.Append(CmcExtrinsic.ToJson(pads).ToJsonString()).Append('\n');
        foreach (string? src in new[] { pads.Open, pads.Short, pads.Gate, pads.Drain })
            if (src is not null) sb.Append(FileHash(DataImport.ResolveSource(dir, src))).Append('\n');
        if (netPath is not null) sb.Append(FileHash(netPath)).Append('\n');
        if (values is not null)
            foreach (var (k, v) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append(k).Append('=').Append(v.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        if (band is { } b) sb.Append(b.Lo.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(b.Hi.ToString("R", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string FileHash(string path) =>
        File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "missing";

    /// <summary>The set's own part: its conditions and exclusions as the .cmc writes them, and its data's bytes.</summary>
    private static string Key(string shared, CmcDataSet set, NormalisedData data)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.UTF8.GetBytes(shared));
        h.AppendData(CmcFile.ToBytes(new CmcDocument { Kind = CmcKind.Active, DataSets = [set] }));
        using (var m = new MemoryStream())
        {
            DataSetExporter.ExportNpy(data.ToDataSet(), m);
            h.AppendData(m.ToArray());
        }
        return Convert.ToHexStringLower(h.GetHashAndReset())[..32];
    }

    private static DataSet? TryRead(string file)
    {
        try { return DataSetImporter.Import(file).DataSet; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException)
        { return null; }   // a damaged entry is a cache that is not there; it is derived and is recomputed
    }

    /// <summary>Writes each new entry beside its final name and renames it in, then removes entries no set uses now.</summary>
    private static void Write(string folder, IReadOnlyDictionary<string, DataSet> fresh, IReadOnlySet<string> keep)
    {
        Directory.CreateDirectory(folder);
        foreach (var (key, ds) in fresh)
        {
            string target = Path.Combine(folder, key + ".npy");
            string tmp = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                using (var fs = File.Create(tmp)) DataSetExporter.ExportNpy(ds, fs);
                File.Move(tmp, target, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        foreach (string file in Directory.EnumerateFiles(folder, "*.npy"))
            if (!keep.Contains(Path.GetFileNameWithoutExtension(file)))
                try { File.Delete(file); } catch (IOException) { }
    }

    private static void AttachNotes(DataSet ds, IReadOnlyList<string> notes)
    {
        if (notes.Count == 0) return;
        ds.Add("Notes", new DataCube([new Axis("note", [.. Enumerable.Range(0, notes.Count).Select(i => (double)i)], labels: [.. notes])],
                                     new double[notes.Count]));
    }

    private static IEnumerable<string> NotesOf(DataSet ds) =>
        ds.Contains("Notes") ? ds["Notes"].Axes[0].Labels ?? [] : [];
}
