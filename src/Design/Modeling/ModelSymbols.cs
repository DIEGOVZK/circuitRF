// The symbol a composed FET is drawn with (brief-modelcomposer-11 R-mc11-2; overview D12).
//
// Pins bind to the device BY NUMBER: 1, 2 and 3 are gate, drain and source; a fourth is the thermal node, and only a model
// that self-heats has one (MC-9). So a symbol is a choice only when its pins say exactly that: a 2-pin symbol is refused,
// a 4-pin symbol on a model without a thermal node is refused, and anything with a pin beyond the fourth is refused.
// A library symbol qualifies only when its kind's own pins are g, d, s in that order — the FET laws' arrangement — because
// a JFET's or a BJT's three pins mean something else in the same positions.
//
// The default, written as no symbol at all, is the standard three-terminal FET symbol (ComposedFetSymbolProvider).

using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;

namespace CircuitRF.Design.Modeling;

/// <summary>One symbol a model can be exported with: how the <c>.cmodel</c> names it, a label, and its pin count.</summary>
public sealed record SymbolChoice(string Value, string Label, int Pins);

public static class ModelSymbols
{
    /// <summary>The library kinds whose pins are gate, drain and source, in that order — never the composed FET's own
    /// kind, whose glyph is the standard symbol (no value at all).</summary>
    public static IReadOnlyList<string> LibraryNames { get; } =
        [.. Enum.GetValues<SymbolKind>()
                .Where(k => k != SymbolKind.ComposedFet)
                .Where(k => SymbolPortDefs.For(k).Select(p => p.Name).SequenceEqual(["g", "d", "s"]))
                .Select(k => k.ToString())];

    /// <summary>
    /// Why <paramref name="symbol"/> cannot be this model's symbol, or null when it can. A <c>.csym</c> path is resolved
    /// against <paramref name="baseDirectory"/> when it is relative. <paramref name="thermal"/>: the model self-heats.
    /// </summary>
    public static string? Refusal(string symbol, string baseDirectory, bool thermal)
    {
        string name = symbol.Trim();
        if (name.Length == 0) return null;
        if (!name.EndsWith(".csym", StringComparison.OrdinalIgnoreCase))
            return LibraryNames.Contains(name, StringComparer.Ordinal) ? null
                 : $"'{name}' is not a library symbol with gate, drain and source pins; choose one of {string.Join(", ", LibraryNames)}, or a .csym.";

        string path = Path.IsPathRooted(name) ? name : Path.GetFullPath(Path.Combine(baseDirectory, name));
        if (!File.Exists(path)) return $"'{name}' does not exist.";
        Symbol.Symbol loaded;
        try { loaded = SymbolPersistence.LoadFromFile(path); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return $"'{name}' is not a readable symbol ({ex.Message})."; }
        return PinRefusal(name, [.. loaded.Pins.Select(p => p.PortIndex)], thermal);
    }

    /// <summary>The pin rule on a symbol's 0-based pin indices.</summary>
    internal static string? PinRefusal(string name, IReadOnlyCollection<int> pins, bool thermal)
    {
        var set = pins.ToHashSet();
        int count = set.Count;
        if (!(set.Contains(0) && set.Contains(1) && set.Contains(2)))
            return $"'{name}' has {count} pin{(count == 1 ? "" : "s")}; a FET symbol has pins 1, 2 and 3 (gate, drain, source).";
        if (set.Any(p => p > 3))
            return $"'{name}' has a pin {set.Max() + 1}; a FET symbol has pins 1 to 3, and a fourth for the thermal node.";
        if (set.Contains(3) && !thermal)
            return $"'{name}' has a fourth pin, the thermal node, and this model does not self-heat.";
        return null;
    }

    /// <summary>
    /// Every symbol this model can take (R-mc11-2): the standard symbol, the qualifying library symbols, and each
    /// <c>.csym</c> under <paramref name="workspaceDirectory"/> the pin rule accepts, its value relative to
    /// <paramref name="baseDirectory"/> (the <c>.cmodel</c>'s folder).
    /// </summary>
    public static IReadOnlyList<SymbolChoice> Candidates(string? workspaceDirectory, string baseDirectory, bool thermal)
    {
        var list = new List<SymbolChoice> { new("", "FET (standard)", 3) };
        list.AddRange(LibraryNames.Select(n => new SymbolChoice(n, n, 3)));
        if (workspaceDirectory is null || !Directory.Exists(workspaceDirectory)) return list;
        foreach (string file in Directory.EnumerateFiles(workspaceDirectory, "*.csym", SearchOption.AllDirectories)
                                         .Order(StringComparer.Ordinal))
        {
            Symbol.Symbol loaded;
            try { loaded = SymbolPersistence.LoadFromFile(file); }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
            var pins = loaded.Pins.Select(p => p.PortIndex).ToList();
            if (PinRefusal(file, pins, thermal) is not null) continue;
            string rel = Path.GetRelativePath(baseDirectory, file).Replace('\\', '/');
            list.Add(new SymbolChoice(rel, Path.GetFileNameWithoutExtension(file), pins.Distinct().Count()));
        }
        return list;
    }
}
