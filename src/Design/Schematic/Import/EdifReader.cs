// The EDIF 2.0.0 schematic reader — brief-schimp-3, overview D2/D8.
//
// Bytes → ForeignSchematic and nothing else, like every reader here. Parsed by EdifSexpr (why not PcbSexpr: that file's
// header); strings decoded by EdifText. `rename` is honoured everywhere: an object's DISPLAY name is the renamed text,
// and its identifier is what every reference names (R-si3-2). Identifiers compare case-insensitively, as the standard says.
//
// ── WHAT THE FILE IS, AS THIS READER TAKES IT (brief §2 — written from the standard) ──
//
// Libraries of cells. A cell whose view holds no `page` is a SYMBOL: its `interface` lists its `port`s (a `designator`,
// a pin-type string, `PackagePortNumbers`, `INVISIBLEPIN`, `IMPLICITPORTCLASS`), and its `contents` (or its interface's
// `symbol`) the figures and one `portImplementation` per pin — the connection dot and the lead. The lead's vector from the
// dot is the pin's direction and length; a pin with no lead is hidden. The `design` statement names the root cell, whose
// view holds the `page`s. A page's `instance`s are placed symbols, its `portImplementation`s are its power and off-page
// connectors (told apart by the connector cell's name), and its `net`s carry both the declared connectivity (`joined`)
// and the wire geometry (`figure`s).
//
// ── UNITS AND AXES ────────────────────────────────────────────────────────────────────────────
//
// One file unit is what each library's `numberDefinition` scale says (`(scale 1 (e 254 -6) (unit DISTANCE))` is
// 0.254 mm, 10 mil) — read, never assumed; a library stating none takes the first scale the file states, and a file
// stating none is refused. Everything is Y UP. Page content sits at y ≤ 0, so a SHEET point is negated into
// ForeignSchematic's Y-down sheet; a SYMBOL point stays Y up, which is ForeignSchematic's symbol convention already.
//
// ── ORIENTATION ───────────────────────────────────────────────────────────────────────────────
//
// The standard's eight orientations mirror FIRST (MX: y → −y; MY: x → −x), then turn counter-clockwise in the Y-up frame.
// ForeignInstance mirrors across the symbol's vertical axis (MY) and then turns clockwise on SCREEN — which, the picture
// being the same picture in either frame, is clockwise in Y-up too. So a turn θ is Rotation 360−θ, and MX is MY plus a
// half turn (MX = R180·MY). The table is Orientation(); EdifReaderTests holds all eight against the standard's matrices.

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Pdk;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Design.Schematic.Import;

/// <summary>How an EDIF file is read: <paramref name="CodePage"/> is the ANSI code page an escaped run is tried in after
/// UTF-8 (the file does not say which machine wrote it).</summary>
public sealed record EdifReadOptions(int CodePage = EdifText.DefaultCodePage);

public static class EdifReader
{
    /// <summary>The format's name, by its extension (overview D7).</summary>
    public const string Format = "edif";

    /// <summary>The ending of an off-page connector's cell name; any other page connector is a power port.</summary>
    public const string OffPageSuffix = "_OFF_PAGE_CONNECTOR";

    /// <summary>Reads <paramref name="path"/> (R-si3-2): the design, or a refusal naming the construct that stopped it.</summary>
    public static ForeignSchematicRead Read(string path, EdifReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new EdifReadOptions();
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ForeignSchematicRead(null, $"The file could not be read: {ex.Message}");
        }
        var decoder = new EdifText(options.CodePage);
        var parsed = EdifSexpr.Parse(EdifSexpr.TextOf(bytes, decoder));
        if (parsed.Root is not { Tag: "edif" } root)
            return new ForeignSchematicRead(null, $"{Path.GetFileName(path)} is not an EDIF file.");
        string version = string.Join(" ", root.Child("edifversion")?.Atoms ?? []);
        if (version != "2 0 0")
            return new ForeignSchematicRead(null, $"{Path.GetFileName(path)} is EDIF version {(version.Length > 0 ? version : "(unstated)")}; only EDIF 2 0 0 is read.");

        var state = new State(Path.GetFullPath(path), root, decoder);
        foreach (var d in parsed.Diagnostics) state.Note(d);
        return state.Read();
    }

    /// <summary>
    /// One of the standard's eight orientations in ForeignInstance's terms (see the header), or null for a word that is
    /// not one of them.
    /// </summary>
    internal static (int Rotation, bool Mirror)? Orientation(string? name) => (name ?? "R0").ToUpperInvariant() switch
    {
        "R0" => (0, false),
        "R90" => (270, false),
        "R180" => (180, false),
        "R270" => (90, false),
        "MX" => (180, true),
        "MY" => (0, true),
        "MXR90" => (90, true),
        "MYR90" => (270, true),
        _ => null,
    };

    /// <summary>A net name the exporter generated (<c>N00012</c>): it carries no meaning and is never drawn.</summary>
    internal static bool IsGeneratedNetName(string name) => Regex.IsMatch(name, @"^N\d+$", RegexOptions.CultureInvariant);

    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Library(string Id, string Display, PcbNode Node, double? MilsPerUnit);

    private sealed record CellDef(Library Library, string Id, string Display, PcbNode Node);

    /// <summary>One port of a symbol cell, as the cell declares and draws it (mils, Y up).</summary>
    private sealed record PortDef(
        string Id, string Name, string Designator, ForeignPinType Type, double X, double Y, ForeignPinDirection Side,
        double Length, bool Hidden, string? ImplicitNet, string[] PackageNumbers);

    /// <summary>A symbol cell read once: its ports, its drawing, how many units its <c>PackagePortNumbers</c> give it.</summary>
    private sealed record SymbolDef(string Key, string Display, string Library, IReadOnlyList<PortDef> Ports, int UnitCount,
                                    IReadOnlyList<KitSymbolShape> Graphics);

    /// <summary>A placed part as its page's nets refer to it: the pin number each port id resolved to.</summary>
    private sealed record Placed(string Reference, IReadOnlyDictionary<string, string> NumberOfPort, IReadOnlyList<ForeignPin> Pins);

    private enum NumberSource { Instance, Port, PackageNumbers, PinName, PortId }

    private sealed class State(string path, PcbNode root, EdifText text)
    {
        private readonly Dictionary<string, Library> _libraries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SymbolDef?> _symbolDefs = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ForeignSymbol> _symbols = [];
        private readonly Dictionary<string, string> _variantKeys = new(StringComparer.Ordinal);
        private readonly List<ForeignSheet> _sheets = [];
        private readonly HashSet<string> _sheetNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _sheetOfCell = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _reading = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _notes = [];
        private readonly SortedDictionary<string, int> _notCarried = new(StringComparer.Ordinal);
        private readonly int[] _numberSources = new int[5];
        private double? _fileScale;
        private int _untitled, _badOrientations, _buses, _busRefs, _symbolTexts, _implicitAdded, _implicitClassNamed;
        private string? _refusal;

        // Declared connectivity, flat designs: union-find over "P:" pins, "N:" connector names and "W:" page nets.
        private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);
        private readonly HashSet<string> _declaredPins = new(StringComparer.Ordinal);
        private readonly HashSet<(ForeignPinRef Pin, string Net)> _implicitPins = [];

        public void Note(string sentence) => _notes.Add(sentence);

        public ForeignSchematicRead Read()
        {
            foreach (var lib in root.Nodes.Where(n => n.Tag is "library" or "external"))
            {
                var (id, display) = NameOf(lib);
                double? scale = ScaleOf(lib);
                _fileScale ??= scale;
                _libraries[id] = new Library(id, display, lib, scale);
            }
            if (_fileScale is null) return Refuse("The file states no numberDefinition scale for distance, so its coordinates have no unit.");

            if (root.Child("design")?.Child("cellref") is not { } designRef)
                return Refuse("The file has no design statement naming its root cell.");
            if (Cell(designRef, null) is not { } top)
                return Refuse($"The design names cell '{designRef.Atom(0)}', which the file does not define.");
            if (Pages(top).Count == 0)
                return Refuse($"The design's cell '{top.Display}' holds no page.");

            bool hierarchical = ReadCell(top, isRoot: true);
            if (_refusal is not null) return Refuse(_refusal);

            IReadOnlyList<IReadOnlySet<ForeignPinRef>>? declared = null;
            if (!hierarchical) declared = DeclaredNets();
            else _notes.Add("A hierarchical design's declared nets span its sheets; each sheet is checked against its own drawing.");

            return new ForeignSchematicRead(new ForeignSchematic(Format, path, _sheets, _symbols, _sheets[0].Name, hierarchical)
            {
                DeclaredNets = declared,
                Notes = Notes(),
                Version = "2 0 0",
            }, null);
        }

        private ForeignSchematicRead Refuse(string why) => new(null, why);

        private List<string> Notes()
        {
            var notes = new List<string>();
            var src = _numberSources;
            var used = new[]
            {
                (src[(int)NumberSource.Instance], "the placed part's own pin designator"),
                (src[(int)NumberSource.Port], "the symbol's port designator"),
                (src[(int)NumberSource.PackageNumbers], "PackagePortNumbers"),
                (src[(int)NumberSource.PinName], "the pin's name (no number stated)"),
                (src[(int)NumberSource.PortId], "the port's identifier (no number or name stated)"),
            }.Where(x => x.Item1 > 0).Select(x => $"{x.Item1} from {x.Item2}").ToList();
            // The brief's chain starts at the placed part's own pin designator; any other source used is said.
            if (src.Skip(1).Any(n => n > 0)) notes.Add($"Pin numbers: {string.Join(", ", used)}.");
            if (_untitled > 0) notes.Add($"{_untitled} placed object(s) with no designator (title blocks, page borders) were not imported.");
            if (_badOrientations > 0) notes.Add($"{_badOrientations} placed object(s) state an orientation that is not one of the eight; each was read unturned.");
            if (_buses > 0 || _busRefs > 0)
                notes.Add($"{_buses} bus(es) and {_busRefs} bus member reference(s) were not imported: a bus is drawing, and its members connect by their own nets.");
            if (_symbolTexts > 0) notes.Add($"{_symbolTexts} text(s) drawn inside symbols were not carried: a symbol cell draws no free text.");
            if (_implicitAdded > 0)
                notes.Add($"{_implicitAdded} hidden power pin(s) are in no declared net; each was joined to the net its name gives, as it is drawn.");
            if (_implicitClassNamed > 0)
                notes.Add($"{_implicitClassNamed} hidden pin(s) state an IMPLICITPORTCLASS other than their own name; each joins the net of its name.");
            notes.AddRange(text.Notes());
            notes.AddRange(_notes);
            foreach (var (kind, count) in _notCarried) notes.Add($"{count} {kind} not imported.");
            return notes;
        }

        private void NotCarried(string kind) => _notCarried[kind] = _notCarried.GetValueOrDefault(kind) + 1;

        // ── names, numbers, properties ──────────────────────────────────────────────────────────────

        /// <summary>A named object's identifier and display name: <c>id</c>, <c>(rename id "text")</c> or <c>(name id …)</c>.</summary>
        private (string Id, string Display) NameOf(PcbNode node)
        {
            switch (node.Items.FirstOrDefault())
            {
                case string s:
                    return (s, Strip(s));
                case PcbNode { Tag: "rename" } r:
                    string id = r.Atom(0) ?? "";
                    string? shown = r.Atom(1) ?? r.Child("stringdisplay")?.Atom(0);
                    return (id, shown is null ? Strip(id) : text.Decode(shown));
                case PcbNode { Tag: "name" } n:
                    return (n.Atom(0) ?? "", Strip(n.Atom(0) ?? ""));
                default:
                    return ("", "");
            }
            static string Strip(string id) => id.StartsWith('&') ? id[1..] : id;
        }

        /// <summary>An EDIF number: an integer atom, or <c>(e mantissa exponent)</c>.</summary>
        private static double? NumberOf(object? item) => item switch
        {
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) => v,
            PcbNode { Tag: "e" } e when e.Num(0) is { } m && e.Num(1) is { } x => m * Math.Pow(10, x),
            _ => null,
        };

        /// <summary>Mils per file unit, from a library's <c>(technology (numberDefinition (scale n value (unit DISTANCE))))</c>.</summary>
        private static double? ScaleOf(PcbNode library)
        {
            foreach (var definition in library.Child("technology")?.Children("numberdefinition") ?? [])
                foreach (var scale in definition.Children("scale"))
                {
                    if (!string.Equals(scale.ChildAtom("unit"), "DISTANCE", StringComparison.OrdinalIgnoreCase)) continue;
                    var items = scale.Items.Where(x => x is string || x is PcbNode { Tag: "e" }).ToList();
                    if (items.Count >= 2 && NumberOf(items[0]) is { } units && NumberOf(items[1]) is { } metres && units != 0)
                        return metres / units / 25.4e-6;
                }
            return null;
        }

        private double Mils(Library lib, double value) => Math.Round(value * (lib.MilsPerUnit ?? _fileScale!.Value), 4) + 0.0;

        private static (double X, double Y)? Pt(PcbNode? pt) => pt?.Num(0) is { } x && pt.Num(1) is { } y ? (x, y) : null;

        private (double X, double Y) SymbolPoint(Library lib, (double X, double Y) p) => (Mils(lib, p.X), Mils(lib, p.Y));

        private (double X, double Y) SheetPoint(Library lib, (double X, double Y) p) => (Mils(lib, p.X), Mils(lib, -p.Y));

        /// <summary>A node's <c>property</c>s by display name, values as text (<c>string</c>, <c>integer</c>,
        /// <c>number</c>, <c>boolean</c>).</summary>
        private Dictionary<string, string> Props(PcbNode node)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in node.Children("property"))
            {
                var (id, display) = NameOf(p);
                string? value = null;
                foreach (var v in p.Nodes)
                {
                    value = v.Tag switch
                    {
                        "string" => v.Atom(0) is { } s ? text.Decode(s) : v.Child("stringdisplay")?.Atom(0) is { } sd ? text.Decode(sd) : "",
                        "integer" => v.Atom(0),
                        "number" => NumberOf(v.Items.FirstOrDefault())?.ToString(CultureInfo.InvariantCulture),
                        "boolean" => v.Child("true") is not null ? "true" : v.Child("false") is not null ? "false" : null,
                        _ => null,
                    };
                    if (value is not null) break;
                }
                map[display] = value ?? "";
                if (!string.Equals(id, display, StringComparison.OrdinalIgnoreCase)) map.TryAdd(id, value ?? "");
            }
            return map;
        }

        // ── cells ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>The cell a <c>cellRef</c> names, in its <c>libraryRef</c>'s library or else <paramref name="home"/>.</summary>
        private CellDef? Cell(PcbNode cellRef, Library? home)
        {
            string id = cellRef.Atom(0) ?? "";
            Library? lib = cellRef.Child("libraryref")?.Atom(0) is { } libId ? _libraries.GetValueOrDefault(libId) : home;
            IEnumerable<Library> where = lib is not null ? [lib] : _libraries.Values;
            foreach (var l in where)
                foreach (var cell in l.Node.Children("cell"))
                {
                    var (cid, display) = NameOf(cell);
                    if (string.Equals(cid, id, StringComparison.OrdinalIgnoreCase)) return new CellDef(l, cid, display, cell);
                }
            return null;
        }

        /// <summary>The view a <c>viewRef</c> names, or the cell's first.</summary>
        private PcbNode? View(CellDef cell, string? viewId)
        {
            var views = cell.Node.Children("view").ToList();
            if (viewId is not null)
                foreach (var v in views)
                    if (string.Equals(NameOf(v).Id, viewId, StringComparison.OrdinalIgnoreCase)) return v;
            return views.FirstOrDefault();
        }

        private List<PcbNode> Pages(CellDef cell, string? viewId = null) =>
            [.. View(cell, viewId)?.Child("contents")?.Children("page") ?? []];

        // ── sheets ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>Reads a cell's pages as sheets — every page of the root of a flat design, or one sheet per hierarchical
        /// cell — then the cells its sheet instances show. True when the design turned out hierarchical.</summary>
        private bool ReadCell(CellDef cell, bool isRoot, string? viewId = null)
        {
            var pages = Pages(cell, viewId);
            var portNames = isRoot ? new HashSet<string>() : PortNamesOf(cell, viewId);
            var children = new List<(CellDef Cell, string? View)>();
            string self = cell.Library.Id + ":" + cell.Id;
            _reading.Add(self);
            int first = _sheets.Count;
            foreach (var page in pages)
            {
                string name = UniqueSheetName(pages.Count == 1 ? cell.Display : NameOf(page).Display);
                _sheets.Add(ReadPage(page, cell.Library, name, portNames, children));
                if (_refusal is not null) return false;
            }

            bool hierarchical = children.Count > 0 || !isRoot;
            if (hierarchical && pages.Count > 1)
            {
                _refusal = $"The cell '{cell.Display}' is a sheet of a hierarchical design and has {pages.Count} pages; "
                         + "a hierarchical sheet of more than one page is not read.";
                return false;
            }
            _sheetOfCell[self] = _sheets[first].Name;

            foreach (var (child, view) in children)
            {
                string key = child.Library.Id + ":" + child.Id;
                if (_reading.Contains(key)) { _refusal = $"The sheet cell '{child.Display}' contains itself: the hierarchy has a loop."; return false; }
                if (_sheetOfCell.ContainsKey(key)) continue;
                ReadCell(child, isRoot: false, view);
                if (_refusal is not null) return false;
            }
            _reading.Remove(self);
            if (children.Count > 0) FixSheetNames();
            return hierarchical;
        }

        private string UniqueSheetName(string stem)
        {
            string n = stem.Length > 0 ? stem : "page";
            for (int k = 2; !_sheetNames.Add(n); k++) n = $"{stem}_{k}";
            return n;
        }

        /// <summary>A hierarchical cell's port names (display), which its page connectors of the same name implement.</summary>
        private HashSet<string> PortNamesOf(CellDef cell, string? viewId)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var port in View(cell, viewId)?.Child("interface")?.Children("port") ?? [])
            {
                var (id, display) = NameOf(port);
                names.Add(display);
                names.Add(id);
            }
            return names;
        }

        /// <summary>Sheet instances name their child sheet by its cell, resolved after the child is read.</summary>
        private void FixSheetNames()
        {
            for (int s = 0; s < _sheets.Count; s++)
            {
                if (_sheets[s].SheetInstances.Count == 0) continue;
                _sheets[s] = _sheets[s] with
                {
                    SheetInstances = [.. _sheets[s].SheetInstances.Select(si => si with { SheetName = _sheetOfCell.GetValueOrDefault(si.SheetName, si.SheetName) })],
                };
            }
        }

        private ForeignSheet ReadPage(PcbNode page, Library lib, string name, HashSet<string> portNames, List<(CellDef, string?)> children)
        {
            var instances = new List<ForeignInstance>();
            var wires = new List<ForeignWire>();
            var junctions = new List<ForeignJunction>();
            var labels = new List<ForeignLabel>();
            var texts = new List<ForeignText>();
            var graphics = new List<ForeignGraphic>();
            var sheetInstances = new List<ForeignSheetInstance>();
            var placed = new Dictionary<string, Placed>(StringComparer.OrdinalIgnoreCase);
            var connectors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // id → display name
            string pageKey = name;

            // Nets last: their portRefs name instances and connectors wherever on the page those are written.
            var nameNode = page.Items.FirstOrDefault() as PcbNode;   // the page's own (rename …), not its content
            foreach (var node in page.Nodes.Where(n => n.Tag != "net" && !ReferenceEquals(n, nameNode)).Concat(page.Children("net")))
            {
                switch (node.Tag)
                {
                    case "instance":
                        PlaceInstance(node, lib, instances, sheetInstances, placed, children);
                        break;
                    case "portimplementation":
                        if (Connector(node, lib, portNames) is { } label)
                        {
                            labels.Add(label);
                            connectors[NameOf(node).Id] = label.Text;
                        }
                        break;
                    case "net":
                        ReadNet(node, lib, pageKey, wires, junctions, labels, placed, connectors);
                        break;
                    case "netbundle":
                        _buses++;
                        break;
                    case "commentgraphics":
                        CommentGraphics(node, lib, texts, graphics);
                        break;
                    case "figure":
                        SheetFigure(node, lib, graphics);
                        break;
                    case "pagesize" or "boundingbox" or "property" or "comment" or "userdata" or "documentation" or "pagetitle":
                        break;
                    default:
                        NotCarried($"page construct(s) '{node.Tag}' were");
                        break;
                }
            }
            if (_refusal is not null) return new ForeignSheet(name);
            return new ForeignSheet(name)
            {
                Instances = instances, Wires = wires, Junctions = junctions, Labels = labels, Texts = texts, Graphics = graphics,
                SheetInstances = sheetInstances,
            };
        }

        // ── placed parts ────────────────────────────────────────────────────────────────────────────

        private void PlaceInstance(PcbNode node, Library lib, List<ForeignInstance> instances, List<ForeignSheetInstance> sheetInstances,
                                   Dictionary<string, Placed> placed, List<(CellDef, string?)> children)
        {
            var (instId, instDisplay) = NameOf(node);
            var viewRef = node.Child("viewref");
            if (viewRef?.Child("cellref") is not { } cellRef)
            {
                NotCarried("placed object(s) naming no cell were");
                return;
            }
            if (Cell(cellRef, lib) is not { } cell)
            {
                _notes.Add($"'{instDisplay}' places cell '{cellRef.Atom(0)}', which the file does not define; it was not imported.");
                return;
            }
            string? viewId = viewRef.Atom(0);
            var transform = node.Child("transform");
            var origin = SheetPoint(lib, Pt(transform?.Child("origin")?.Child("pt")) ?? (0, 0));
            var orientation = Orientation(transform?.ChildAtom("orientation"));
            if (orientation is null) _badOrientations++;
            var (rotation, mirror) = orientation ?? (0, false);
            string designator = node.ChildAtom("designator") is { } d ? text.Decode(d).Trim() : "";

            if (Pages(cell, viewId).Count > 0)
            {
                sheetInstances.Add(SheetInstance(cell, viewId, designator.Length > 0 ? designator : instDisplay, origin, rotation, mirror));
                children.Add((cell, viewId));
                return;
            }
            if (designator.Length == 0) { _untitled++; return; }

            var props = Props(node);
            int unit = props.TryGetValue("PositionInPackage", out var pos) && int.TryParse(pos, CultureInfo.InvariantCulture, out int p0) ? p0 + 1 : 0;
            string suffix = props.GetValueOrDefault("Designator") ?? "";
            if (unit == 0) unit = suffix.Length == 1 && char.IsAsciiLetter(suffix[0]) ? char.ToUpperInvariant(suffix[0]) - 'A' + 1 : 1;
            string reference = suffix.Length > 0 && designator.Length > suffix.Length && designator.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? designator[..^suffix.Length]
                : designator;

            if (SymbolOf(cell, viewId) is not { } def)
            {
                _notes.Add($"{designator}: its symbol '{cell.Display}' has no ports; it was not imported.");
                return;
            }
            if (unit > def.UnitCount && def.UnitCount > 1)
                _notes.Add($"{designator} is unit {unit} of '{def.Display}', which has {def.UnitCount}; it was read as unit {def.UnitCount}.");
            unit = Math.Clamp(unit, 1, def.UnitCount);

            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pi in node.Children("portinstance"))
                if (pi.ChildAtom("designator") is { } od && text.Decode(od).Trim() is { Length: > 0 } number)
                    overrides[NameOf(pi).Id] = number;

            var numberOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pins = new List<ForeignPin>();
            foreach (var port in def.Ports)
            {
                var (number, source) = overrides.TryGetValue(port.Id, out var o) ? (o, NumberSource.Instance) : NumberFor(port, unit);
                _numberSources[(int)source]++;
                numberOf[port.Id] = number;
                pins.Add(PinOf(port, number));
            }
            string key = KeyFor(def, unit, pins);
            placed[instId] = new Placed(reference, numberOf, pins);
            foreach (var pin in pins.Where(p => p.Hidden && p.ImplicitNet is not null))
                _implicitPins.Add((new ForeignPinRef(reference, pin.Number), pin.ImplicitNet!));
            instances.Add(new ForeignInstance(reference, unit, key, origin.X, origin.Y, rotation, mirror,
                                              props.GetValueOrDefault("Value"), props));
        }

        /// <summary>The brief's fallback chain past the placed part's own designator: the port's designator, the unit's entry in
        /// <c>PackagePortNumbers</c>, the pin's name, the port's identifier.</summary>
        private static (string Number, NumberSource Source) NumberFor(PortDef port, int unit)
        {
            if (port.Designator.Length > 0) return (port.Designator, NumberSource.Port);
            if (unit - 1 < port.PackageNumbers.Length && port.PackageNumbers[unit - 1].Length > 0)
                return (port.PackageNumbers[unit - 1], NumberSource.PackageNumbers);
            if (port.Name.Length > 0) return (port.Name, NumberSource.PinName);
            return (port.Id.TrimStart('&'), NumberSource.PortId);
        }

        private static ForeignPin PinOf(PortDef port, string number) =>
            new(number, port.Name, port.Type, port.X, port.Y, port.Side, port.Length, port.Hidden, port.ImplicitNet);

        /// <summary>The ForeignSymbol a placed unit draws: the cell's own when its pins are numbered as the cell numbers them,
        /// else a variant (a placed part that renumbers a pin), one per distinct numbering.</summary>
        private string KeyFor(SymbolDef def, int unit, List<ForeignPin> pins)
        {
            if (_symbols.All(s => s.Key != def.Key))
                _symbols.Add(new ForeignSymbol(def.Key, def.Display, def.Library,
                    [.. Enumerable.Range(1, def.UnitCount).Select(u => new ForeignSymbolUnit(u,
                        [.. def.Ports.Select(p => PinOf(p, NumberFor(p, u).Number))], def.Graphics))]));
            var own = _symbols.First(s => s.Key == def.Key).Unit(unit)!;
            if (own.Pins.Select(p => p.Number).SequenceEqual(pins.Select(p => p.Number))) return def.Key;

            string signature = def.Key + "\u0001" + unit + "\u0001" + string.Join("\u0001", pins.Select(p => p.Number));
            if (_variantKeys.TryGetValue(signature, out var key)) return key;
            key = def.Key;
            for (int k = 2; _symbols.Any(s => s.Key == key); k++) key = $"{def.Key}#{k}";
            _variantKeys[signature] = key;
            _symbols.Add(new ForeignSymbol(key, def.Display, def.Library,
                [.. Enumerable.Range(1, def.UnitCount).Select(u => u == unit ? new ForeignSymbolUnit(u, pins, def.Graphics)
                                                                             : new ForeignSymbolUnit(u, [.. def.Ports.Select(p => PinOf(p, NumberFor(p, u).Number))], def.Graphics))]));
            return key;
        }

        /// <summary>A hierarchical block: its pins where the cell's own symbol puts them, turned and placed like a part.</summary>
        private ForeignSheetInstance SheetInstance(CellDef cell, string? viewId, string name, (double X, double Y) origin, int rotation, bool mirror)
        {
            var view = View(cell, viewId)!;
            var rot = RotationOf(rotation);
            (double X, double Y) Place((double X, double Y) local)
            {
                var (x, y) = SchematicGeometry.LocalToWorld((float)local.X, (float)-local.Y, origin.X, origin.Y, rot, mirror);
                return (Math.Round(x, 4) + 0.0, Math.Round(y, 4) + 0.0);
            }

            var pins = new List<ForeignSheetPin>();
            var points = new List<(double X, double Y)>();
            var portName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var port in view.Child("interface")?.Children("port") ?? []) portName.TryAdd(NameOf(port).Id, NameOf(port).Display);
            foreach (var pi in SymbolScopes(view).SelectMany(s => s.Children("portimplementation")))
                if (ConnectPoint(pi) is { } c)
                {
                    var at = Place(SymbolPoint(cell.Library, c));
                    pins.Add(new ForeignSheetPin(portName.GetValueOrDefault(NameOf(pi).Id, NameOf(pi).Display), at.X, at.Y));
                    points.Add(at);
                }
            foreach (var shape in SymbolScopes(view).SelectMany(s => s.Children("figure")).SelectMany(f => f.Nodes))
                foreach (var p in shape.Descendants("pt")) if (Pt(p) is { } q) points.Add(Place(SymbolPoint(cell.Library, q)));
            if (pins.Count == 0) _notes.Add($"The sheet '{name}' draws no block pins; its ports have nothing to connect to.");
            if (points.Count == 0) points.Add(origin);
            double x0 = points.Min(p => p.X), y0 = points.Min(p => p.Y);
            return new ForeignSheetInstance(name, cell.Library.Id + ":" + cell.Id, x0, y0, points.Max(p => p.X) - x0, points.Max(p => p.Y) - y0, pins);
        }

        // ── symbols ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>Where a symbol's figures and port implementations are: its view's <c>contents</c> and its interface's
        /// <c>symbol</c>, whichever the exporter used.</summary>
        private static IEnumerable<PcbNode> SymbolScopes(PcbNode view)
        {
            if (view.Child("interface")?.Child("symbol") is { } symbol) yield return symbol;
            if (view.Child("contents") is { } contents) yield return contents;
        }

        private SymbolDef? SymbolOf(CellDef cell, string? viewId)
        {
            var view = View(cell, viewId);
            string cacheKey = $"{cell.Library.Id}:{cell.Id}:{(view is null ? "" : NameOf(view).Id)}";
            if (_symbolDefs.TryGetValue(cacheKey, out var cached)) return cached;
            var def = view is null ? null : ReadSymbol(cell, view);
            _symbolDefs[cacheKey] = def;
            return def;
        }

        private SymbolDef? ReadSymbol(CellDef cell, PcbNode view)
        {
            var implementations = new Dictionary<string, PcbNode>(StringComparer.OrdinalIgnoreCase);
            var graphics = new List<KitSymbolShape>();
            foreach (var scope in SymbolScopes(view))
                foreach (var node in scope.Nodes)
                    switch (node.Tag)
                    {
                        case "portimplementation": implementations.TryAdd(NameOf(node).Id, node); break;
                        case "figure": Shapes(node, cell.Library, graphics); break;
                        case "annotate": _symbolTexts++; break;
                        case "commentgraphics":
                            foreach (var f in node.Children("figure")) Shapes(f, cell.Library, graphics);
                            _symbolTexts += node.Children("annotate").Count();
                            break;
                    }

            var ports = new List<PortDef>();
            foreach (var port in view.Child("interface")?.Nodes ?? [])
            {
                if (port.Tag == "portbundle") { _buses++; continue; }
                if (port.Tag != "port") continue;
                var (id, display) = NameOf(port);
                var props = Props(port);
                string designator = port.ChildAtom("designator") is { } d ? text.Decode(d).Trim() : "";
                string typeText = props.GetValueOrDefault("PINTYPE") ?? props.GetValueOrDefault("PinType") ?? props.GetValueOrDefault("Type")
                                  ?? port.ChildAtom("direction") ?? "";
                var type = PinType(typeText);
                string[] package = props.TryGetValue("PackagePortNumbers", out var ppn)
                    ? [.. ppn.Split(',').Select(s => s.Trim())]
                    : [];

                (double X, double Y) at = (0, 0);
                var side = ForeignPinDirection.Right;
                double length = 0;
                bool hasLead = false;
                if (implementations.TryGetValue(id, out var impl) && ConnectPoint(impl) is { } c)
                {
                    at = SymbolPoint(cell.Library, c);
                    if (Lead(impl, c) is { } far)
                    {
                        var f = SymbolPoint(cell.Library, far);
                        double dx = f.X - at.X, dy = f.Y - at.Y;
                        length = Math.Round(Math.Sqrt(dx * dx + dy * dy), 4);
                        side = Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? ForeignPinDirection.Right : ForeignPinDirection.Left)
                                                            : (dy >= 0 ? ForeignPinDirection.Up : ForeignPinDirection.Down);
                        hasLead = length > 0;
                    }
                }
                else _notes.Add($"'{cell.Display}' declares port '{display}' but does not draw it; it was placed at the symbol's origin.");

                bool hidden = !hasLead || IsTrue(props.GetValueOrDefault("INVISIBLEPIN"));
                string? implicitNet = null;
                if (hidden && display.Length > 0 && (props.TryGetValue("IMPLICITPORTCLASS", out var implicitClass) || type == ForeignPinType.PowerIn))
                {
                    implicitNet = display;
                    if (implicitClass is { Length: > 0 } && !string.Equals(implicitClass, display, StringComparison.OrdinalIgnoreCase)) _implicitClassNamed++;
                }
                ports.Add(new PortDef(id, display, designator, type, at.X, at.Y, side, length, hidden, implicitNet, package));
            }
            if (ports.Count == 0) return null;

            int units = Math.Max(1, ports.Max(p => p.PackageNumbers.Length));
            string key = $"{cell.Library.Display}:{cell.Display}";
            if (cell.Node.Children("view").Count() > 1) key += ":" + NameOf(view).Display;
            return new SymbolDef(key, cell.Display, cell.Library.Display, ports, units, graphics);
        }

        private static bool IsTrue(string? v) => v is not null && (v.Length == 0 || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                                                   || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase));

        /// <summary>The pin-type strings of the format (brief §2), and the standard's own <c>direction</c> words. Unknown → passive.</summary>
        private static ForeignPinType PinType(string s) => s.Trim().ToUpperInvariant() switch
        {
            "PAS" or "PASSIVE" => ForeignPinType.Passive,
            "IO" or "INOUT" or "BIDIRECTIONAL" => ForeignPinType.Bidirectional,
            "IN" or "INPUT" or "DBO_IN" => ForeignPinType.Input,
            "OUT" or "OUTPUT" or "DBO_OUT" => ForeignPinType.Output,
            "POWER" or "PWR" => ForeignPinType.PowerIn,
            "HIZ" or "3ST" or "TRISTATE" => ForeignPinType.TriState,
            "OC" => ForeignPinType.OpenCollector,
            "OE" => ForeignPinType.OpenEmitter,
            _ => ForeignPinType.Passive,
        };

        /// <summary>A port implementation's connection dot, in file units.</summary>
        private static (double X, double Y)? ConnectPoint(PcbNode impl) =>
            impl.Child("connectlocation") is { } cl ? Pt(cl.Descendants("pt").FirstOrDefault()) : null;

        /// <summary>The lead's far end: the point of the implementation's own figures farthest from the dot.</summary>
        private static (double X, double Y)? Lead(PcbNode impl, (double X, double Y) dot)
        {
            (double X, double Y)? far = null;
            double best = 0;
            foreach (var figure in impl.Children("figure"))
                foreach (var p in figure.Descendants("pt"))
                    if (Pt(p) is { } q && (q.X - dot.X) * (q.X - dot.X) + (q.Y - dot.Y) * (q.Y - dot.Y) is var d2 && d2 > best)
                    {
                        best = d2;
                        far = q;
                    }
            return far;
        }

        /// <summary>A symbol figure's shapes, mils Y up, in Core's vocabulary.</summary>
        private void Shapes(PcbNode figure, Library lib, List<KitSymbolShape> into)
        {
            List<double> Flat(IEnumerable<(double X, double Y)> pts) => [.. pts.SelectMany(p => { var m = SymbolPoint(lib, p); return new[] { m.X, m.Y }; })];
            foreach (var shape in figure.Nodes)
            {
                switch (shape.Tag)
                {
                    case "path" or "polygon":
                    {
                        var pts = PointList(shape);
                        if (pts.Count < 2) break;
                        bool closed = shape.Tag == "polygon";
                        if (closed && pts.Count > 2 && pts[0] == pts[^1]) pts.RemoveAt(pts.Count - 1);
                        if (pts.Count == 2 && !closed)
                        {
                            var (a, b) = (SymbolPoint(lib, pts[0]), SymbolPoint(lib, pts[1]));
                            into.Add(new KitSymbolLine(a.X, a.Y, b.X, b.Y));
                        }
                        else into.Add(new KitSymbolPath(Flat(pts), closed, Filled: false));
                        break;
                    }
                    case "rectangle":
                    {
                        var pts = shape.Children("pt").Select(Pt).OfType<(double X, double Y)>().ToList();
                        if (pts.Count < 2) break;
                        var (a, b) = (SymbolPoint(lib, pts[0]), SymbolPoint(lib, pts[1]));
                        into.Add(new KitSymbolRectangle(a.X, a.Y, b.X, b.Y, false));
                        break;
                    }
                    case "circle":
                    {
                        // Two points, the ends of a diameter.
                        var pts = shape.Children("pt").Select(Pt).OfType<(double X, double Y)>().ToList();
                        if (pts.Count < 2) break;
                        var (a, b) = (SymbolPoint(lib, pts[0]), SymbolPoint(lib, pts[1]));
                        double r = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) / 2;
                        if (r > 0) into.Add(new KitSymbolArc((a.X + b.X) / 2, (a.Y + b.Y) / 2, r, 0, 360));
                        break;
                    }
                    case "openshape" or "shape":
                        Curve(shape.Child("curve"), lib, shape.Tag == "shape", into);
                        break;
                    case "dot" or "figuregroupoverride":
                        break;
                    default:
                        NotCarried($"symbol figure(s) '{shape.Tag}' were");
                        break;
                }
            }
        }

        /// <summary>A <c>curve</c>: points joined by straight runs, and three-point <c>arc</c>s.</summary>
        private void Curve(PcbNode? curve, Library lib, bool closed, List<KitSymbolShape> into)
        {
            if (curve is null) return;
            (double X, double Y)? first = null, last = null;
            void LineTo((double X, double Y) p)
            {
                if (last is { } l && l != p) into.Add(new KitSymbolLine(l.X, l.Y, p.X, p.Y));
                first ??= p;
                last = p;
            }
            foreach (var item in curve.Nodes)
            {
                if (item.Tag == "pt" && Pt(item) is { } q) LineTo(SymbolPoint(lib, q));
                else if (item.Tag == "arc")
                {
                    var pts = item.Children("pt").Select(Pt).OfType<(double X, double Y)>().Select(p => SymbolPoint(lib, p)).ToList();
                    if (pts.Count < 3) continue;
                    LineTo(pts[0]);
                    if (ComponentSymbolSexprReader.ArcThroughThreePoints(pts[0], pts[1], pts[2]) is { } arc) into.Add(arc);
                    else into.Add(new KitSymbolLine(pts[0].X, pts[0].Y, pts[2].X, pts[2].Y));
                    last = pts[2];
                }
            }
            if (closed && first is { } f) LineTo(f);
        }

        private static List<(double X, double Y)> PointList(PcbNode shape) =>
            [.. (shape.Child("pointlist")?.Children("pt") ?? []).Select(Pt).OfType<(double X, double Y)>()];

        // ── connectors, nets, comments ──────────────────────────────────────────────────────────────

        /// <summary>A page connector: an off-page connector (its cell's name ends in <see cref="OffPageSuffix"/>), a
        /// hierarchical port of the sheet's own cell, or a power port — each a label named by the connector.</summary>
        private ForeignLabel? Connector(PcbNode node, Library lib, HashSet<string> portNames)
        {
            var (id, display) = NameOf(node);
            if (ConnectPoint(node) is not { } c || display.Length == 0)
            {
                NotCarried("page connector(s) with no connection point or name were");
                return null;
            }
            var at = SheetPoint(lib, c);
            string cellName = node.Child("instance")?.Child("viewref")?.Child("cellref")?.Atom(0) ?? "";
            var kind = cellName.EndsWith(OffPageSuffix, StringComparison.OrdinalIgnoreCase) ? ForeignLabelKind.OffPage
                     : portNames.Contains(id) || portNames.Contains(display) ? ForeignLabelKind.Hierarchical
                     : ForeignLabelKind.Power;
            return new ForeignLabel(kind, display, at.X, at.Y);
        }

        private void ReadNet(PcbNode net, Library lib, string pageKey, List<ForeignWire> wires, List<ForeignJunction> junctions,
                             List<ForeignLabel> labels, Dictionary<string, Placed> placed, Dictionary<string, string> connectors)
        {
            var (netId, display) = NameOf(net);
            string netKey = $"W:{pageKey}:{netId}:{_parent.Count}";
            Find(netKey);
            foreach (var portRef in net.Child("joined")?.Nodes ?? [])
            {
                if (portRef.Tag != "portref") { if (portRef.Tag is "portlist") _busRefs++; continue; }
                if (portRef.Items.FirstOrDefault() is PcbNode { Tag: "member" }) { _busRefs++; continue; }
                string portId = portRef.Atom(0) ?? "";
                if (portRef.Child("instanceref")?.Atom(0) is { } instId)
                {
                    if (!placed.TryGetValue(instId, out var part)) continue;   // a title block, a sheet instance, an unread part
                    if (!part.NumberOfPort.TryGetValue(portId, out var number)) continue;
                    string pin = PinKey(part.Reference, number);
                    _declaredPins.Add(pin);
                    Union(netKey, pin);
                }
                else
                    Union(netKey, "N:" + (connectors.GetValueOrDefault(portId) ?? portId.TrimStart('&')));
            }

            var netWires = new List<ForeignWire>();
            foreach (var figure in net.Children("figure"))
                foreach (var shape in figure.Nodes)
                {
                    if (shape.Tag is "path" or "polyline" && PointList(shape) is { Count: >= 2 } pts)
                        netWires.Add(new ForeignWire([.. pts.Select(p => SheetPoint(lib, p))]));
                    else if (shape.Tag == "dot" && Pt(shape.Child("pt")) is { } d)
                    {
                        var j = SheetPoint(lib, d);
                        junctions.Add(new ForeignJunction(j.X, j.Y));
                    }
                }
            wires.AddRange(netWires);

            // A named net is labelled once per connected group of its wires, which is how the editor joins an alias's
            // separate stretches on one page; a generated name is never drawn.
            if (!IsGeneratedNetName(display) && display.Length > 0)
                foreach (var group in WireGroups(netWires))
                    labels.Add(new ForeignLabel(ForeignLabelKind.Local, display, group.Points[0].X, group.Points[0].Y));

            foreach (var sub in net.Children("net")) ReadNet(sub, lib, pageKey, wires, junctions, labels, placed, connectors);
        }

        /// <summary>The first wire of each touching group (a shared point, or an end on another's segment).</summary>
        private static IEnumerable<ForeignWire> WireGroups(List<ForeignWire> wires)
        {
            int[] parent = [.. Enumerable.Range(0, wires.Count)];
            int F(int x) => parent[x] == x ? x : parent[x] = F(parent[x]);
            bool Touch(ForeignWire a, ForeignWire b) =>
                new[] { a.Points[0], a.Points[^1] }.Any(p => OnWire(p, b)) || new[] { b.Points[0], b.Points[^1] }.Any(p => OnWire(p, a));
            static bool OnWire((double X, double Y) p, ForeignWire w)
            {
                for (int i = 0; i + 1 < w.Points.Count; i++)
                    if (SchematicGeometry.PointOnSegment(p.X, p.Y, w.Points[i].X, w.Points[i].Y, w.Points[i + 1].X, w.Points[i + 1].Y, 0.01)) return true;
                return false;
            }
            for (int i = 0; i < wires.Count; i++)
                for (int k = i + 1; k < wires.Count; k++)
                    if (Touch(wires[i], wires[k])) parent[F(i)] = F(k);
            return wires.Where((_, i) => Enumerable.Range(0, i).All(k => F(k) != F(i)));
        }

        private void CommentGraphics(PcbNode node, Library lib, List<ForeignText> texts, List<ForeignGraphic> graphics)
        {
            foreach (var item in node.Nodes)
            {
                if (item.Tag == "annotate" && item.Child("stringdisplay") is { } sd && sd.Atom(0) is { } raw)
                {
                    var origin = sd.Descendants("origin").FirstOrDefault()?.Child("pt");
                    var at = SheetPoint(lib, Pt(origin) ?? (0, 0));
                    string t = text.Decode(raw);
                    if (t.Length > 0) texts.Add(new ForeignText(t, at.X, at.Y));
                }
                else if (item.Tag == "figure") SheetFigure(item, lib, graphics);
                else NotCarried($"comment graphic(s) '{item.Tag}' were");
            }
        }

        private void SheetFigure(PcbNode figure, Library lib, List<ForeignGraphic> graphics)
        {
            foreach (var shape in figure.Nodes)
            {
                switch (shape.Tag)
                {
                    case "path" or "polygon" when PointList(shape) is { Count: >= 2 } pts:
                        graphics.Add(new ForeignGraphic([.. pts.Select(p => SheetPoint(lib, p))], shape.Tag == "polygon"));
                        break;
                    case "rectangle" when shape.Children("pt").Select(Pt).OfType<(double X, double Y)>().ToList() is { Count: 2 } r:
                        var (a, b) = (SheetPoint(lib, r[0]), SheetPoint(lib, r[1]));
                        graphics.Add(new ForeignGraphic([a, (b.X, a.Y), b, (a.X, b.Y)], Closed: true));
                        break;
                    case "figuregroupoverride":
                        break;
                    default:
                        NotCarried($"sheet figure(s) '{shape.Tag}' were");
                        break;
                }
            }
        }

        // ── declared connectivity ───────────────────────────────────────────────────────────────────

        private static string PinKey(string reference, string number) => "P:" + reference + "\u0001" + number;

        private string Find(string x)
        {
            if (!_parent.TryGetValue(x, out var p)) { _parent[x] = x; return x; }
            if (p == x) return x;
            string r = Find(p);
            _parent[x] = r;
            return r;
        }

        private void Union(string a, string b)
        {
            string ra = Find(a), rb = Find(b);
            if (ra != rb) _parent[ra] = rb;
        }

        /// <summary>The file's own nets as pin sets (overview D8): every page net with its pins, joined across pages through
        /// connectors of the same name. A hidden power pin no net lists joins the net its name gives.</summary>
        private List<IReadOnlySet<ForeignPinRef>> DeclaredNets()
        {
            foreach (var (pin, net) in _implicitPins)
            {
                string key = PinKey(pin.Reference, pin.PinNumber);
                if (_declaredPins.Contains(key)) continue;
                _declaredPins.Add(key);
                Union(key, "N:" + net);
                _implicitAdded++;
            }
            return [.. _parent.Keys.Where(k => k.StartsWith("P:", StringComparison.Ordinal))
                .GroupBy(Find)
                .Select(g => (IReadOnlySet<ForeignPinRef>)g.Select(k =>
                {
                    int sep = k.IndexOf('\u0001');
                    return new ForeignPinRef(k[2..sep], k[(sep + 1)..]);
                }).ToHashSet())];
        }
    }

    private static SymbolRotation RotationOf(int degrees) => degrees switch
    {
        90 => SymbolRotation.R90,
        180 => SymbolRotation.R180,
        270 => SymbolRotation.R270,
        _ => SymbolRotation.R0,
    };

    /// <summary>Every list below <paramref name="node"/> tagged <paramref name="tag"/>, depth first.</summary>
    private static IEnumerable<PcbNode> Descendants(this PcbNode node, string tag)
    {
        var stack = new Stack<PcbNode>([node]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var c in n.Nodes.Reverse())
            {
                if (c.Tag == tag) yield return c;
                stack.Push(c);
            }
        }
    }
}
