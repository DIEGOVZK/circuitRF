using System;
using System.Collections.Generic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Theming;

/// <summary>
/// Settings ▸ General ▸ <b>Units</b> (brief-dfr22-1): the preferred length and frequency unit, read by
/// name off <see cref="AppPreferences"/>. Null is <b>Automatic</b> — what circuitRF did before the
/// preference existed — and so is any name this build does not know (the <c>LayoutPartKind.Parse</c>
/// rule: exact spelling, never a guess).
///
/// <para><b>Only for something NEW.</b> A unit a document already states is the user's own override
/// and wins everywhere; nothing here rewrites a stored number, so changing a preference changes no
/// value and no result (R-dfr22-1-6). Below the firewall the unit is an ARGUMENT — this class is the
/// one place the GUI reads it.</para>
/// </summary>
public static class PreferredUnits
{
    /// <summary>The length units offered, by stored name — µm, mm, mil. Inch is a
    /// <see cref="LayoutUnit"/> but not a schematic Length option, and nm is no one's working unit.</summary>
    public static IReadOnlyList<LayoutUnit> LengthChoices { get; } = [LayoutUnit.Um, LayoutUnit.Mm, LayoutUnit.Mil];

    /// <summary>The frequency units offered, by stored name.</summary>
    public static IReadOnlyList<string> FrequencyChoices { get; } = FreqUnitHelper.Units;

    /// <summary>A stored length name as its unit, or null (Automatic) for null or an unknown name.</summary>
    public static LayoutUnit? ParseLength(string? stored)
    {
        foreach (var u in LengthChoices)
            if (string.Equals(stored, u.ToString(), StringComparison.Ordinal)) return u;
        return null;
    }

    /// <summary>A stored frequency name, or null (Automatic) for null or an unknown name.</summary>
    public static string? ParseFrequency(string? stored)
    {
        foreach (var u in FrequencyChoices)
            if (string.Equals(stored, u, StringComparison.Ordinal)) return u;
        return null;
    }

    /// <summary>The preferred length unit, or null for Automatic.</summary>
    public static LayoutUnit? Length => ParseLength(AppPreferencesIo.Load().PreferredLengthUnit);

    /// <summary>The preferred frequency unit (<c>Hz</c> … <c>GHz</c>), or null for Automatic.</summary>
    public static string? Frequency => ParseFrequency(AppPreferencesIo.Load().PreferredFrequencyUnit);

    /// <summary>The frequency unit a new Data Display plot opens in — GHz under Automatic, as before.</summary>
    public static FreqUnit PlotFrequencyUnit => Frequency switch
    {
        "Hz"  => FreqUnit.Hz,
        "kHz" => FreqUnit.kHz,
        "MHz" => FreqUnit.MHz,
        _     => FreqUnit.GHz,
    };

    /// <summary>
    /// A NEW frequency's built-in default (<paramref name="coeff"/> in <paramref name="unit"/>) shown in
    /// the preferred unit, the same frequency: <c>1 GHz</c> reads <c>1000 MHz</c> with MHz preferred.
    /// Unchanged under Automatic, and for an expression, which has no magnitude to rescale.
    /// </summary>
    public static (string Coeff, string Unit) NewFrequency(string coeff, string unit)
        => NewFrequency(coeff, unit, Frequency);

    /// <summary><see cref="NewFrequency(string,string)"/> with the preference given.</summary>
    public static (string Coeff, string Unit) NewFrequency(string coeff, string unit, string? preferred)
    {
        if (preferred is null || preferred == unit || !IsOffered(unit)
            || !NumericText.TryParseDouble(coeff.Trim(), out _)) return (coeff, unit);
        return (FreqUnitHelper.Rescale(coeff, unit, preferred), preferred);
    }

    /// <summary>
    /// A newly placed part's frequency rows (<c>F</c>, <c>Freq</c>, <c>Fc</c>…) — the registry states
    /// them in GHz — in the preferred unit, same frequency. Rows already in another unit are the
    /// part's own and stay as they are.
    /// </summary>
    public static void ApplyToNewFrequencyRows(IEnumerable<EditableParameter> parameters, string? preferred)
    {
        if (preferred is null) return;
        foreach (var p in parameters)
        {
            if (p.Dimension != UnitDimension.Frequency || p.Unit != "GHz") continue;
            (p.Expression, p.Unit) = NewFrequency(p.Expression, p.Unit, preferred);
        }
    }

    private static bool IsOffered(string unit) => Array.IndexOf(FreqUnitHelper.Units, unit) >= 0;
}
