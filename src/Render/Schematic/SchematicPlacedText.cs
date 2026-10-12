namespace CircuitRF.Render;

/// <summary>What a string <see cref="SchematicRenderer"/> draws is, on the schematic.</summary>
public enum SchematicTextRole
{
    /// <summary>Label row 0: the component's type name.</summary>
    ComponentType,
    /// <summary>Label row 1: the instance name (its designator).</summary>
    InstanceName,
    /// <summary>Label rows 2+: a shown parameter (<c>R = 50 Ohm</c>), or the footprint row.</summary>
    Parameter,
    /// <summary>A net label.</summary>
    NetLabel,
    /// <summary>A pin's name beside its pin (a library symbol's; a built-in draws none).</summary>
    PinName,
    /// <summary>Text inside a symbol's own artwork: a port number, a polarity mark, <c>RF</c>/<c>LO</c>/<c>IF</c>.</summary>
    SymbolText,
    /// <summary>A mark about the instance rather than the circuit: an external alias, "moved", "Not Found".</summary>
    Chrome,
}

/// <summary>
/// One string the schematic renderer drew, where it drew it: the line box (ascent to descent, origin to advance
/// width) as four corners in reading order — top-left, top-right, bottom-right, bottom-left — in canvas pixels.
/// </summary>
/// <param name="ComponentId">The component it belongs to; for a net label, the label's own id.</param>
/// <param name="Index">The label row (<see cref="SchematicComponent.Labels"/>), the pin index, the symbol-primitive
/// index, or -1.</param>
public readonly record struct SchematicPlacedText(
    string Text, SchematicTextRole Role, string? ComponentId, int Index,
    (float X, float Y) TopLeft, (float X, float Y) TopRight, (float X, float Y) BottomRight, (float X, float Y) BottomLeft);
