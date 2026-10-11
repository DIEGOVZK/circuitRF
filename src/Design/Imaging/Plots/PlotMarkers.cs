// Legends and markers — brief-img-14-sparameter-plots-to-touchstone.md R-im14-7.
//
// A LEGEND is a column of short line samples, each beside a word. A sample is found as a short horizontal run of one
// colour ending just left of a word and centred on it; its colour and whether it is broken (dashed or dotted) are what
// it is matched to a trace by, and the word is read through PlotQuantity's grammar. The legend's box is handed to the
// trace pass so its samples are not read as curves.
//
// A MARKER is a name (m1, M2, Mkr3) and, where the tool drew one, a readout: a block of words under the name saying
// `freq=2.4 GHz` and often the value itself (`dB(S(2,1))=-0.512`). A marker NAME standing on its own near a curve is the
// glyph's label; the glyph is the dark mark just under it, and its lowest pixel is the point marked. Name and readout
// are joined by name. A readout's frequency is the strongest frequency evidence a Smith trace can have (R-im14-8); its
// value, where read, is an exact data point on a rectangular trace.

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Design.Schematic.Recognition;

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>One legend entry: a line sample and its word.</summary>
public sealed record LegendEntry(int Rgb, TraceStyle Style, string Text, PixelBox Sample, PixelBox Word);

/// <summary>A marker read off a plot (R-im14-7).</summary>
/// <param name="X">The point marked, pixels; NaN until placed (a readout whose glyph's label was not read is placed by
/// its own complex value, once the chart is calibrated).</param>
/// <param name="FrequencyHz">The readout's frequency, or null when none was read.</param>
/// <param name="Value">The readout's value of the plotted quantity, in the plot's own units, or null.</param>
/// <param name="Complex">The readout's complex value (<c>S(1,1)=0.9161∠-38.32°</c>), or null.</param>
/// <param name="Readout">The readout's words, as read.</param>
public sealed record PlotMarker(int Plot, string Name, double X, double Y, double? FrequencyHz, double? Value, string Readout,
                                System.Numerics.Complex? Complex = null)
{
    /// <summary>The trace it sits on (the session's trace number), once matched; 0 when none is near.</summary>
    public int Trace { get; init; }

    public bool Placed => !double.IsNaN(X) && !double.IsNaN(Y);
}

public static class PlotMarkers
{
    private static readonly Regex NameShape = new(@"^(m|M|Mkr|MKR|mkr)\s*([0-9]{1,2})$", RegexOptions.CultureInvariant);
    // The key is read forgivingly: a typeset f and r at a small size come back as 1 and a (freq as 1raq).
    // The key is read forgivingly: a typeset f and r at a small size come back as 1 and a (freq as 1raq), and the key may
    // be lost altogether — a value with a frequency unit after an = is a frequency. A G reads as O, C or 6.
    private static readonly Regex FreqShape = new(
        @"(?:(?:[A-Za-z1|]{1,4}[qQ]|[Ff])\s*=\s*([-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*([TGMkKOC]?)\s*(Hz|HZ|hz)?"
        + @"|=\s*([-+]?[0-9]*\.?[0-9]+)\s*([TGMkKOC]?)\s*(Hz|HZ|hz))", RegexOptions.CultureInvariant);
    private static readonly Regex ValueShape = new(@"\)\s*=\s*([-+]?[0-9]*\.?[0-9]+)", RegexOptions.CultureInvariant);

    /// <summary>A marker's name, or null.</summary>
    public static string? Name(string text)
    {
        var m = NameShape.Match(text.Trim());
        return m.Success ? "m" + m.Groups[2].Value : null;
    }

    /// <summary>A readout's frequency, hertz, or null.</summary>
    public static double? Frequency(string text)
    {
        var m = FreqShape.Match(Digits(text.Replace(" ", "")));
        if (!m.Success) return null;
        int g = m.Groups[1].Success ? 1 : 4;
        if (!double.TryParse(m.Groups[g].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        double scale = m.Groups[g + 1].Value switch { "T" => 1e12, "G" or "O" or "C" => 1e9, "M" => 1e6, "k" or "K" => 1e3, _ => 1 };
        // A readout without a multiplier or a unit says nothing about its unit; GHz is how plots of RF parts read.
        if (m.Groups[g + 1].Value.Length == 0 && !m.Groups[g + 2].Success) scale = 1e9;
        return v * scale;
    }

    /// <summary>A number's letters read back as the digits they are: an O, S, Z or l touching a digit or a decimal point
    /// (<c>O.6GHz</c> is 0.6 GHz). A letter with no digit beside it is left alone — the G of GHz, the m of a name.</summary>
    internal static string Digits(string text) =>
        NumberLetter.Replace(text, m => m.Value switch { "O" or "o" => "0", "S" => "5", "Z" => "2", _ => "1" });

    private static readonly Regex NumberLetter = new(@"(?<=[=0-9.]|^)[OoSZl](?=[0-9.])|(?<=[0-9]\.?)[OoSZl](?![a-zA-Z])",
                                                     RegexOptions.CultureInvariant);

    /// <summary>A readout's plotted value (<c>dB(S(2,1))=-0.512</c>), or null.</summary>
    public static double? Value(string text)
    {
        var m = ValueShape.Match(text.Replace(" ", ""));
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    /// <summary>The legend entries in or near <paramref name="frame"/>: a word with a short line sample just left of it.</summary>
    public static IReadOnlyList<LegendEntry> Legend(PlotInk ink, PlotFrame frame, IReadOnlyList<PlotWord> words)
    {
        var result = new List<LegendEntry>();
        double size = frame.IsChart ? 2 * frame.Radius : Math.Max(frame.Right - frame.Left, frame.Bottom - frame.Top);
        foreach (var w in words)
        {
            if (!w.Read || w.Vertical || w.Number is not null) continue;
            if (w.Box.CentreX < frame.Left - 0.2 * size || w.Box.CentreX > frame.Right + 0.4 * size
                || w.Box.CentreY < frame.Top - 0.2 * size || w.Box.CentreY > frame.Bottom + 0.2 * size) continue;
            int h = w.Height;
            int cy = (int)Math.Round(w.Box.CentreY - 0.5);
            // The sample: the longest stretch of one non-background colour ending within 2h left of the word, on a row
            // within h/3 of its centre.
            (int From, int To, int Row, int Rgb, int Breaks)? best = null;
            for (int y = cy - Math.Max(1, h / 3); y <= cy + Math.Max(1, h / 3); y++)
            {
                int end = -1;
                for (int x = w.Box.Left - 1; x >= Math.Max(0, w.Box.Left - 2 * h); x--)
                    if (ink.At(x, y)) { end = x; break; }
                if (end < 0) continue;
                int rgb = ink.Raster.Rgb(end, y);
                int from = end, breaks = 0, gap = 0;
                for (int x = end - 1; x >= Math.Max(0, end - 10 * h); x--)
                {
                    if (ink.At(x, y) && PlotInk.Distance(ink.Raster.Rgb(x, y), rgb) < 64) { from = x; if (gap > 0) breaks++; gap = 0; }
                    else if (++gap > Math.Max(3, h)) break;
                }
                if (end - from + 1 < 1.2 * h || end - from + 1 > 9 * h) continue;
                // Thin: no ink of that colour two rows above or below along most of it.
                int thick = 0;
                for (int x = from; x <= end; x++)
                    if (ink.At(x, y - 3) || ink.At(x, y + 3)) thick++;
                if (thick > 0.3 * (end - from + 1)) continue;
                if (best is null || end - from > best.Value.To - best.Value.From) best = (from, end, y, rgb, breaks);
            }
            if (best is not { } b) continue;
            var style = b.Breaks >= 2 ? ((b.To - b.From) / (double)(b.Breaks + 1) < 1.5 * Math.Max(2, h / 6.0) ? TraceStyle.Dotted : TraceStyle.Dashed)
                                      : TraceStyle.Solid;
            result.Add(new LegendEntry(b.Rgb, style, w.Text, new PixelBox(b.From, b.Row - 1, b.To, b.Row + 1), w.Box));
        }
        // A legend is at least two entries, or one inside the plot area: a lone word with a dash before it outside the
        // plot is more likely an axis title's decoration.
        return result.Count >= 2 || result.Any(e => frame.Inside(e.Word.CentreX, e.Word.CentreY)) ? result : [];
    }

    /// <summary>The box a legend occupies, to keep its samples out of the trace pass.</summary>
    public static IReadOnlyList<PixelBox> Boxes(IReadOnlyList<LegendEntry> legend) =>
        [.. legend.Select(e => e.Sample.Union(e.Word))];

    // A complex readout: S(1,1)=0.9161∠-38.32° (MA) or S(2,1)=-0.76 dB ∠-38.3° (dB). The angle sign is read as
    // whatever the reader made of it — one character or none — between the two numbers.
    private static readonly Regex ComplexShape = new(
        @"=\s*([-+]?[0-9]*\.?[0-9]+)\s*(dB|d8|DB)?\s*\S?\s*([-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)", RegexOptions.CultureInvariant);

    /// <summary>A readout's complex value, or null. The value part is read with the stroke reader's usual confusions
    /// undone (Z for 2, O for 0, S for 5, l for 1): they are all digits there.</summary>
    public static System.Numerics.Complex? ComplexValue(string text)
    {
        int eq = text.LastIndexOf('=');
        if (eq < 0 || PlotQuantity.Entry(text[..eq]) is null) return null;
        var v = new System.Text.StringBuilder(text[..eq]);
        // A Z directly before a sign is the angle mark, not a 2.
        string tail = Regex.Replace(text[eq..], @"[Zz](?=\s*[-+])", "∠");
        foreach (char c in tail)
            v.Append(c switch { 'Z' or 'z' => '2', 'O' or 'o' => '0', 'S' => '5', 'l' or 'I' or '|' => '1', 'B' when v.Length > 0 && v[^1] != 'd' => '8', _ => c });
        var m = ComplexShape.Match(v.ToString()[eq..]);
        if (!m.Success || !m.Groups[3].Success) return null;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
            || !double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double deg)) return null;
        double mag = m.Groups[2].Success ? Math.Pow(10, a / 20) : a;
        if (!(mag >= 0) || mag > 100 || Math.Abs(deg) > 360) return null;
        return System.Numerics.Complex.FromPolarCoordinates(mag, deg * Math.PI / 180);
    }

    /// <summary>
    /// The markers of <paramref name="frame"/>. A name with a readout block under it is a readout; a name alone near a
    /// curve is a glyph's label, placed at its glyph's tip. A readout is joined to the glyph label of its name when that
    /// name is the only one of its kind; otherwise it stays unplaced and is placed later by its complex value.
    /// </summary>
    public static IReadOnlyList<PlotMarker> Find(PlotInk ink, PlotFrame frame, IReadOnlyList<PlotWord> words)
    {
        double size = frame.IsChart ? 2 * frame.Radius : Math.Max(frame.Right - frame.Left, frame.Bottom - frame.Top);
        var named = words.Where(w => w.Read && !w.Vertical && Name(w.Text) is not null
                                     && w.Box.CentreX > frame.Left - 0.4 * size && w.Box.CentreX < frame.Right + 0.6 * size
                                     && w.Box.CentreY > frame.Top - 0.3 * size && w.Box.CentreY < frame.Bottom + 0.3 * size).ToList();
        var readouts = new List<(string Name, string Text, double? F, double? V, System.Numerics.Complex? C)>();
        var labels = new List<(string Name, PlotWord Word)>();
        foreach (var w in named)
        {
            string name = Name(w.Text)!;
            // The readout block: words under the name, left-aligned with it, a line apart.
            var block = new List<PlotWord>();
            double y = w.Box.Bottom;
            foreach (var u in words.Where(u => u.Read && u != w && !u.Vertical && u.Box.Top > w.Box.Top
                                               && u.Box.Left >= w.Box.Left - 1.5 * w.Height && u.Box.Left <= w.Box.Left + 4 * w.Height)
                                   .OrderBy(u => u.Box.Top))
            {
                if (u.Box.Top - y > 1.2 * w.Height) break;
                if (Name(u.Text) is not null) break;
                block.Add(u);
                y = u.Box.Bottom;
            }
            string text = string.Join(" ", block.Select(b => b.Text));
            double? f = block.Select(b => Frequency(b.Text)).FirstOrDefault(v => v is not null) ?? Frequency(text);
            var c = block.Select(b => ComplexValue(b.Text)).FirstOrDefault(v => v is not null);
            double? value = block.Select(b => Value(b.Text)).FirstOrDefault(v => v is not null);
            if (f is not null || c is not null) readouts.Add((name, text, f, value, c));
            else labels.Add((name, w));
        }

        var result = new List<PlotMarker>();
        var joined = new HashSet<int>();
        foreach (var r in readouts)
        {
            var mine = labels.Select((l, i) => (l, i)).Where(x => x.l.Name == r.Name).ToList();
            bool unique = readouts.Count(o => o.Name == r.Name) == 1 && mine.Count == 1;
            // A readout's own complex value outranks a glyph label it could only be joined to by a name.
            if (unique && r.C is null)
            {
                var (tx, ty) = Tip(ink, mine[0].l.Word);
                joined.Add(mine[0].i);
                result.Add(new PlotMarker(frame.Index, r.Name, tx, ty, r.F, r.V, r.Text, r.C));
            }
            else
            {
                if (unique) joined.Add(mine[0].i);
                result.Add(new PlotMarker(frame.Index, r.Name, double.NaN, double.NaN, r.F, r.V, r.Text, r.C));
            }
        }
        for (int i = 0; i < labels.Count; i++)
        {
            if (joined.Contains(i)) continue;
            var (tx, ty) = Tip(ink, labels[i].Word);
            result.Add(new PlotMarker(frame.Index, labels[i].Name, tx, ty, null, null, ""));
        }
        return [.. result.OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.FrequencyHz ?? 0)];
    }

    /// <summary>The point a marker's glyph marks: the lowest pixel of the dark mark just under its label, at the mark's
    /// centre; the label's foot when there is no mark.</summary>
    private static (double X, double Y) Tip(PlotInk ink, PlotWord label)
    {
        int h = label.Height;
        int left = label.Box.Left - h / 2, right = label.Box.Right + h / 2;
        int top = label.Box.Bottom + 1, bottom = label.Box.Bottom + (int)(2.5 * h);
        int lowest = -1;
        double sx = 0;
        int n = 0;
        int labelRgb = label.Rgb;
        for (int y = top; y <= bottom; y++)
        {
            bool any = false;
            for (int x = left; x <= right; x++)
            {
                if (!ink.At(x, y) || PlotInk.Distance(ink.Raster.Rgb(x, y), labelRgb) > 80) continue;
                any = true;
                if (y > lowest) { lowest = y; sx = 0; n = 0; }
                if (y == lowest) { sx += x + 0.5; n++; }
            }
            if (!any && lowest >= 0) break;
        }
        return lowest >= 0 && n > 0 ? (sx / n, lowest + 1.0) : (label.Box.CentreX, label.Box.Bottom + 1.0);
    }
}
