namespace CircuitRF.Ui.Layout;

/// <summary>
/// The layers open layouts draw on that their technology does not define, per technology file, and
/// whether each is shown and selectable — for THIS SESSION ONLY.
///
/// <para><b>Why it exists.</b> An import into a chosen technology keeps a file the technology has no
/// layer for (a paste stencil, say) on a key of its own, and nothing is added to the technology. Its
/// shapes are drawn through <see cref="FallbackPalette"/>, which is always visible — and the Layers
/// panel lists the technology's layers only, so they could not be hidden at all (designer report,
/// round 18: "solder mask doesn't go totally away" was the two paste layers).</para>
///
/// <para><b>Why session only.</b> The technology is a shared file, and visibility in it is a saved
/// setting; a layer it does not define has nowhere in it to keep one without being added to it, which
/// is a different decision. So the rows the technology editor shows for these layers write here, and
/// nothing here is ever written to disk.</para>
///
/// <para>UI-thread state, keyed by the technology's full path, so every open layout resolving one
/// technology hides a layer together — as the technology's own Vis column does.</para>
/// </summary>
internal static class UndefinedLayerSession
{
    internal sealed record Entry(LayerKey Key, bool Visible, bool Selectable);

    private static readonly Dictionary<string, Dictionary<LayerKey, Entry>> ByTech = new(StringComparer.Ordinal);

    /// <summary>Bumped on every change, so a reader can cache what it derives.</summary>
    internal static int Version { get; private set; }

    /// <summary>Raised with the technology's full path whenever its set or a toggle changes.</summary>
    internal static event Action<string>? Changed;

    private static string Normalize(string techPath) => Path.GetFullPath(techPath);

    /// <summary>Records layers a layout under <paramref name="techPath"/> draws that the technology
    /// does not define. A layer already known keeps its toggles.</summary>
    internal static void Seen(string techPath, IEnumerable<LayerKey> keys)
    {
        string path = Normalize(techPath);
        if (!ByTech.TryGetValue(path, out var set)) ByTech[path] = set = [];
        bool added = false;
        foreach (var key in keys)
            if (set.TryAdd(key, new Entry(key, true, true))) added = true;
        if (!added) return;
        Version++;
        Changed?.Invoke(path);
    }

    /// <summary>Every layer recorded for <paramref name="techPath"/>, in key order.</summary>
    internal static IReadOnlyList<Entry> For(string techPath) =>
        ByTech.TryGetValue(Normalize(techPath), out var set)
            ? [.. set.Values.OrderBy(e => e.Key.Layer).ThenBy(e => e.Key.Datatype)]
            : [];

    internal static void Set(string techPath, LayerKey key, bool visible, bool selectable)
    {
        string path = Normalize(techPath);
        if (!ByTech.TryGetValue(path, out var set)) ByTech[path] = set = [];
        var entry = new Entry(key, visible, selectable);
        if (set.TryGetValue(key, out var old) && old == entry) return;
        set[key] = entry;
        Version++;
        Changed?.Invoke(path);
    }

    /// <summary>
    /// <paramref name="tech"/> as a layout under <paramref name="techPath"/> should DRAW it: with a
    /// layer of the fallback palette's own look added for every undefined layer someone has hidden or
    /// made unselectable. The same instance when there is none, so a session nobody toggled in draws
    /// exactly as before. Never saved: the copy exists only for the renderer and the hit test.
    /// </summary>
    internal static Technology WithSessionLayers(Technology tech, string techPath)
    {
        if (!ByTech.TryGetValue(Normalize(techPath), out var set)) return tech;
        var toggled = set.Values.Where(e => (!e.Visible || !e.Selectable) && tech.Layers.All(l => l.Key != e.Key)).ToList();
        if (toggled.Count == 0) return tech;

        var copy = TechPersistence.Clone(tech);
        foreach (var e in toggled.OrderBy(e => e.Key.Layer).ThenBy(e => e.Key.Datatype))
        {
            var def = FallbackPalette.For(e.Key);
            def.Visible = e.Visible;
            def.Selectable = e.Selectable;
            copy.Layers.Add(def);
        }
        return copy;
    }

    /// <summary>Tests only: forget everything.</summary>
    internal static void Reset()
    {
        ByTech.Clear();
        Version++;
    }
}
