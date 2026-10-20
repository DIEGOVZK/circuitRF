using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Design ▸ Ground Pour Margin… (R-dfr22-3-4): how far the generated ground pour reaches beyond the lines and vias
/// it covers, in substrate heights. One number; the caller records it in the workspace's <c>.cws</c>.
/// </summary>
public static class GroundPourMarginDialog
{
    /// <summary>Parses a margin as typed: a non-negative number of substrate heights, at most 1000.</summary>
    internal static bool TryParse(string? text, out double heights)
    {
        heights = 0;
        return double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out heights)
               && double.IsFinite(heights) && heights >= 0 && heights <= 1000;
    }

    /// <summary>Asks; null when cancelled.</summary>
    public static async Task<double?> AskAsync(Window owner, double current)
    {
        double? answer = null;
        var dialog = new Window
        {
            Title                 = "Ground Pour Margin",
            Width                 = 380,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
            ShowInTaskbar         = false,
        };

        var box = new TextBox { Text = current.ToString("0.##", CultureInfo.InvariantCulture), FontSize = 12 };
        var invalid = new TextBlock
        {
            Text = "Enter a number of substrate heights, 0 or more.", FontSize = 11, TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        invalid.Bind(TextBlock.ForegroundProperty, dialog.GetResourceObservable("CrfWarningBrush"));

        void Commit()
        {
            if (!TryParse(box.Text, out double h)) { invalid.IsVisible = true; return; }
            answer = h;
            dialog.Close();
        }

        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => dialog.Close();
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Commit();
        box.TextChanged += (_, _) => invalid.IsVisible = !TryParse(box.Text, out _);

        dialog.Content = new StackPanel
        {
            Margin  = new Avalonia.Thickness(20, 16),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Margin, in substrate heights:", FontSize = 12 },
                box,
                new TextBlock
                {
                    Text = "How far the ground pour Update Layout from Schematic and Draw Ground Pour draw reaches beyond " +
                           "the lines and vias it covers. A board outline, when one is drawn, bounds the pour instead.",
                    FontSize = 10, Opacity = 0.55, TextWrapping = TextWrapping.Wrap,
                },
                invalid,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Margin = new Avalonia.Thickness(0, 8, 0, 0),
                    Children = { cancel, ok },
                },
            },
        };
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await dialog.ShowDialog(owner);
        return answer;
    }
}
