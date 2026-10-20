// The one form every import lands in (brief-modelcomposer-3 R-mc3-2): per bias point the terminal voltages
// and currents at the data set's stated plane, and for S kinds the S-matrix on its frequency axis with Z0.
// A quantity a source does not carry is NaN, never zero — a zero gate current is a measurement.
//
// It round-trips through a DataSet so the cache can store it as circuitRF's own .npy (CmcCache).

using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Design.Modeling;

/// <summary>
/// One bias point. <see cref="PointId"/> is stable across re-imports of an unchanged source, which is what lets a
/// user's exclusion (stored in the <c>.cmc</c>) find its point again. Currents in amps, voltages in volts.
/// </summary>
public sealed record BiasPoint(string PointId, double Vgs, double Vds, double Ig, double Id)
{
    public double AmbientC { get; init; } = double.NaN;
    /// <summary>Intrinsic voltages a simulated table carries beside its terminal ones (D6 cross-check).</summary>
    public double VgsIntrinsic { get; init; } = double.NaN;
    public double VdsIntrinsic { get; init; } = double.NaN;
    /// <summary>A passive block's geometry value (<c>turns</c>, a length in metres) from its source's name or comments
    /// (MC-12); NaN elsewhere.</summary>
    public double Geometry { get; init; } = double.NaN;
    public bool Excluded { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>A data set's normalised data (R-mc3-2).</summary>
public sealed class NormalisedData
{
    public required IReadOnlyList<BiasPoint> Points { get; init; }
    /// <summary>The port count of <see cref="S"/> and <see cref="C"/> — 2 for a FET.</summary>
    public int Ports { get; init; } = 2;
    public double[]? FrequenciesHz { get; init; }
    /// <summary>[point, frequency, i, j], C order; null when the data set has none.</summary>
    public Complex[]? S { get; init; }
    public double Z0 { get; init; } = 50;
    /// <summary>A capacitance table, [point, i, j] in farads; null when there is none.</summary>
    public double[]? C { get; init; }

    public int Count => Points.Count;

    public Complex SAt(int point, int freq, int i, int j)
    {
        int nf = FrequenciesHz!.Length, n = Ports;
        return S![((point * nf + freq) * n + i) * n + j];
    }

    /// <summary>The same data with the user's exclusions applied by point id; an id that names no point is ignored.</summary>
    public NormalisedData WithExclusions(IReadOnlyList<PointExclusion> exclusions)
    {
        var map = exclusions.ToDictionary(x => x.Point, x => x.Reason, StringComparer.Ordinal);
        return new NormalisedData
        {
            Points = [.. Points.Select(p => map.TryGetValue(p.PointId, out var r)
                                             ? p with { Excluded = true, Reason = r }
                                             : p with { Excluded = false, Reason = "" })],
            Ports = Ports, FrequenciesHz = FrequenciesHz, S = S, Z0 = Z0, C = C,
        };
    }

    // ── DataSet form (the cache) ──────────────────────────────────────────────

    private static readonly (string Name, Func<BiasPoint, double> Get)[] Columns =
    [
        ("Vgs", p => p.Vgs), ("Vds", p => p.Vds), ("Ig", p => p.Ig), ("Id", p => p.Id),
        ("T", p => p.AmbientC), ("Vgsi", p => p.VgsIntrinsic), ("Vdsi", p => p.VdsIntrinsic), ("G", p => p.Geometry),
    ];

    public DataSet ToDataSet()
    {
        var ds = new DataSet();
        var point = PointAxis();
        foreach (var (name, get) in Columns)
            ds.Add(name, new DataCube([point], [.. Points.Select(get)]));
        ds.Add("Z0", new DataCube([new Axis("index", [0])], [Z0]));
        var ports = new Axis("port", [.. Enumerable.Range(1, Ports).Select(i => (double)i)]);
        if (S is not null)
            ds.Add("S", new DataCube([point, new Axis("freq", FrequenciesHz!, "Hz"), ports, ports], S));
        if (C is not null)
            ds.Add("C", new DataCube([point, ports, ports], C));
        return ds;
    }

    private Axis PointAxis() =>
        new("point", [.. Enumerable.Range(0, Points.Count).Select(i => (double)i)], labels: [.. Points.Select(p => p.PointId)]);

    public static NormalisedData FromDataSet(DataSet ds)
    {
        var vgs = ds["Vgs"];
        string[] ids = vgs.Axes[0].Labels ?? throw new InvalidDataException("The cached data has no point ids.");
        // A column a cache written before it existed lacks reads as NaN — what the source did not carry.
        var col = Columns.ToDictionary(c => c.Name, c => ds.Contains(c.Name) ? ds[c.Name].RealValues
                                                                             : [.. Enumerable.Repeat(double.NaN, ids.Length)]);
        var points = new BiasPoint[ids.Length];
        for (int k = 0; k < ids.Length; k++)
            points[k] = new BiasPoint(ids[k], col["Vgs"][k], col["Vds"][k], col["Ig"][k], col["Id"][k])
            {
                AmbientC = col["T"][k], VgsIntrinsic = col["Vgsi"][k], VdsIntrinsic = col["Vdsi"][k], Geometry = col["G"][k],
            };
        var s = ds.Contains("S") ? ds["S"] : null;
        var c = ds.Contains("C") ? ds["C"] : null;
        int ports = s?.Axes[2].Length ?? c?.Axes[1].Length ?? 2;
        return new NormalisedData
        {
            Points = points,
            Ports = ports,
            FrequenciesHz = s?.Axes[1].Values,
            S = s?.ComplexValues,
            Z0 = ds["Z0"].RealValues[0],
            C = c?.RealValues,
        };
    }
}
