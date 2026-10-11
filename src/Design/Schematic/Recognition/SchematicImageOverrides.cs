// What the user corrected on a schematic picture — brief-img-10-schematic-emit.md R-im10-8 (overview D11, D15).
//
// Two corrections, both held as OVERRIDES — re-applied after every re-run, like a parts-table edit — and both carried to
// the CLI as pixel points, so the format stays the contract:
//   --disconnect x,y        the crossing at (x, y) does not connect;
//   --connect x,y           the crossing at (x, y) connects;
//   --connect x,y:x2,y2     the dangling wire end at (x, y) joins the pin (or wire end) at (x2, y2).
// Points are in the SOURCE picture's pixels — what the user clicked, whatever the reading turned to straighten it.
// Nothing else on a picture is editable: the schematic editor is where a drawing is fixed.

using System.Globalization;

namespace CircuitRF.Design.Schematic.Recognition;

public enum ConnectionOverrideKind { Connect, Disconnect }

/// <summary>One correction, in the source picture's pixels.</summary>
/// <param name="ToX">For a join: the pin the dangling end at (<paramref name="X"/>, <paramref name="Y"/>) was dragged
/// onto; null for a crossing.</param>
public sealed record ConnectionOverride(ConnectionOverrideKind Kind, double X, double Y, double? ToX = null, double? ToY = null)
{
    public const string ConnectFlag = "--connect";
    public const string DisconnectFlag = "--disconnect";

    /// <summary>A dangling end joined to a pin, rather than a crossing set.</summary>
    public bool IsJoin => ToX is not null && ToY is not null;

    public static ConnectionOverride Crossing(double x, double y, bool connect) =>
        new(connect ? ConnectionOverrideKind.Connect : ConnectionOverrideKind.Disconnect, x, y);

    public static ConnectionOverride Join(double x, double y, double toX, double toY) =>
        new(ConnectionOverrideKind.Connect, x, y, toX, toY);

    /// <summary>The CLI's flag.</summary>
    public string Flag => Kind == ConnectionOverrideKind.Connect ? ConnectFlag : DisconnectFlag;

    /// <summary>The CLI's value: <c>x,y</c> or <c>x,y:x2,y2</c>.</summary>
    public string Value => IsJoin ? $"{N(X)},{N(Y)}:{N(ToX!.Value)},{N(ToY!.Value)}" : $"{N(X)},{N(Y)}";

    public override string ToString() => $"{Flag} {Value}";

    /// <summary>Every override as the CLI's arguments, flag then value.</summary>
    public static IReadOnlyList<string> ToArguments(IEnumerable<ConnectionOverride> overrides) =>
        [.. overrides.SelectMany(o => new[] { o.Flag, o.Value })];

    /// <summary>One flag's value read back, or null with the reason.</summary>
    public static ConnectionOverride? Parse(string flag, string value, out string? error)
    {
        error = null;
        bool connect = flag == ConnectFlag;
        if (!connect && flag != DisconnectFlag) { error = $"'{flag}' is not {ConnectFlag} or {DisconnectFlag}."; return null; }
        var halves = (value ?? "").Split(':');
        if (halves.Length > 2 || (!connect && halves.Length == 2) || !Point(halves[0], out double x, out double y))
        {
            error = $"{flag} needs a pixel point x,y{(connect ? " (or x,y:x2,y2 to join a wire end to a pin)" : "")}; '{value}' is not one.";
            return null;
        }
        if (halves.Length == 1) return Crossing(x, y, connect);
        if (!Point(halves[1], out double x2, out double y2))
        {
            error = $"{flag} needs a pixel point x,y:x2,y2; '{value}' is not one.";
            return null;
        }
        return Join(x, y, x2, y2);
    }

    /// <summary>The overrides as one provenance option value, in order.</summary>
    public static string Spell(IEnumerable<ConnectionOverride> overrides) => string.Join(" ", overrides.Select(o => o.ToString()));

    private static bool Point(string text, out double x, out double y)
    {
        x = y = 0;
        var p = text.Split(',');
        return p.Length == 2
               && double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x) && double.IsFinite(x)
               && double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y) && double.IsFinite(y);
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
