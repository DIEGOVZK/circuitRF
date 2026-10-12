using System.Globalization;
using SkiaSharp;

namespace CircuitRF.SchematicSynth;

/// <summary>
/// One picture of IML-2's random circuits drawn in a style pack (brief-iml3): a <see cref="Look"/> drawn from the pack,
/// the <see cref="Scene"/> built from the drawing(s), the distractors added, a view chosen, every layer rendered into the
/// picture and into its own mask through the same matrix, then the medium's conditions (<see cref="ForeignShot"/>).
///
/// <para><b>The labels are exact for the reason IML-2's are:</b> nothing is read off pixels. A part's body, pins and
/// words are the scene's, through the view matrix — which carries the slight rotation of a scanned page too — then
/// through the shot's offset and rescale. The masks are the same ink drawn white on black, each layer alone,
/// thresholded at a quarter of full coverage.</para>
/// </summary>
public static class ForeignPicture
{
    public static PictureFiles Make(string id, IReadOnlyList<Drawing> drawings, StylePack pack, FontBook fonts, Random r)
    {
        var look = Look.Draw(pack, fonts, r);
        var scene = SceneBuilder.Build(drawings, look, r);
        Distractors.Add(scene, look, r);
        var view = View.Choose(scene, pack, r);

        int pw = view.Width, ph = view.Height;
        double gridPx = 100 * view.Zoom;
        double dpr = view.Dpr;
        var pens = new Dictionary<char, double>
        {
            ['w'] = Math.Max(look.MinPx * dpr, look.WireShare * gridPx),
            ['s'] = Math.Max(look.MinPx * dpr, look.SymbolShare * gridPx),
        };
        pens['a'] = pens['s'];

        using var picture = new SKBitmap(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
        var masks = Enumerable.Range(0, 4).Select(_ => new SKBitmap(new SKImageInfo(pw, ph, SKColorType.Alpha8, SKAlphaType.Premul))).ToArray();
        try
        {
            using (var painter = new Painter(picture, masks, view.Matrix, view.Zoom, pens, look))
            {
                painter.Clear(look.Paper);
                painter.Grid(look, view, dpr);
                foreach (var i in scene.Background) painter.Ink(i);
                foreach (var i in scene.Annotations) painter.Ink(i);
                foreach (var i in scene.Wires) painter.Ink(i);
                foreach (var p in scene.Parts) foreach (var i in p.Inks) painter.Ink(i);
                foreach (var m in scene.Markers) painter.Marker(m);
                foreach (var t in scene.Texts) painter.Text(t);
                foreach (var i in scene.Overlays) painter.Ink(i);
            }

            var raw = masks.Select(Threshold).ToList();
            var shot = ForeignShot.Take(picture, raw, look, view, r);

            Px Map(W w)
            {
                var p = view.Matrix.MapPoint((float)w.X, (float)w.Y);
                return new Px((p.X + shot.OffsetX) * shot.ScaleX, (p.Y + shot.OffsetY) * shot.ScaleY);
            }
            var label = Label(id + shot.Extension, scene, view, shot, Map, pens['w'] * shot.ScaleX, pack.Name);

            string netlist = drawings.Count == 1 ? Netlists.Text(drawings[0].Truth) : Merge(drawings);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [id + ".wire.png"] = shot.Masks[0], [id + ".symbol.png"] = shot.Masks[1],
                [id + ".text.png"] = shot.Masks[2], [id + ".annotation.png"] = shot.Masks[3],
            };
            return new PictureFiles(id, id + shot.Extension, shot.Image, files, label, netlist, [.. label.Parts.Select(p => p.Class)])
            {
                SymbolMask = shot.RawMasks[1],
                Drawing = drawings[0],
                Drawings = drawings,
                Conditions = $"{pack.Name},{string.Join("+", drawings.Select(d => d.Mode.ToString().ToLowerInvariant()))},{view.Mode},{shot.Conditions}",
            };
        }
        finally
        {
            foreach (var m in masks) m.Dispose();
        }
    }

    /// <summary>A mask pixel is on at a quarter of full coverage — the anti-aliased fringe of a thin line included.</summary>
    private static byte[] Threshold(SKBitmap alpha)
    {
        var px = alpha.GetPixelSpan();
        var o = new byte[alpha.Width * alpha.Height];
        int stride = alpha.RowBytes;
        for (int y = 0; y < alpha.Height; y++)
            for (int x = 0; x < alpha.Width; x++)
                o[y * alpha.Width + x] = px[y * stride + x] >= 64 ? (byte)255 : (byte)0;
        return o;
    }

    // ── the label ────────────────────────────────────────────────────────────

    private static MlLabel Label(string imageName, Scene scene, View view, Shot shot, Func<W, Px> map, double pen, string style)
    {
        double[] cv = [shot.OffsetX * shot.ScaleX, shot.OffsetY * shot.ScaleY, (shot.OffsetX + view.Width) * shot.ScaleX, (shot.OffsetY + view.Height) * shot.ScaleY];
        bool Inside(Px p) => p.X >= cv[0] && p.X <= cv[2] && p.Y >= cv[1] && p.Y <= cv[3];
        double[] BoxOf((double X0, double Y0, double X1, double Y1) b) =>
            Labels.Box([map(new W(b.X0, b.Y0)), map(new W(b.X1, b.Y0)), map(new W(b.X1, b.Y1)), map(new W(b.X0, b.Y1))]);

        var texts = new List<LabelText>();
        var ignore = new List<double[]>();
        foreach (var t in scene.Texts)
        {
            t.Id = "";
            if (t.Unlabelled) continue;
            Px[] quad = [.. t.Quad().Select(map)];
            int inside = quad.Count(Inside);
            if (inside < 4)
            {
                var b = Labels.Box(quad);
                if (inside > 0 || Labels.Overlaps(b, cv)) ignore.Add(Labels.Clip(b, cv));
                continue;
            }
            t.Id = "t" + (texts.Count + 1).ToString(CultureInfo.InvariantCulture);
            texts.Add(new LabelText(t.Id, quad, t.String, t.Role));
        }

        var parts = new List<LabelPart>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in scene.Parts)
        {
            double[] box = BoxOf(p.Body);
            var pins = p.Pins.Select(q => new LabelPin(q.Name, map(q.At))).ToList();
            if (!Labels.Overlaps(box, cv) && !pins.Any(q => Inside(q.At))) continue;
            bool cut = !(box[0] >= cv[0] && box[1] >= cv[1] && box[2] <= cv[2] && box[3] <= cv[3])
                       || pins.Any(q => !Inside(q.At) || q.At.X > shot.Width - 1 || q.At.Y > shot.Height - 1);
            string pid = ids.Add(p.Id) ? p.Id : p.Id + "." + parts.Count.ToString(CultureInfo.InvariantCulture);
            ids.Add(pid);
            string? des = p.Designator is { Id.Length: > 0 } d ? d.Id : null;
            parts.Add(new LabelPart(pid, p.Class, p.Variant, p.Turn, p.Mirror, box, pins, des,
                                    [.. p.Values.Where(v => v.Id.Length > 0).Select(v => v.Id)], cut)
            { Params = p.Params.Count > 0 ? p.Params : null });
        }

        var wires = new List<LabelWire>();
        foreach (var w in scene.WirePaths)
            foreach (var piece in Labels.ClipPolyline([.. w.Select(map)], cv)) wires.Add(new LabelWire(piece, pen));
        var junctions = scene.Junctions.Select(map).Where(Inside).ToList();
        var crossings = scene.Crossings.Select(map).Where(Inside).ToList();

        var annotations = new List<LabelAnnotation>();
        foreach (var a in scene.AnnotationBoxes)
        {
            var b = BoxOf(a.Box);
            if (!Labels.Overlaps(b, cv)) continue;
            var clipped = Labels.Clip(b, cv);
            if (clipped[2] - clipped[0] < 1 || clipped[3] - clipped[1] < 1) continue;
            annotations.Add(new LabelAnnotation(clipped, a.Kind));
        }
        annotations.AddRange(shot.Chrome.Select(b => new LabelAnnotation(b, "chrome")));

        string stem = Path.GetFileNameWithoutExtension(imageName);
        var maskNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["wire"] = stem + ".wire.png", ["symbol"] = stem + ".symbol.png",
            ["text"] = stem + ".text.png", ["annotation"] = stem + ".annotation.png",
        };
        return new MlLabel(imageName, shot.Width, shot.Height, style, parts, wires, junctions, crossings, texts, annotations, ignore, maskNames);
    }

    /// <summary>Two circuits' truths in one netlist: the second's instances and nets carry a <c>b_</c> prefix so the two
    /// stay two circuits (they share only ground).</summary>
    private static string Merge(IReadOnlyList<Drawing> drawings)
    {
        var sb = new System.Text.StringBuilder();
        for (int k = 0; k < drawings.Count; k++)
        {
            string text = Netlists.Text(drawings[k].Truth);
            if (k == 0) { sb.Append(text); continue; }
            foreach (string line in text.Split('\n'))
            {
                var tokens = line.Split(' ');
                int colon = tokens[0].IndexOf(':');
                if (colon <= 0 || line.StartsWith('#') || line.StartsWith('*')) { if (line.Length > 0 && !line.Contains(" = ")) sb.Append(line).Append('\n'); continue; }
                tokens[0] = tokens[0][..(colon + 1)] + "b_" + tokens[0][(colon + 1)..];
                for (int i = 1; i < tokens.Length && !tokens[i].Contains('='); i++)
                    if (tokens[i] != "0" && tokens[i].Length > 0) tokens[i] = "b_" + tokens[i];
                sb.Append(string.Join(' ', tokens)).Append('\n');
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// How the scene is framed (brief-iml3 R-iml3-4): the whole circuit with a margin most of the time; a window over part
/// of it as a screenshot crops one; a SMALL picture (the whole circuit in 240–420 px) and a VERY LARGE one (up to
/// 7,000 px wide) now and then; a page turned up to 2° as a scan of print is; Retina 2× on screens.
/// </summary>
public sealed class View
{
    public required SKMatrix Matrix { get; init; }
    public double Zoom { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Dpr { get; init; } = 1;
    public double Rotation { get; init; }
    public string Mode { get; init; } = "whole";

    private const double ResistorBody = 180;

    public static View Choose(Scene scene, StylePack pack, Random r)
    {
        var e = scene.Extent();
        double ew = Math.Max(100, e.X1 - e.X0), eh = Math.Max(100, e.Y1 - e.Y0);
        bool paper = pack.Medium == "paper", screen = pack.Medium == "screen";
        double dpr = !paper && r.NextDouble() < 0.35 ? 2 : 1;
        double rot = paper && r.NextDouble() < 0.4 ? (r.NextDouble() * 2 - 1) * 2.0 : 0;

        double LogUniform(double lo, double hi) => Math.Exp(Math.Log(lo) + r.NextDouble() * (Math.Log(hi) - Math.Log(lo)));
        double x = r.NextDouble();
        // A screenshot is often a window over part of a drawing; a figure in a document shows the whole circuit.
        string mode = x < 0.08 ? "small" : x < 0.11 ? "large" : x < 0.30 && screen ? "window" : "whole";
        double zoom = mode switch
        {
            "small" => LogUniform(240, 420) / ew,
            "large" => LogUniform(220, 420) / ResistorBody,
            "window" => LogUniform(60, 300) / ResistorBody,
            _ => LogUniform(12, 90) / ResistorBody,
        } * (mode is "small" ? 1 : dpr);

        double cx = (e.X0 + e.X1) / 2, cy = (e.Y0 + e.Y1) / 2;
        int w = 0, h = 0;
        if (mode == "window")
        {
            w = (int)(r.Next(480, 1401) * dpr);
            h = (int)(r.Next(360, 1001) * dpr);
            if (ew * zoom < w && eh * zoom < h) mode = "whole";
            else if (scene.Parts.Count > 0)
            {
                // Centred near a part, so a close-up shows a part and not the canvas between two.
                var part = scene.Parts[r.Next(scene.Parts.Count)];
                cx = (part.Body.X0 + part.Body.X1) / 2 + (r.NextDouble() - 0.5) * w / zoom * 0.5;
                cy = (part.Body.Y0 + part.Body.Y1) / 2 + (r.NextDouble() - 0.5) * h / zoom * 0.5;
            }
        }
        if (mode != "window")
        {
            double t = Math.Abs(rot) * Math.PI / 180;
            double rw = ew * Math.Cos(t) + eh * Math.Sin(t), rh = ew * Math.Sin(t) + eh * Math.Cos(t);
            // Never past 7,000 × 5,000, the largest pictures in the starting set — and only a "large" picture comes near
            // it; the rest stay within what a screenshot or a figure usually is.
            double capW = mode == "large" ? 6800 : 2600, capH = mode == "large" ? 4800 : 1900;
            zoom = Math.Min(zoom, Math.Min(capW / rw, capH / rh));
            int mx = (int)(r.Next(6, 90) * dpr), my = (int)(r.Next(6, 70) * dpr);
            if (mode == "small") { mx = r.Next(2, 12); my = r.Next(2, 10); }
            w = Math.Max(120, (int)Math.Ceiling(rw * zoom) + 2 * mx);
            h = Math.Max(90, (int)Math.Ceiling(rh * zoom) + 2 * my);
        }

        var m = SKMatrix.CreateTranslation(-(float)cx, -(float)cy);
        m = m.PostConcat(SKMatrix.CreateScale((float)zoom, (float)zoom));
        if (rot != 0) m = m.PostConcat(SKMatrix.CreateRotationDegrees((float)rot));
        m = m.PostConcat(SKMatrix.CreateTranslation(w / 2f, h / 2f));
        return new View { Matrix = m, Zoom = zoom, Width = w, Height = h, Dpr = dpr, Rotation = rot, Mode = mode + (dpr > 1 ? "-2x" : "") + (rot != 0 ? "-turned" : "") };
    }
}

/// <summary>Draws scene ink into the picture and the same ink, white, into its layer's mask.</summary>
internal sealed class Painter : IDisposable
{
    private readonly SKCanvas _picture;
    private readonly SKCanvas[] _masks;
    private readonly double _zoom;
    private readonly Dictionary<char, double> _pens;
    private readonly Look _look;

    public Painter(SKBitmap picture, SKBitmap[] masks, SKMatrix matrix, double zoom, Dictionary<char, double> pens, Look look)
    {
        _picture = new SKCanvas(picture);
        _masks = [.. masks.Select(m => new SKCanvas(m))];
        foreach (var c in _masks.Append(_picture)) c.SetMatrix(matrix);
        _zoom = zoom;
        _pens = pens;
        _look = look;
    }

    public void Dispose()
    {
        _picture.Dispose();
        foreach (var m in _masks) m.Dispose();
    }

    public void Clear(SKColor paper)
    {
        _picture.Clear(paper);
        foreach (var m in _masks) m.Clear(SKColors.Transparent);
    }

    /// <summary>The canvas grid, dots or lines, background only — no mask.</summary>
    public void Grid(Look look, View view, double dpr)
    {
        if (look.Grid == "none") return;
        double pitchPx = look.GridPitch * view.Zoom;
        if (pitchPx < 5) return;
        if (!view.Matrix.TryInvert(out var inv)) return;
        var corners = new[] { inv.MapPoint(0, 0), inv.MapPoint(view.Width, 0), inv.MapPoint(view.Width, view.Height), inv.MapPoint(0, view.Height) };
        double x0 = Math.Floor(corners.Min(p => p.X) / look.GridPitch) * look.GridPitch, x1 = corners.Max(p => p.X);
        double y0 = Math.Floor(corners.Min(p => p.Y) / look.GridPitch) * look.GridPitch, y1 = corners.Max(p => p.Y);
        var colour = look.GridColour;
        using var paint = new SKPaint { Color = colour, IsAntialias = look.Antialias, StrokeWidth = (float)(Math.Max(1, dpr) / _zoom), Style = SKPaintStyle.Stroke };
        if (look.Grid == "lines")
        {
            paint.Color = colour.WithAlpha(90);
            for (double x = x0; x <= x1; x += look.GridPitch) _picture.DrawLine((float)x, (float)y0, (float)x, (float)y1, paint);
            for (double y = y0; y <= y1; y += look.GridPitch) _picture.DrawLine((float)x0, (float)y, (float)x1, (float)y, paint);
            return;
        }
        paint.Style = SKPaintStyle.Fill;
        float s = (float)(Math.Max(1, dpr) / _zoom);
        for (double x = x0; x <= x1; x += look.GridPitch)
            for (double y = y0; y <= y1; y += look.GridPitch)
                _picture.DrawRect((float)x - s / 2, (float)y - s / 2, s, s, paint);
    }

    private SKPaint Paint(SKColor colour, bool fill, double penPx, float[]? dash, bool antialias)
    {
        var p = new SKPaint
        {
            Color = colour, IsAntialias = antialias, Style = fill ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
            StrokeWidth = (float)(penPx / _zoom), StrokeCap = _look.Cap, StrokeJoin = SKStrokeJoin.Round,
        };
        if (dash is { Length: >= 2 } && !fill) p.PathEffect = SKPathEffect.CreateDash(dash, 0);
        return p;
    }

    public void Ink(Ink ink)
    {
        if (ink.Points.Count < 2) return;
        using var path = new SKPath();
        path.MoveTo((float)ink.Points[0].X, (float)ink.Points[0].Y);
        for (int i = 1; i < ink.Points.Count; i++) path.LineTo((float)ink.Points[i].X, (float)ink.Points[i].Y);
        if (ink.Closed) path.Close();
        double pen = ink.Pen * _pens[ink.PenOf];
        void Draw(SKCanvas c, SKColor colour, bool aa)
        {
            if (ink.Fill) { using var f = Paint(colour, true, 0, null, aa); c.DrawPath(path, f); }
            if (ink.Pen > 0) { using var s = Paint(colour, false, pen, ink.Dash, aa); c.DrawPath(path, s); }
        }
        Draw(_picture, ink.Colour, _look.Antialias);
        if (ink.Layer != Layer.None && ink.Colour.Alpha > 128 && !(ink.Layer == Layer.Symbol && ink.Colour == _look.Paper))
            Draw(_masks[(int)ink.Layer], SKColors.White, true);
    }

    public void Marker(Marker m)
    {
        double size = Math.Max(m.MinPx, m.World * _zoom) / _zoom, h = size / 2;
        using var path = new SKPath();
        switch (m.Shape)
        {
            case "square": path.AddRect(new SKRect((float)(m.At.X - h), (float)(m.At.Y - h), (float)(m.At.X + h), (float)(m.At.Y + h))); break;
            case "diamond":
                path.MoveTo((float)m.At.X, (float)(m.At.Y - h)); path.LineTo((float)(m.At.X + h), (float)m.At.Y);
                path.LineTo((float)m.At.X, (float)(m.At.Y + h)); path.LineTo((float)(m.At.X - h), (float)m.At.Y); path.Close();
                break;
            default: path.AddCircle((float)m.At.X, (float)m.At.Y, (float)h); break;
        }
        void Draw(SKCanvas c, SKColor colour, bool aa)
        {
            using var p = Paint(colour, m.Fill, Math.Max(1, _pens['s'] * 0.7), null, aa);
            c.DrawPath(path, p);
        }
        Draw(_picture, m.Colour, _look.Antialias);
        Draw(_masks[(int)m.Layer], SKColors.White, true);
    }

    public void Text(SceneText t)
    {
        void Draw(SKCanvas c, SKColor colour, bool aa)
        {
            c.Save();
            c.Translate((float)t.Origin.X, (float)t.Origin.Y);
            if (t.Angle != 0) c.RotateDegrees((float)-t.Angle);
            using var paint = new SKPaint { Color = colour, IsAntialias = aa };
            using var font = t.Font(t.Size);
            font.Edging = aa ? SKFontEdging.Antialias : SKFontEdging.Alias;
            if (t.SubscriptFrom <= 0 || t.SubscriptFrom >= t.String.Length)
                c.DrawText(t.String, 0, 0, SKTextAlign.Left, font, paint);
            else
            {
                string main = t.String[..t.SubscriptFrom];
                c.DrawText(main, 0, 0, SKTextAlign.Left, font, paint);
                using var sub = t.Font(t.Size * SceneText.SubScale);
                sub.Edging = font.Edging;
                c.DrawText(t.String[t.SubscriptFrom..], font.MeasureText(main), (float)(t.Size * SceneText.SubDrop), SKTextAlign.Left, sub, paint);
            }
            c.Restore();
        }
        Draw(_picture, t.Colour, _look.Antialias);
        var layer = t.Unlabelled ? Layer.Annotation : t.Layer;
        Draw(_masks[(int)layer], SKColors.White, true);
    }
}
