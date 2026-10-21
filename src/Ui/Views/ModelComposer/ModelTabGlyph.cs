using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.ModelComposer;

namespace CircuitRF.Ui.Views.ModelComposer;

/// <summary>
/// A plots-panel tab's face (owner, review 2): the Data step's five tabs as glyphs rather than words — Output and Transfer a
/// small rectangular plot of their curve shape with <c>Vds</c> or <c>Vgs</c> beside it, the way the layout editor's EM Setup
/// button carries its word, S the same with <c>S</c>, the bias plane a grid of bias points on axes, and Smith the Data
/// Display's own Smith glyph. Every other tab is its name. Drawn in the button's foreground, so a lit tab draws white.
/// </summary>
public sealed class ModelTabGlyph : StackPanel
{
    public static readonly StyledProperty<string?> TabProperty =
        AvaloniaProperty.Register<ModelTabGlyph, string?>(nameof(Tab));

    public string? Tab
    {
        get => GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    /// <summary>What each glyph tab is, for its tooltip.</summary>
    public static readonly IReadOnlyDictionary<string, string> Tips = new Dictionary<string, string>
    {
        [ModelComposerViewModel.OutputTab] = "Output: Id against Vds, a curve per Vgs",
        [ModelComposerViewModel.TransferTab] = "Transfer: Id against Vgs, a curve per Vds",
        [ModelComposerViewModel.BiasPlaneTab] = "Bias plane: every measured bias point",
        [ModelComposerViewModel.STab] = "S-parameters in dB at the cursor's bias",
        [ModelComposerViewModel.SmithTab] = "Smith chart of the reflections at the cursor's bias",
    };

    public ModelTabGlyph()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 3;
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TabProperty) Build();
    }

    private void Build()
    {
        Children.Clear();
        string tab = Tab ?? "";
        switch (tab)
        {
            case ModelComposerViewModel.OutputTab: Children.Add(new CurveGlyph { Shape = CurveShape.Output }); Children.Add(Word("Vds")); break;
            case ModelComposerViewModel.TransferTab: Children.Add(new CurveGlyph { Shape = CurveShape.Transfer }); Children.Add(Word("Vgs")); break;
            case ModelComposerViewModel.STab: Children.Add(new CurveGlyph { Shape = CurveShape.SParams }); Children.Add(Word("S")); break;
            case ModelComposerViewModel.BiasPlaneTab: Children.Add(new CurveGlyph { Shape = CurveShape.BiasPlane }); break;
            case ModelComposerViewModel.SmithTab:
            {
                var smith = new PlotTypeGlyphControl { Kind = PlotTypeGlyphKind.Smith, Width = 16, Height = 16 };
                smith.Bind(PlotTypeGlyphControl.StrokeProperty, new Binding("Foreground")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) },
                });
                Children.Add(smith);
                break;
            }
            default: Children.Add(new TextBlock { Text = tab, VerticalAlignment = VerticalAlignment.Center }); break;
        }
        ToolTip.SetTip(this, Tips.TryGetValue(tab, out var tip) ? tip : null);
    }

    private static TextBlock Word(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
    };

    public enum CurveShape { Output, Transfer, SParams, BiasPlane }

    /// <summary>A 16-pixel plot: two axes and the tab's curve shape, in the inherited foreground.</summary>
    public sealed class CurveGlyph : Control
    {
        public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<CurveGlyph>();

        static CurveGlyph() => AffectsRender<CurveGlyph>(ForegroundProperty);

        public CurveShape Shape { get; init; }

        public IBrush? Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public CurveGlyph()
        {
            Width = 16;
            Height = 16;
        }

        public override void Render(DrawingContext ctx)
        {
            var brush = Foreground ?? Brushes.Gray;
            var axis = new Pen(brush, 1.1);
            var curve = new Pen(brush, 1.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            // Axes: left and bottom, inset half a pixel so they are crisp.
            ctx.DrawLine(axis, new Point(1.5, 1), new Point(1.5, 14.5));
            ctx.DrawLine(axis, new Point(1.5, 14.5), new Point(15, 14.5));
            switch (Shape)
            {
                case CurveShape.Output:
                    // Three output curves: a steep rise to a knee, then flat.
                    foreach (double level in new[] { 11.0, 7.5, 4.0 })
                        Polyline(ctx, curve, [(2, 14.5), (5, 14.5 - (14.5 - level) * 0.85), (8, level), (15, level - 0.6)]);
                    break;
                case CurveShape.Transfer:
                    // One transfer curve: off below threshold, then rising.
                    Polyline(ctx, curve, [(2, 14), (6, 13.8), (8.5, 12), (11, 8), (13, 5), (15, 3)]);
                    break;
                case CurveShape.SParams:
                    // A gain curve rolling off with frequency, and a return loss below it.
                    Polyline(ctx, curve, [(2, 4), (8, 4.5), (11, 6.5), (15, 10)]);
                    Polyline(ctx, curve, [(2, 12.5), (7, 11), (11, 12), (15, 11)]);
                    break;
                case CurveShape.BiasPlane:
                    // A grid of bias points.
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j < 3; j++)
                            ctx.DrawEllipse(brush, null, new Point(5 + 4 * i, 3.5 + 4 * j), 1.2, 1.2);
                    break;
            }
        }

        private static void Polyline(DrawingContext ctx, IPen pen, (double X, double Y)[] pts)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(pts[0].X, pts[0].Y), false);
                for (int k = 1; k < pts.Length; k++) c.LineTo(new Point(pts[k].X, pts[k].Y));
                c.EndFigure(false);
            }
            ctx.DrawGeometry(null, pen, g);
        }
    }
}
