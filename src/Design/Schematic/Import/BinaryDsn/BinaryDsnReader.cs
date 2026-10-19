// The binary .DSN design (a compound file) → ForeignSchematic — brief-schimp-4 R-si4-3/4/5, overview D7.
//
// Not derived from any other implementation (overview D7); every layout fact used here is recorded in
// src/Design/RESOLVED.md. The container is CompoundFile; the streams are read by version:
//
//   2.0  DsnPageV2 + DsnCache.
//   3.2  DsnPageV3 + DsnCache — the same sections, every record sized.
//   any other version: a refusal naming it, with the EDIF route (R-si4-3).
//
// ── THE TRANSFORM ─────────────────────────────────────────────────────────────────────────────
//
// A placed part stores a POSITION and a rotation code r (0–3) with a mirror bit, and every one of its pins' page positions.
// With the cache symbol's pin hotspot (hx, hy) and body width W and height H, in 1/100 inch with Y down:
//     mirror:  x → W − x                       (first)
//     r = 0: (x, y)   1: (y, W − x)   2: (W − x, H − y)   3: (H − y, x)
//     page = position + that
// — a quarter turn COUNTER-clockwise on screen per step, re-anchored so the turned body's top-left stays at the position.
// Every pin lands exactly where the page says it is. In ForeignInstance's terms (mirror first, then
// quarter turns CLOCKWISE) r = 1 is 270°, r = 3 is 90°, and the instance's origin is position + that map of (0, 0).
//
// ── HIERARCHY ─────────────────────────────────────────────────────────────────────────────────
//
// Each storage under Views/ is a schematic, its pages beneath it. In a hierarchical design (version 3.2) a block in a page's
// parts list places a schematic its value names, and that schematic's ports — the list after the parts — are its pins.
// The root is the schematic no block places. Each schematic becomes ONE sheet, its pages tiled left to right in it (SI-1
// builds one cell per sheet, and off-page connectors join only within a schematic); blocks become sheet instances, ports
// hierarchical labels. Power names stay design-wide.

// ── CONNECTIVITY THE FILE DECLARES (R-si4-5) ─────────────────────────────────────────────────
//
// Every placed pin names the wire it touches, and every wire names its net: the pins grouped by net ARE the file's own
// netlist, and become DeclaredNets. SI-1 then compares that against both the drawing and the import. Ground symbols carry
// the net they name ("0" is the simulation ground), so they become power labels, and SI-1's map makes "0" node 0.

using CircuitRF.Core.Pdk;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Schematic.Import.Cfb;

namespace CircuitRF.Design.Schematic.Import.BinaryDsn;

/// <summary>How a binary design is read: <paramref name="CodePage"/> is the ANSI code page its text is decoded in.</summary>
public sealed record BinaryDsnReadOptions(int CodePage = EdifText.DefaultCodePage);

public static class BinaryDsnReader
{
    /// <summary>The format's name, by its extension (overview D7).</summary>
    public const string Format = "dsn";

    /// <summary>One schematic unit (1/100 inch) in mils.</summary>
    private const double Mils = 10;

    /// <summary>The versions read, as "major.minor".</summary>
    public static readonly IReadOnlyList<string> ReadableVersions = ["2.0", "3.2"];

    public static ForeignSchematicRead Read(string path, BinaryDsnReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new BinaryDsnReadOptions();
        string file = Path.GetFileName(path);
        var open = CompoundFile.Open(path);
        if (!open.Ok) return Refused($"{file} could not be opened: {open.Refusal}");
        var cfb = open.File!;
        if (cfb.Streams.FirstOrDefault(s => s.Path == "Library") is null
            || !cfb.Streams.Any(s => s.Path.StartsWith("Views/", StringComparison.Ordinal)))
            return Refused($"{file} is a compound file, but not a schematic design: it has no Library stream and no views.");

        var libraryBytes = cfb.ReadStream("Library");
        if (!libraryBytes.Ok) return Refused($"{file}: {libraryBytes.Refusal}");
        var version = DsnLibrary.VersionOf(libraryBytes.Bytes!);
        string v = version is { } vv ? $"{vv.Major}.{vv.Minor}" : "(unstated)";
        if (!ReadableVersions.Contains(v))
            return Refused($"{file}: this design's version ({v}) is not readable; export it as EDIF 2.0.0 from the editor "
                         + "and import that file.");

        var text = new EdifText(options.CodePage);
        try
        {
            var library = DsnLibrary.Read(libraryBytes.Bytes!, text);
            var cacheBytes = cfb.ReadStream("Cache");
            if (!cacheBytes.Ok) return Refused($"{file}: {cacheBytes.Refusal}");
            var cache = new DsnCache(cacheBytes.Bytes!, text, library.Major);

            var storages = cfb.Entries.Where(e => e.Kind == CompoundEntryKind.Storage && e.Path.Count(c => c == '/') == 1
                                                  && e.Path.StartsWith("Views/", StringComparison.Ordinal)).ToList();
            if (storages.Count == 0) return Refused($"{file} holds no schematic.");
            if (storages.Count > 1 && library.Major < 3)
                return Refused($"{file} holds {storages.Count} schematics (a hierarchical design); hierarchy is read in version "
                             + "3.2 designs only. Export it as EDIF 2.0.0 from the editor and import that file.");

            var schematics = new List<DsnSchematic>();
            foreach (var storage in storages)
            {
                var pages = new List<DsnPage>();
                foreach (var entry in cfb.Streams.Where(s => s.Path.StartsWith(storage.Path + "/Pages/", StringComparison.Ordinal)))
                {
                    var bytes = cfb.ReadStream(entry);
                    if (!bytes.Ok) return Refused($"{file}: {bytes.Refusal}");
                    var stream = new DsnBytes(bytes.Bytes!, entry.Path, text);
                    pages.Add(library.Major >= 3 ? DsnPageV3.Read(stream, cache) : DsnPageV2.Read(stream));
                }
                schematics.Add(new DsnSchematic(storage.Name, pages));
            }
            if (schematics.All(s => s.Pages.Count == 0))
                return Refused($"{file}'s schematic{(schematics.Count == 1 ? " has" : "s have")} no pages.");
            var all = schematics.SelectMany(s => s.Pages).ToList();
            if (all.All(p => p.Parts.Count == 0 && p.Wires.Count == 0 && p.Blocks.Count == 0))
                return Refused($"{file} draws nothing: its page{(all.Count == 1 ? "" : "s")} hold{(all.Count == 1 ? "s" : "")} "
                             + "no parts and no wires.");
            if (Root(schematics, library, out var why) is not { } root) return Refused($"{file}: {why}");
            return new ForeignSchematicRead(Build(path, library, cache, schematics, root, text), null);
        }
        catch (DsnFormatException ex)
        {
            return Refused($"{file} (version {v}) could not be read: {ex.Message}. Export it as EDIF 2.0.0 from the editor "
                         + "and import that file.");
        }
    }

    private static ForeignSchematicRead Refused(string why) => new(null, why);

    /// <summary>One schematic of the design (a <c>Views/</c> storage) and its pages, in the order the container lists them.</summary>
    private sealed record DsnSchematic(string Name, IReadOnlyList<DsnPage> Pages);

    /// <summary>
    /// The root of a hierarchy: the one schematic no block places — or, when several are placed by none (left-over
    /// schematics), the one of those that places blocks. A design of one schematic is its own root.
    /// </summary>
    private static DsnSchematic? Root(List<DsnSchematic> schematics, DsnLibrary library, out string why)
    {
        why = "";
        if (schematics.Count == 1) return schematics[0];
        var placed = schematics.SelectMany(s => s.Pages).SelectMany(p => p.Blocks)
                               .Select(b => library.String(b.ImplementationIndex) ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unplaced = schematics.Where(s => !placed.Contains(s.Name)).ToList();
        if (unplaced.Count > 1) unplaced = [.. unplaced.Where(s => s.Pages.Any(p => p.Blocks.Count > 0))];
        if (unplaced.Count == 1) return unplaced[0];
        why = unplaced.Count == 0
            ? $"every one of its {schematics.Count} schematics is placed by a hierarchical block, so none is the top"
            : $"it holds {schematics.Count} schematics and no single one places the others "
              + $"({string.Join(", ", schematics.Select(s => s.Name))}); export it as EDIF 2.0.0 from the editor and import that file";
        return null;
    }

    // ── the neutral model ───────────────────────────────────────────────────────────────────────

    private static ForeignSchematic Build(string path, DsnLibrary library, DsnCache cache, List<DsnSchematic> schematics,
                                          DsnSchematic root, EdifText text)
    {
        var notes = new List<string>();
        var symbols = new Dictionary<string, ForeignSymbol>(StringComparer.OrdinalIgnoreCase);
        // A hierarchy is one sheet per schematic, its pages tiled left to right in it, the root first; a flat design one sheet
        // per page, which the build tiles.
        bool hierarchical = schematics.Count > 1;
        var ordered = hierarchical ? [root, .. schematics.Where(s => s != root)] : schematics;
        var pages = ordered.SelectMany((s, si) => s.Pages.Select(p => (Schematic: s, Page: p))).ToList();
        var schematicOf = schematics.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        // The declared nets (R-si4-5), joined as the editor joins them: a pin to its wire's net, pins that touch, a ground or
        // power symbol's NAME to the net it sits on, design-wide; an off-page connector's within its schematic (net ids are
        // per page); a hierarchical port's to the pin of that name on each block placing its schematic.
        var joins = new Joins();
        var wiredPins = new HashSet<string>(StringComparer.Ordinal);
        var allPins = new List<(string Key, ForeignPinRef Pin)>();
        int skippedGraphics = 0, skippedText = 0, misplaced = 0, overruled = 0;
        var misplacedAt = new List<string>();
        int unconnected = 0;
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string OffKey(DsnSchematic s, string net) => hierarchical ? $"off:{s.Name}:{net.ToUpperInvariant()}" : $"name:{net.ToUpperInvariant()}";
        string PortKey(DsnSchematic s, string port) => $"port:{s.Name.ToUpperInvariant()}:{port.ToUpperInvariant()}";

        // A reference placed as several units numbers its pins per unit — A1, B1 — since every unit counts from 1 and a
        // pin's identity is the reference's (R-si1-2).
        var multiUnit = pages.SelectMany(p => p.Page.Parts).GroupBy(p => p.Reference, StringComparer.Ordinal)
                             .Where(g => g.Count() > 1 || g.Any(p => p.Unit > 1)).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        static string Letter(int unit) => unit <= 26 ? ((char)('A' + unit - 1)).ToString() : $"U{unit}";

        // Power names are the DESIGN's and off-page names their schematic's: a net alias spelling one (in any case — the
        // editor's own net table is upper-cased) is that net on every page they reach, so it becomes a global label spelled
        // as the symbol spells it — global within the one sheet a hierarchy gives a schematic.
        var powerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in pages.SelectMany(p => p.Page.Globals))
            if (library.String(g.NetIndex) is { Length: > 0 } n) powerNames.TryAdd(n, n);
        var offNames = schematics.ToDictionary(s => s, s =>
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in (hierarchical ? s.Pages : pages.Select(p => p.Page)).SelectMany(p => p.OffPages))
                if (library.String(g.NetIndex) is { Length: > 0 } n) d.TryAdd(n, n);
            return d;
        });
        ForeignLabel Named(DsnSchematic s, string name, (double X, double Y) at) =>
            powerNames.TryGetValue(name, out var spelled) || offNames[s].TryGetValue(name, out spelled)
                ? new ForeignLabel(ForeignLabelKind.Global, spelled, at.X, at.Y)
                : new ForeignLabel(ForeignLabelKind.Local, name, at.X, at.Y);

        var sheets = new List<ForeignSheet>();
        var blockCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        double pageX = 0;
        DsnSchematic? current = null;
        ForeignSheet? sheet = null;
        for (int pi = 0; pi < pages.Count; pi++)
        {
            var (schematic, page) = pages[pi];
            if (!hierarchical || schematic != current)
            {
                if (sheet is not null) sheets.Add(sheet);
                sheet = new ForeignSheet(hierarchical ? schematic.Name : page.Name);
                current = schematic;
                pageX = 0;
            }
            double toMils = page.Grid > 0 ? 100.0 / page.Grid : 1;
            double ox = pageX;
            (double X, double Y) At((int X, int Y) p) => (p.X * Mils + ox, p.Y * Mils);
            notes.AddRange(page.Skipped);
            var wiresById = page.Wires.GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.First());
            var instances = new List<ForeignInstance>();
            foreach (var part in page.Parts)
            {
                var dsn = cache.Find(part.SymbolName);
                if (dsn is null) { (cache.Mentions(part.SymbolName) ? unreadable : missing).Add(part.SymbolName); continue; }
                string? prefix = multiUnit.Contains(part.Reference) ? Letter(part.Unit) : null;
                string symbolKey = prefix is null ? dsn.Name : $"{dsn.Name}#{prefix}";
                if (!symbols.ContainsKey(symbolKey))
                {
                    symbols[symbolKey] = Symbol(dsn, symbolKey, prefix);
                    skippedGraphics += dsn.SkippedGraphics;
                    skippedText += dsn.Graphics.Count(g => g is DsnText);
                }

                var (ox0, oy0) = Map(part.Rotation, part.Mirror, 0, 0, dsn.Width, dsn.Height);
                var props = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (n, val) in part.Properties)
                    if (library.String(n) is { Length: > 0 } key && library.String(val) is { } value) props[key] = value;
                var origin = At((part.Position.X + ox0, part.Position.Y + oy0));
                instances.Add(new ForeignInstance(
                    part.Reference, prefix is null ? 1 : part.Unit, symbolKey, origin.X, origin.Y,
                    part.Rotation switch { 1 => 270, 2 => 180, 3 => 90, _ => 0 }, part.Mirror,
                    library.String(part.ValueIndex), props));

                foreach (var pin in part.Pins)
                {
                    // The page records where each pin IS; the symbol and the transform must agree with it.
                    if (pin.Index >= 1 && pin.Index <= dsn.Pins.Count)
                    {
                        var hot = dsn.Pins[pin.Index - 1].Hot;
                        var (dx, dy) = Map(part.Rotation, part.Mirror, hot.X, hot.Y, dsn.Width, dsn.Height);
                        if ((part.Position.X + dx, part.Position.Y + dy) != pin.At)
                        {
                            misplaced++;
                            if (misplacedAt.Count < 5) misplacedAt.Add($"{part.Reference}.{pin.Index} (rotation code {part.Rotation}{(part.Mirror ? ", mirrored" : "")})");
                        }
                    }
                    var pinRef = new ForeignPinRef(part.Reference, prefix + pin.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    string pinKey = $"p:{pinRef}";
                    allPins.Add((pinKey, pinRef));
                    joins.Union(pinKey, $"x:{pi}:{pin.At.X},{pin.At.Y}");
                    if (!wiresById.TryGetValue(pin.Wire, out var wire)) { if (!joins.Touches($"x:{pi}:{pin.At.X},{pin.At.Y}")) unconnected++; continue; }
                    joins.Union(pinKey, $"n:{pi}:{wire.Net}");
                    wiredPins.Add(pinKey);
                }
            }

            // Hierarchical blocks: a sheet instance of the schematic each places, its pins where the page records them.
            var blocks = new List<ForeignSheetInstance>();
            foreach (var block in page.Blocks)
            {
                string impl = library.String(block.ImplementationIndex) ?? "";
                var child = schematicOf.GetValueOrDefault(impl);
                string childName = child?.Name ?? impl;
                blockCount[childName] = blockCount.GetValueOrDefault(childName) + 1;
                var body = block.Body;
                var corners = new[] { Map(block.Rotation, block.Mirror, 0, 0, body.Width, body.Height),
                                      Map(block.Rotation, block.Mirror, body.Width, body.Height, body.Width, body.Height) };
                var topLeft = At((block.Position.X + corners.Min(c => c.X), block.Position.Y + corners.Min(c => c.Y)));
                bool turned = block.Rotation % 2 == 1;
                var pins = new List<ForeignSheetPin>();
                foreach (var pin in block.Pins)
                {
                    if (pin.Index < 1 || pin.Index > body.Pins.Count) continue;
                    string port = body.Pins[pin.Index - 1].Name;
                    var at = At(pin.At);
                    pins.Add(new ForeignSheetPin(port, at.X, at.Y));
                    string key = PortKey(child ?? schematic, port);
                    if (child is null) key = $"block:{pi}:{block.Name}:{port}";
                    joins.Union(key, $"x:{pi}:{pin.At.X},{pin.At.Y}");
                    if (wiresById.TryGetValue(pin.Wire, out var w)) joins.Union(key, $"n:{pi}:{w.Net}");
                }
                blocks.Add(new ForeignSheetInstance(block.Name, childName, topLeft.X, topLeft.Y,
                                                    (turned ? body.Height : body.Width) * Mils, (turned ? body.Width : body.Height) * Mils, pins));
            }

            var labels = new List<ForeignLabel>();
            foreach (var (g, kind) in page.Globals.Select(g => (g, ForeignLabelKind.Power))
                                         .Concat(page.OffPages.Select(g => (g, ForeignLabelKind.OffPage)))
                                         .Concat(page.Ports.Select(g => (g, ForeignLabelKind.Hierarchical))))
            {
                string net = library.String(g.NetIndex) is { Length: > 0 } n ? n : g.SymbolName;
                if (Hotspot(g, cache, wiresById, ref overruled) is not { } at)
                {
                    string what = kind switch
                    {
                        ForeignLabelKind.Power => "ground or power", ForeignLabelKind.OffPage => "off-page", _ => "hierarchical port",
                    };
                    notes.Add($"A '{net}' {what} symbol on {page.Name} touches no wire and was skipped.");
                    continue;
                }
                // A port of a flat design's page is its schematic's, reaching its other pages.
                var labelKind = kind == ForeignLabelKind.Hierarchical && !hierarchical && pages.Count > 1 ? ForeignLabelKind.Global : kind;
                labels.Add(new ForeignLabel(labelKind, net, At(at).X, At(at).Y));
                string key = kind switch
                {
                    ForeignLabelKind.Power => $"name:{net.ToUpperInvariant()}",
                    ForeignLabelKind.OffPage => OffKey(schematic, net),
                    _ => PortKey(schematic, net),
                };
                joins.Union(key, $"x:{pi}:{at.X},{at.Y}");
                if (wiresById.TryGetValue(g.Wire, out var w)) joins.Union(key, $"n:{pi}:{w.Net}");
            }
            // Net aliases (version 3) are names a wire carries; a page's own. Where the alias says is where its TEXT is,
            // which need not touch the wire — the label goes on the wire's nearest point to it.
            foreach (var a in page.Aliases)
                if (wiresById.TryGetValue(a.Wire, out var w))
                    labels.Add(Named(schematic, a.Name, At(Nearest(a.At, w.A, w.B))));
            // A version-2 page names its nets in a table only; one no symbol carries gets a label on its first wire —
            // never an auto-named one ("N02504"), which names nothing the user wrote.
            if (page.Aliases.Count == 0)
            {
                var named = labels.Select(l => l.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, net) in page.NetNames.Where(n => !named.Contains(n.Name) && !AutoName(n.Name)))
                    if (page.Wires.FirstOrDefault(w => w.Net == net) is { } w)
                        labels.Add(new ForeignLabel(ForeignLabelKind.Local, name, At(w.A).X, At(w.A).Y));
            }

            labels.AddRange(DrawnApart(page, labels, ox).Select(l => Named(schematic, l.Name, At(l.At))));

            double width = page.Width * toMils, height = page.Height * toMils;
            sheet = sheet! with { Width = ox + width, Height = Math.Max(sheet.Height, height) };
            sheet = sheet with
            {
                Instances = [.. sheet.Instances, .. instances],
                Wires = [.. sheet.Wires, .. page.Wires.Select(w => new ForeignWire([At(w.A), At(w.B)]))],
                Junctions = [.. sheet.Junctions, .. Junctions(page.Wires).Select(p => At(p)).Select(p => new ForeignJunction(p.X, p.Y))],
                Labels = [.. sheet.Labels, .. labels],
                SheetInstances = [.. sheet.SheetInstances, .. blocks],
            };
            if (hierarchical) pageX = SchematicImportBuild.NextPageX(ox, width);
        }
        if (sheet is not null) sheets.Add(sheet);

        notes.Add($"Read as a binary .DSN design, version {library.Version}; its text decoded as code page {text.CodePage}.");
        notes.Add("The file states its connectivity (each pin's wire, each wire's net); it is compared with the drawing.");
        if (hierarchical)
        {
            notes.Add($"A hierarchy of {schematics.Count} schematics with {root.Name} at the top; each becomes a sheet, its pages "
                    + "tiled left to right in the order the file stores them.");
            foreach (var s in schematics.Where(s => s != root && !blockCount.ContainsKey(s.Name)))
                notes.Add($"Schematic {s.Name} is placed by no hierarchical block.");
        }
        else if (pages.Count > 1)
            notes.Add($"{pages.Count} pages, tiled in the order the file stores them; nets cross them by their off-page connectors "
                    + "and their ground and power symbols.");
        if (skippedGraphics > 0) notes.Add($"{skippedGraphics} symbol graphic(s) of a kind this reader does not draw were left out.");
        if (skippedText > 0) notes.Add($"{skippedText} text item(s) drawn inside symbols were left out; an imported symbol carries no text.");
        foreach (var (_, page) in pages.Where(p => p.Page.Parts.Count == 0 && p.Page.Wires.Count == 0 && p.Page.Blocks.Count == 0))
            notes.Add($"Page {page.Name} draws nothing and was left out.");
        if (missing.Count > 0)
            notes.Add($"{missing.Count} symbol(s) are not in the design's cache, so their parts were skipped: {string.Join(", ", missing)}.");
        if (unreadable.Count > 0)
            notes.Add($"{unreadable.Count} symbol(s) in the design's cache hold a graphic this reader cannot read in version "
                    + $"{library.Version}, so their parts were skipped: {string.Join(", ", unreadable)}.");
        if (unconnected > 0) notes.Add($"{unconnected} pin(s) touch no wire and no other pin.");
        if (misplaced > 0)
            notes.Add($"{misplaced} pin(s) are drawn away from where the file places them, e.g. {string.Join(", ", misplacedAt)}.");
        if (overruled > 0)
            notes.Add($"{overruled} ground, power, off-page or port symbol(s) did not land on the wire the file joins them to; each "
                    + "was connected at that wire's end.");
        notes.AddRange(text.Notes());

        // A schematic placed by two blocks holds ONE set of references for both places, so its pins have no single net
        // each and the declared nets — pin sets — cannot say it; the drawing still checks every sheet.
        var repeated = blockCount.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
        if (repeated.Count > 0)
            notes.Add($"{string.Join(", ", repeated)} {(repeated.Count == 1 ? "is" : "are")} placed more than once, so the file's own "
                    + "nets are not compared; each sheet is checked against its drawing.");

        var drawn = hierarchical ? sheets : sheets.Where(s => s.Instances.Count > 0 || s.Wires.Count > 0).ToList();
        return new ForeignSchematic(Format, Path.GetFullPath(path), drawn, [.. symbols.Values], drawn[0].Name, hierarchical)
        {
            DeclaredNets = repeated.Count > 0 ? null : [.. allPins.GroupBy(p => joins.Find(p.Key))
                                      .Where(g => g.Count() > 1 || g.Any(p => wiredPins.Contains(p.Key)))
                                      .Select(g => (IReadOnlySet<ForeignPinRef>)g.Select(p => p.Pin).ToHashSet())],
            Notes = notes,
            Version = library.Version,
        };
    }

    private static bool AutoName(string name) => name.Length > 1 && name[0] == 'N' && name.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>Union-find over string keys — pins, nets, points, names.</summary>
    private sealed class Joins
    {
        private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _members = new(StringComparer.Ordinal);

        public string Find(string k)
        {
            if (!_parent.TryGetValue(k, out var p)) { _parent[k] = k; _members[k] = 1; return k; }
            if (p == k) return k;
            string root = Find(p);
            _parent[k] = root;
            return root;
        }

        public void Union(string a, string b)
        {
            string ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            _parent[ra] = rb;
            _members[rb] += _members[ra];
        }

        /// <summary>Whether anything besides <paramref name="k"/> itself has joined it.</summary>
        public bool Touches(string k) => _members[Find(k)] > 2;
    }

    /// <summary>
    /// Labels for a net the file names as ONE but draws in separate pieces: the editor joined them by a name — a net alias
    /// spelling another piece's automatic name ("N15847582") — so each piece no label reaches gets the
    /// net's name, and the import joins what the file joins.
    /// </summary>
    private static IEnumerable<(string Name, (int X, int Y) At)> DrawnApart(DsnPage page, List<ForeignLabel> labels, double ox)
    {
        var names = new Dictionary<uint, string>();
        foreach (var (name, net) in page.NetNames) names.TryAdd(net, name);
        var labelled = labels.Select(l => ((int)Math.Round((l.X - ox) / Mils), (int)Math.Round(l.Y / Mils))).ToList();
        foreach (var net in page.Wires.GroupBy(w => w.Net))
        {
            var wires = net.ToList();
            if (wires.Count < 2 || !names.TryGetValue(net.Key, out var netName)) continue;
            var parent = Enumerable.Range(0, wires.Count).ToArray();
            int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
            for (int i = 0; i < wires.Count; i++)
                for (int j = i + 1; j < wires.Count; j++)
                    if (Touch(wires[i], wires[j])) parent[Root(i)] = Root(j);
            var pieces = Enumerable.Range(0, wires.Count).GroupBy(Root).ToList();
            if (pieces.Count < 2) continue;
            foreach (var piece in pieces)
            {
                if (piece.Any(i => labelled.Any(p => OnSegment(p, wires[i].A, wires[i].B)))) continue;
                yield return (netName, wires[piece.First()].A);
            }
        }

        static bool Touch(DsnWire u, DsnWire v) =>
            OnSegment(u.A, v.A, v.B) || OnSegment(u.B, v.A, v.B) || OnSegment(v.A, u.A, u.B) || OnSegment(v.B, u.A, u.B);
    }

    /// <summary>The source's own transform of a symbol-local point (see the header).</summary>
    internal static (int X, int Y) Map(int rotation, bool mirror, int x, int y, int w, int h)
    {
        if (mirror) x = w - x;
        return rotation switch
        {
            1 => (y, w - x),
            2 => (w - x, h - y),
            3 => (h - y, x),
            _ => (x, y),
        };
    }

    /// <summary>Where a ground, power or off-page symbol connects: its symbol's pin, placed. The record also names the wire
    /// it touches; a placed pin that is not on that wire is overruled by the wire's nearer end (and counted).</summary>
    private static (int X, int Y)? Hotspot(DsnGlobal g, DsnCache cache, Dictionary<uint, DsnWire> wires, ref int overruled)
    {
        (int X, int Y)? placed = null;
        if (cache.Find(g.SymbolName) is { Pins.Count: > 0 } s)
        {
            var (dx, dy) = Map(g.Rotation, g.Mirror, s.Pins[0].Hot.X, s.Pins[0].Hot.Y, s.Width, s.Height);
            placed = (g.Position.X + dx, g.Position.Y + dy);
        }
        if (!wires.TryGetValue(g.Wire, out var w)) return placed;
        if (placed is { } p && OnSegment(p, w.A, w.B)) return p;
        if (placed is not null) overruled++;
        var near = placed ?? g.Position;
        static long D((int X, int Y) a, (int X, int Y) b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);
        return D(w.A, near) <= D(w.B, near) ? w.A : w.B;
    }

    /// <summary>The dots the editor draws and the file does not store: three or more wire ends at a point, or an end on
    /// another wire's interior.</summary>
    private static IEnumerable<(int X, int Y)> Junctions(IReadOnlyList<DsnWire> wires)
    {
        var ends = wires.SelectMany(w => new[] { w.A, w.B }).GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (p, count) in ends)
        {
            bool tee = wires.Any(w => p != w.A && p != w.B && OnSegment(p, w.A, w.B));
            if (count >= 3 || (tee && count >= 1)) yield return p;
        }
    }

    /// <summary>The point of segment <paramref name="a"/>–<paramref name="b"/> nearest <paramref name="p"/>, on the grid the
    /// segment's own ends are on (wires here are horizontal or vertical; a diagonal one gives its nearer end).</summary>
    private static (int X, int Y) Nearest((int X, int Y) p, (int X, int Y) a, (int X, int Y) b)
    {
        if (a.X == b.X) return (a.X, Math.Clamp(p.Y, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
        if (a.Y == b.Y) return (Math.Clamp(p.X, Math.Min(a.X, b.X), Math.Max(a.X, b.X)), a.Y);
        long da = (long)(p.X - a.X) * (p.X - a.X) + (long)(p.Y - a.Y) * (p.Y - a.Y);
        long db = (long)(p.X - b.X) * (p.X - b.X) + (long)(p.Y - b.Y) * (p.Y - b.Y);
        return da <= db ? a : b;
    }

    private static bool OnSegment((int X, int Y) p, (int X, int Y) a, (int X, int Y) b) =>
        (long)(b.X - a.X) * (p.Y - a.Y) == (long)(b.Y - a.Y) * (p.X - a.X)
        && p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);

    // ── symbols ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The neutral symbol, keyed <paramref name="key"/>; its pin numbers are the pins' indexes, after
    /// <paramref name="prefix"/> (a unit letter) when the part has several units.</summary>
    private static ForeignSymbol Symbol(DsnSymbol s, string key, string? prefix)
    {
        var pins = s.Pins.Select((p, i) =>
        {
            int dx = p.Start.X - p.Hot.X, dy = p.Hot.Y - p.Start.Y;   // body side, Y up
            var side = Math.Abs(dx) >= Math.Abs(dy)
                ? (dx >= 0 ? ForeignPinDirection.Right : ForeignPinDirection.Left)
                : (dy > 0 ? ForeignPinDirection.Up : ForeignPinDirection.Down);
            return new ForeignPin(prefix + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), p.Name, ForeignPinType.Unspecified,
                                  p.Hot.X * Mils, -p.Hot.Y * Mils, side, Math.Sqrt(dx * dx + dy * dy) * Mils);
        }).ToList();
        var graphics = new List<KitSymbolShape>();
        foreach (var g in s.Graphics) Shape(g, graphics);
        // A pin is itself a line, from the body (its start) out to where wires connect (its hotspot); the symbol's
        // graphics stop at the body, so without it a pin floats clear of the part.
        foreach (var p in s.Pins.Where(p => p.Start != p.Hot))
        {
            var (a, b) = (Up(p.Start.X, p.Start.Y), Up(p.Hot.X, p.Hot.Y));
            graphics.Add(new KitSymbolLine(a.X, a.Y, b.X, b.Y));
        }
        int dot = s.Name.LastIndexOf('.');
        string display = dot > 0 ? s.Name[..dot] : s.Name;
        return new ForeignSymbol(key, display, Path.GetFileName(s.Library.Replace('\\', '/')), [new ForeignSymbolUnit(1, pins, graphics)]);
    }

    private static (double X, double Y) Up(double x, double y) => (x * Mils, -y * Mils);

    private static void Shape(DsnGraphic g, List<KitSymbolShape> into)
    {
        switch (g)
        {
            case DsnLine l:
            {
                var (a, b) = (Up(l.X1, l.Y1), Up(l.X2, l.Y2));
                into.Add(new KitSymbolLine(a.X, a.Y, b.X, b.Y));
                break;
            }
            case DsnPolyline p when p.Points.Count >= 2:
            {
                var pts = p.Points.Select(q => Up(q.X, q.Y)).ToList();
                if (pts.Count == 2) { into.Add(new KitSymbolLine(pts[0].X, pts[0].Y, pts[1].X, pts[1].Y)); break; }
                into.Add(new KitSymbolPath([.. pts.SelectMany(q => new[] { q.X, q.Y })], pts[0] == pts[^1], Filled: false));
                break;
            }
            case DsnRectangle r:
            {
                var (a, b) = (Up(r.X1, r.Y1), Up(r.X2, r.Y2));
                into.Add(new KitSymbolRectangle(a.X, a.Y, b.X, b.Y, r.Filled));
                break;
            }
            case DsnPolygon p when p.Points.Count >= 3:
            {
                var pts = p.Points.Select(q => Up(q.X, q.Y)).ToList();
                into.Add(new KitSymbolPath([.. pts.SelectMany(q => new[] { q.X, q.Y })], Closed: true, p.Filled));
                break;
            }
            case DsnEllipse e:
            {
                double cx = (e.X1 + e.X2) / 2.0, cy = (e.Y1 + e.Y2) / 2.0, rx = Math.Abs(e.X2 - e.X1) / 2.0, ry = Math.Abs(e.Y2 - e.Y1) / 2.0;
                if (rx <= 0 || ry <= 0) break;
                if (Math.Abs(rx - ry) < 1e-9) { var c = Up(cx, cy); into.Add(new KitSymbolArc(c.X, c.Y, rx * Mils, 0, 360)); break; }
                into.Add(new KitSymbolPath([.. EllipsePoints(cx, cy, rx, ry, 0, 2 * Math.PI, 48).SelectMany(q => new[] { q.X, q.Y })], true, false));
                break;
            }
            case DsnArc a:
            {
                double cx = (a.X1 + a.X2) / 2.0, cy = (a.Y1 + a.Y2) / 2.0, rx = Math.Abs(a.X2 - a.X1) / 2.0, ry = Math.Abs(a.Y2 - a.Y1) / 2.0;
                if (rx <= 0 || ry <= 0) break;
                // Angles counter-clockwise ON SCREEN (Y down), i.e. with the Y axis turned up.
                double t0 = Math.Atan2(-(a.Sy - cy) / ry, (a.Sx - cx) / rx), t1 = Math.Atan2(-(a.Ey - cy) / ry, (a.Ex - cx) / rx);
                double sweep = t1 - t0;
                while (sweep <= 0) sweep += 2 * Math.PI;
                if (Math.Abs(rx - ry) < 1e-9)
                {
                    (double X, double Y) P(double t) => Up(cx + rx * Math.Cos(t), cy - ry * Math.Sin(t));
                    if (ComponentSymbolSexprReader.ArcThroughThreePoints(P(t0), P(t0 + sweep / 2), P(t0 + sweep)) is { } arc) into.Add(arc);
                    break;
                }
                into.Add(new KitSymbolPath([.. EllipsePoints(cx, cy, rx, ry, t0, sweep, 24).SelectMany(q => new[] { q.X, q.Y })], false, false));
                break;
            }
        }
    }

    private static IEnumerable<(double X, double Y)> EllipsePoints(double cx, double cy, double rx, double ry, double t0, double sweep, int n)
    {
        for (int i = 0; i <= n; i++)
        {
            double t = t0 + sweep * i / n;
            yield return Up(cx + rx * Math.Cos(t), cy - ry * Math.Sin(t));
        }
    }
}
