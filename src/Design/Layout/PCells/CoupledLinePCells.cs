namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// The artwork of the three physical coupled pairs — MCLIN (edge-coupled microstrip), BCLIN (broadside
/// stripline) and MBCLIN (broadside microstrip). Each is two straight strips of width <c>W</c> and length
/// <c>L</c> (SI metres, R-pc-6) running to +X, with a pin at every strip end in the coupled-pair order
/// the models read: 1 = strip A's start (the cell origin, R4), 2 = strip A's end, 3 = strip B's start
/// (beside 1), 4 = strip B's end (beside 2). Strip A is the upper one (+Y) as on the symbol.
///
/// <para><b>MCLIN</b> draws both strips on the signal layer (MLIN's resolution), strip B a gap <c>S</c>
/// below strip A. <b>BCLIN/MBCLIN</b> draw strip A on <c>LayerA</c> and strip B on <c>LayerB</c> — the
/// conductors <see cref="SubstrateResolver.ResolveBroadsidePair"/> chooses, the same call the schematic's
/// model is resolved by, so the drawn pair and the simulated one are on the same layers — with strip B's
/// centre <c>O</c> below strip A's. With no technology they fall back to layers (1,0) and (2,0) and say so.</para>
///
/// <para>Handles: <c>L</c> on both ends of strip A (each anchored on the other, as MLIN's), <c>W</c> on
/// strip A's top edge and strip B's bottom edge (each anchored on that strip's other edge), and the
/// coupling dimension — <c>S</c> on strip B's top edge measured from strip A's bottom edge, or <c>O</c> on
/// strip B's centre line measured from strip A's.</para>
/// </summary>
public static class CoupledLinePCells
{
    public const string McLinId = "MCLIN";
    public const string BcLinId = "BCLIN";
    public const string MbcLinId = "MBCLIN";

    private static readonly LayerKey FallbackA = new(1, 0), FallbackB = new(2, 0);

    public static PCellResult GenerateMclin(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology,
                                            PCellLayerSelection layerSelection)
    {
        int dbu = LayoutUnits.DefaultDbuPerMicron;
        long w = PCellUnits.MetresToDbu(parameters.Real("W", 0.0), dbu);
        long s = PCellUnits.MetresToDbu(parameters.Real("S", 0.0), dbu);
        long l = PCellUnits.MetresToDbu(parameters.Real("L", 0.0), dbu);
        var layer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);
        long yb = -(w + s);                                     // strip B's centre line
        return Build(layer, layer, w, l, yb, coupling: new PCellHandle("S", l / 2, -w / 2, l / 2, -w / 2 - s,
            AxisDeg: 270, KeepAnchorFixed: true, Quantity: PCellHandleQuantity.Length, Min: 0.0), notes: null);
    }

    public static PCellResult GenerateBclin(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology,
                                            PCellLayerSelection layerSelection)
        => Broadside(parameters, technology, stripline: true);

    public static PCellResult GenerateMbclin(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology,
                                             PCellLayerSelection layerSelection)
        => Broadside(parameters, technology, stripline: false);

    /// <summary>The drawing layers a broadside pair's two strips go on, and why when they are fallbacks.</summary>
    public static (LayerKey A, LayerKey B, IReadOnlyList<string> Notes) BroadsideLayers(
        IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, bool stripline)
    {
        string? Name(string key) => parameters.Text(key) is { Length: > 0 } t ? t.Trim() : null;
        var (pair, failure, warnings) = SubstrateResolver.ResolveBroadsidePair(
            technology, Name("LayerA"), Name("LayerB"), stripline);
        var notes = new List<string>(warnings);
        if (pair is null)
        {
            notes.Add($"{failure?.Reason ?? "no stackup resolved"} — strip A drawn on layer {FallbackA.Layer}/{FallbackA.Datatype}, " +
                      $"strip B on {FallbackB.Layer}/{FallbackB.Datatype}");
            return (FallbackA, FallbackB, notes);
        }
        var a = pair.A.DrawingLayers.Count > 0 ? pair.A.DrawingLayers[0] : FallbackA;
        var b = pair.B.DrawingLayers.Count > 0 ? pair.B.DrawingLayers[0] : FallbackB;
        return (a, b, notes);
    }

    private static PCellResult Broadside(IReadOnlyDictionary<string, PCellValue> parameters, Technology? technology, bool stripline)
    {
        int dbu = LayoutUnits.DefaultDbuPerMicron;
        long w = PCellUnits.MetresToDbu(parameters.Real("W", 0.0), dbu);
        long o = PCellUnits.MetresToDbu(parameters.Real("O", 0.0), dbu);
        long l = PCellUnits.MetresToDbu(parameters.Real("L", 0.0), dbu);
        var (a, b, notes) = BroadsideLayers(parameters, technology, stripline);
        return Build(a, b, w, l, -o, coupling: new PCellHandle("O", l / 2, 0, l / 2, -o,
            AxisDeg: 270, KeepAnchorFixed: true, Quantity: PCellHandleQuantity.Length, Min: 0.0), notes);
    }

    /// <summary>Strip A centred on y = 0 on <paramref name="layerA"/>, strip B centred on
    /// <paramref name="yb"/> on <paramref name="layerB"/>, both from x = 0 to <paramref name="l"/>.</summary>
    private static PCellResult Build(LayerKey layerA, LayerKey layerB, long w, long l, long yb, PCellHandle coupling,
                                     IReadOnlyList<string>? notes)
    {
        long hw = w / 2;
        var shapes = new LayoutShape[]
        {
            new RectShape { Layer = layerA, X1 = 0, Y1 = -hw, X2 = l, Y2 = w - hw },
            new RectShape { Layer = layerB, X1 = 0, Y1 = yb - hw, X2 = l, Y2 = yb + w - hw },
        };
        var pins = new[]
        {
            new PCellPin("1", 0, 0, layerA, w, 180.0),
            new PCellPin("2", l, 0, layerA, w, 0.0),
            new PCellPin("3", 0, yb, layerB, w, 180.0),
            new PCellPin("4", l, yb, layerB, w, 0.0),
        };
        long mid = l / 2;
        const PCellHandleQuantity len = PCellHandleQuantity.Length;
        var handles = new[]
        {
            new PCellHandle("L", 0, 0, l, 0, AxisDeg: 0, KeepAnchorFixed: true, Quantity: len),
            new PCellHandle("L", l, 0, 0, 0, AxisDeg: 180, KeepAnchorFixed: true, Quantity: len),
            new PCellHandle("W", mid, -hw, mid, w - hw, AxisDeg: 90, KeepAnchorFixed: true, Quantity: len),
            new PCellHandle("W", mid, yb + w - hw, mid, yb - hw, AxisDeg: 270, KeepAnchorFixed: true, Quantity: len),
            coupling,
        };
        return new PCellResult(shapes, pins, Diagnostics: notes is { Count: > 0 } ? notes : null, Handles: handles);
    }
}
