// ================================================================
//  ModelReportPages.cs  —  the Model Validation Report's PDF pages:
//  the card first, then each tab's plots under the text that
//  reproduces them
//
//  The card (report.json, ModelReportCard) opens the document, for
//  the model's user before its developer: the scorecard — a grade
//  swatch, the test, its result and what the grade means — then where
//  the model is backed by data, how it was fitted, the simulator's
//  settings and every data set. Text flows onto as many pages as it
//  needs.
//
//  Each tab then gets a page headed by its section's conditions and
//  results; its plots fill the rest of that page, or a page of their
//  own when the text leaves them less than half of one. The plots are
//  drawn by the caller's composer into a clipped band, so this file
//  lays out text and nothing else — it reads no data.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Modeling;
using SkiaSharp;

namespace CircuitRF.Render.DataDisplay;

internal static class ModelReportPages
{
    private static readonly SKColor Ink = new(0x22, 0x22, 0x22), Muted = new(0x60, 0x60, 0x60), Rule = new(0xC8, 0xC8, 0xC8);

    internal static SKColor Colour(Grade g) => g switch
    {
        Grade.Green => new SKColor(0x2E, 0x9E, 0x44),
        Grade.Yellow => new SKColor(0xE8, 0xA2, 0x00),
        _ => new SKColor(0xD0, 0x34, 0x2C),
    };

    /// <summary>The pages: the card, then one per tab (two when its text leaves the plots too little room).</summary>
    public static List<Action<SKCanvas>> Build(ReportCard card, IReadOnlyList<(string Name, IReadOnlyList<PlacedPlot> Plots)> tabs,
                                               Action<SKCanvas, IReadOnlyList<PlacedPlot>, PagePlacement> drawPlots, PagePlacement page)
    {
        var flow = new Flow(page);
        Front(flow, card);
        var withPlots = tabs.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var textOnly = card.Sections.Where(s => !withPlots.Contains(s.Name) && (s.Conditions.Count > 0 || s.Lines.Count > 0)).ToList();
        if (textOnly.Count > 0)
        {
            flow.Heading("Notes");
            foreach (var s in textOnly) Section(flow, s, heading: false);
        }

        foreach (var (name, plots) in tabs)
        {
            flow.NewPage();
            if (card.Sections.FirstOrDefault(s => s.Name == name) is { } text) Section(flow, text, heading: true);
            else flow.Heading(name);
            float top = flow.Y + 6;
            if (page.Height - page.Margin - top < 0.5f * page.UsableHeight)
            {
                flow.NewPage();
                top = page.Margin;
            }
            flow.Draw(canvas =>
            {
                canvas.Save();
                canvas.ClipRect(new SKRect(0, top - 2, page.Width, page.Height));
                canvas.Translate(0, top - page.Margin);
                drawPlots(canvas, plots, new PagePlacement(page.Width, page.Height - top + page.Margin, page.Margin));
                canvas.Restore();
            });
        }
        return flow.Pages();
    }

    private static void Front(Flow flow, ReportCard card)
    {
        flow.Title("Model Validation Report");
        flow.Para($"Model: {card.Model} · Document: {card.Document}", flow.Small, Muted);
        flow.Space(6);
        flow.Para("For the designer using this model: the scorecard says what a design can rely on, the next sections say over which " +
                  "biases, temperatures and frequencies the model is backed by data and how it was built, and each later page states the " +
                  "conditions a result can be reproduced under.", flow.Small, Muted);
        flow.Space(8);
        flow.GradeLine("Overall grade", card.Overall, bold: true);
        foreach (var g in Enum.GetValues<Grade>()) flow.GradeLine(g.ToString(), g, legend: ReportCard.Legend(g));
        flow.Space(6);

        flow.Heading("Scorecard");
        if (card.Scores.Count == 0) flow.Para("No test produced a grade.", flow.Body, Muted);
        else
        {
            float[] w = Columns(flow.Width, 54, 150, 230);
            flow.Row(w, [("", flow.SmallBold), ("Test", flow.SmallBold), ("Result", flow.SmallBold), ("What it means", flow.SmallBold)], null);
            foreach (var r in card.Scores)
                flow.Row(w, [("", flow.Small), ($"{r.Section} · {r.Test}", flow.Small), (r.Result, flow.Small), (r.Meaning, flow.Small)], r.Grade);
        }

        flow.Heading("Where the model is supported by data");
        foreach (string l in card.Validity) flow.Bullet(l);
        flow.Heading("How the model was fitted");
        foreach (string l in card.Methods) flow.Bullet(l);
        flow.Heading("Simulator settings");
        foreach (string l in card.Simulation) flow.Bullet(l);

        flow.Heading("Data");
        float[] cw = Columns(flow.Width, 78, 70, 82, 46, 112, 112, 64);
        flow.Row(cw, [("Set", flow.SmallBold), ("Kind", flow.SmallBold), ("Used for", flow.SmallBold), ("Points", flow.SmallBold),
                      ("Vgs", flow.SmallBold), ("Vds", flow.SmallBold), ("Ambient", flow.SmallBold), ("Also", flow.SmallBold)], null);
        foreach (var c in card.Coverage)
            flow.Row(cw, [(c.Set, flow.Small), (c.Kind, flow.Small), (c.UsedFor, flow.Small),
                          (c.Points + (c.Excluded > 0 ? $" (+{c.Excluded} excl.)" : ""), flow.Small),
                          (c.Vgs, flow.Small), (c.Vds, flow.Small), (c.AmbientC, flow.Small), (c.Other, flow.Small)], null);
    }

    private static void Section(Flow flow, SectionText s, bool heading)
    {
        if (heading) flow.Heading(s.Name);
        else flow.Para(s.Name, flow.BodyBold, Ink);
        if (s.Conditions.Count > 0)
        {
            flow.Para("Conditions", flow.SmallBold, Ink);
            foreach (string c in s.Conditions)
                if (c.StartsWith("  ", StringComparison.Ordinal)) flow.Bullet(c.Trim(), indent: 22);
                else flow.Para(c, flow.Small, Ink, indent: 10);
        }
        if (s.Lines.Count > 0)
        {
            flow.Para("Results", flow.SmallBold, Ink);
            foreach (string l in s.Lines) flow.Bullet(l);
        }
    }

    /// <summary>Fixed column widths, the last taking what is left.</summary>
    private static float[] Columns(float width, params float[] fixedWidths) =>
        [.. fixedWidths, Math.Max(60, width - fixedWidths.Sum() - 6 * fixedWidths.Length)];

    /// <summary>Text flowed down pages: each call places its lines at the cursor, starting a page when they do not fit.</summary>
    private sealed class Flow
    {
        private readonly PagePlacement _page;
        private readonly List<List<Action<SKCanvas>>> _pages = [];
        public float Y { get; private set; }
        public float Width => _page.UsableWidth;
        private float Left => _page.Margin;
        private float Bottom => _page.Height - _page.Margin;

        public readonly Style Body, BodyBold, Small, SmallBold, Head, Big;

        public Flow(PagePlacement page)
        {
            _page = page;
            Body = new(SkiaFonts.PlexRegular, SkiaFonts.DejaVuRegular, 9.5f);
            BodyBold = new(SkiaFonts.PlexBold, SkiaFonts.DejaVuBold, 9.5f);
            Small = new(SkiaFonts.PlexRegular, SkiaFonts.DejaVuRegular, 8f);
            SmallBold = new(SkiaFonts.PlexBold, SkiaFonts.DejaVuBold, 8f);
            Head = new(SkiaFonts.PlexBold, SkiaFonts.DejaVuBold, 13f);
            Big = new(SkiaFonts.PlexBold, SkiaFonts.DejaVuBold, 20f);
            NewPage();
        }

        public void NewPage()
        {
            _pages.Add([c => c.Clear(SKColors.White)]);
            Y = _page.Margin;
        }

        public void Draw(Action<SKCanvas> a) => _pages[^1].Add(a);

        public List<Action<SKCanvas>> Pages() =>
            [.. _pages.Select(list => (Action<SKCanvas>)(c => { foreach (var a in list) a(c); }))];

        private void Need(float h)
        {
            if (Y + h > Bottom && Y > _page.Margin + 1) NewPage();
        }

        public void Space(float h) => Y += h;

        public void Title(string text)
        {
            Need(Big.Line);
            Text(text, Left, Y + Big.Size, Big, Ink);
            Y += Big.Line + 2;
        }

        public void Heading(string text)
        {
            Need(Head.Line + 2 * Body.Line);   // never strand a heading at a page's foot
            Y += 6;
            Text(text, Left, Y + Head.Size, Head, Ink);
            Y += Head.Line;
            float y = Y;
            Draw(c => { using var p = new SKPaint { Color = Rule, StrokeWidth = 0.6f, IsAntialias = true }; c.DrawLine(Left, y, Left + Width, y, p); });
            Y += 4;
        }

        public void Para(string text, Style s, SKColor colour, float indent = 0)
        {
            foreach (string line in Wrap(text, s, Width - indent))
            {
                Need(s.Line);
                Text(line, Left + indent, Y + s.Size, s, colour);
                Y += s.Line;
            }
            Y += 1;
        }

        public void Bullet(string text, float indent = 10)
        {
            var lines = Wrap(text, Small, Width - indent - 8);
            for (int i = 0; i < lines.Count; i++)
            {
                Need(Small.Line);
                if (i == 0) Text("•", Left + indent, Y + Small.Size, Small, Muted);
                Text(lines[i], Left + indent + 8, Y + Small.Size, Small, Ink);
                Y += Small.Line;
            }
            Y += 1.5f;
        }

        /// <summary>A grade swatch and its name, with what it means beside it.</summary>
        public void GradeLine(string label, Grade g, bool bold = false, string? legend = null)
        {
            var s = bold ? BodyBold : Small;
            Need(s.Line + 3);
            float y = Y;
            Swatch(Left, y, 54, s.Line, g);
            Text(legend ?? label, Left + 62, y + s.Size, s, Ink);
            Y += s.Line + 3;
        }

        /// <summary>One table row: each cell wrapped in its column; a grade, when given, fills the first cell as a swatch.</summary>
        public void Row(float[] widths, (string Text, Style Style)[] cells, Grade? grade)
        {
            var wrapped = cells.Select((c, i) => Wrap(c.Text, c.Style, widths[i])).ToArray();
            float h = wrapped.Select((l, i) => Math.Max(1, l.Count) * cells[i].Style.Line).Max() + 4;
            Need(h);
            float x = Left, y = Y;
            if (grade is { } g) Swatch(x, y + 1, widths[0], Small.Line, g);
            for (int i = 0; i < cells.Length; i++)
            {
                for (int k = 0; k < wrapped[i].Count; k++)
                    Text(wrapped[i][k], x, y + cells[i].Style.Size + k * cells[i].Style.Line, cells[i].Style, Ink);
                x += widths[i] + 6;
            }
            float ry = y + h - 1;
            Draw(c => { using var p = new SKPaint { Color = Rule, StrokeWidth = 0.4f, IsAntialias = true }; c.DrawLine(Left, ry, Left + Width, ry, p); });
            Y += h;
        }

        private void Swatch(float x, float y, float w, float h, Grade g) => Draw(c =>
        {
            using var fill = new SKPaint { Color = Colour(g), IsAntialias = true };
            c.DrawRoundRect(new SKRect(x, y, x + w, y + h), 2.5f, 2.5f, fill);
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
            string t = g.ToString().ToUpperInvariant();
            float tw = RendererText.MeasureTextWithFallback(t, SmallBold.Primary, SmallBold.Fallback);
            RendererText.DrawLeftTextWithFallback(c, t, x + (w - tw) / 2, y + h - 0.32f * h, SmallBold.Primary, SmallBold.Fallback, white);
        });

        private void Text(string text, float x, float y, Style s, SKColor colour) => Draw(c =>
        {
            using var paint = new SKPaint { Color = colour, IsAntialias = true };
            RendererText.DrawLeftTextWithFallback(c, text, x, y, s.Primary, s.Fallback, paint);
        });

        /// <summary>Greedy word wrap to <paramref name="width"/>; a word wider than the column is broken by characters.</summary>
        private static List<string> Wrap(string text, Style s, float width)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            string current = "";
            foreach (string word in text.Split(' '))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (Measure(candidate, s) <= width) { current = candidate; continue; }
                if (current.Length > 0) lines.Add(current);
                current = word;
                while (current.Length > 1 && Measure(current, s) > width)
                {
                    int n = current.Length - 1;
                    while (n > 1 && Measure(current[..n], s) > width) n--;
                    lines.Add(current[..n]);
                    current = current[n..];
                }
            }
            if (current.Length > 0) lines.Add(current);
            return lines;
        }

        private static float Measure(string t, Style s) => RendererText.MeasureTextWithFallback(t, s.Primary, s.Fallback);
    }

    /// <summary>A font pair (Plex, DejaVu for what Plex lacks) at one size, and its line height.</summary>
    private sealed class Style(SKTypeface primary, SKTypeface fallback, float size)
    {
        public SKFont Primary { get; } = new(primary, size) { Subpixel = true };
        public SKFont Fallback { get; } = new(fallback, size) { Subpixel = true };
        public float Size => size;
        public float Line => size * 1.35f;
    }
}
