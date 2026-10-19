# Import Schematic

**Status:** SI-1 implemented (the neutral model, the map, the build, the check, the two write paths); SI-2 implemented
(the `.kicad_sch` reader, §8). Readers still to come: SI-3 (EDIF 2.0.0), SI-4 (the binary `.DSN` design in a CFBF container). Entry points: SI-5. Source of
decisions: `docs/sonnet-briefs/brief-schimp-0-overview.md` (D1–D11). Each phase keeps this note current.

## 1. The pipeline

```
file ──SchematicFileClassifier (content, never extension)──▶ reader ──▶ ForeignSchematic   (bytes → neutral model)
                                                                          │
                                       SchematicImportMap: each part → native component, ground, or symbol cell
                                                                          │
                                       SchematicImportBuild: nets from geometry → TestBench + ExactPlacement
                                                                          │  NetlistSchematic.Build
                         ┌────────────────────────────────────────────────┴───────────────────────┐
              SchematicImport.Import (new cell)                          SchematicImport.BuildInto (open schematic)
                         └──────────────── ImportConnectivity.Compare, every import ──────────────┘
```

Everything lives in `src/Design/Schematic/Import/` (namespace `CircuitRF.Design.Schematic.Import`). A reader touches
nothing but bytes; only `SchematicImport` writes. The GUI command, the drop, the CLI noun and the MCP tool call one of
its two functions and hold no import logic (D1).

## 2. The neutral model — `ForeignSchematic.cs`

| Record | Holds |
|---|---|
| `ForeignSymbol` | key (unique in the source), display and library names, units; per unit pins and graphics |
| `ForeignPin` | `Number` (the identity), `Name`, electrical type, connection point, body side, length, `Hidden`, `ImplicitNet` |
| `ForeignSheet` | name, page size, `Instances`, `Wires`, `Junctions`, `Labels`, `NoConnects`, `SheetInstances`, `Texts`, `Graphics` |
| `ForeignInstance` | reference, unit (from 1), symbol key, origin, rotation 0/90/180/270, mirror, value, properties |
| `ForeignLabel` | kind `Local / Global / Hierarchical / OffPage / Power`, text, anchor |
| `ForeignSchematic` | format (by extension), source path, sheets, symbols, root sheet, `Hierarchical`, `DeclaredNets`, `Notes` |

**Two coordinate conventions.** Symbol geometry is mils, **Y up** (the symbol formats' own handedness, and
`ComponentPart`'s), so the one flip in `ComponentImport.BuildSymbol` applies once. Sheet contents are mils, **Y down** —
a sheet point is a schematic point (one schematic unit is one mil, D6). A pin lands at
`SchematicGeometry.LocalToWorld((x, −y), origin, rotation, mirror)`: mirror across the symbol's vertical axis first, then
quarter turns clockwise on screen. A reader whose format composes differently converts to this.

**Pin numbers are the identity** (`ForeignPinRef(reference, pinNumber)`), never names (`GND` repeats) or a format's port
ids. A multi-unit part is one reference; unit *n* is placed as instance `<ref><letter>` (`U1A`, `U1B`) and its pins map
back to the reference's numbers. A pin with no number is known by its name, then its 1-based position (reported).

Readers must give every (reference, unit) once; two placements claiming the same pair are renamed `<ref>_2` with a note.

## 3. The map — `SchematicImportMap.cs`

Rules are data, tried in order; every condition a rule states must hold (symbol name pattern, whole name,
case-insensitive; reference prefix letters; pin count; optional value pattern). A multi-unit symbol never matches.

| Rule | Symbol name | Prefix | Pins | Becomes |
|---|---|---|---|---|
| resistor | `R`, `RES`, `RESISTOR`, or one of those then `_`/space and more | `R` | 2 | `Resistor`, `R` |
| capacitor | `C`, `CAP`, `CAPACITOR`, `CP`, likewise | `C` | 2 | `Capacitor`, `C` |
| inductor | `L`, `IND`, `INDUCTOR`, likewise | `L` | 2 | `Inductor`, `L` |
| ground | `GND`, `GROUND`, `EARTH`, `0`, likewise | — | 1 | ground (its net is node 0) |

Anything unmatched becomes a **symbol cell**. The value is read by the bill-of-materials reader (`10k`, `4k7`, `100nF`,
`4u7`, `2R2`, `1.5 nH`) and written as a number and unit (`10 kOhm`). A value that does not read as the part's dimension
makes the native part's value a global variable `<Ref>_<Param>`, with a default (a not-fitted marker defaults to an open:
1 GOhm, 0.01 pF, 1 H; anything else to 1 kOhm, 1 nF, 1 nH) and the source text in a note. A power or global label named
`GND`, `GROUND`, `EARTH` or `0` is ground; `AGND`, `DGND` and the like stay named nets. A user may push a native row to a
symbol cell (`SchematicImportOptions.SymbolCells`), never the reverse.

## 4. The build — `SchematicImportBuild.cs`

**Nets, in two layers.** *Physical*: a wire's vertices; anything (wire end, junction, pin, label, sheet pin) lying on a
wire, interior included; anything at the same point. Two wires merely crossing are not joined. *Logical*: physical groups
joined by name — local and hierarchical labels per sheet, global/power/off-page labels across the scope, a hidden power
pin's `ImplicitNet`, and ground. A net is named by its best label (power/global, then hierarchical, off-page, local),
made unique; an unnamed net is a quiet `N`k, drawn without a label when one group of its wires reaches every pin.

**Placement.** A symbol cell's symbol is translated so its first pin is the local origin, and the instance is placed with
that pin on the source's point. Its pins are NOT snapped to the 100-mil pin grid (`BuildSymbol(…, snapPins: false)`), so
a part pitching pins at 50 mils keeps them apart and every pin lands exactly. A native part
takes the rotation and on-grid origin nearest its source pins; a pin it misses is re-joined by one dog-leg, the source
wire never moved. A pin the source joins by touching a label or pin (no wire), and a hidden power pin, get a net label on
the pin. Each wired physical ground group keeps its wires and gets a ground symbol where the source grounded it; a
ground pin with no wire gets one on the pin.

**The grid.** The drawing is never snapped (`ExactPlacement.SnapToGrid = false`); its `GridSize` is the largest divisor of
100 that every source point sits on, because extraction quantizes connection points to it. Measured in
`src/Design/RESOLVED.md` (R-si1-6).

**Pages and hierarchy (D5).** A flat design's pages are tiled left to right with a 20-square gap; off-page labels join by
name. A hierarchical design gets one cell per distinct child sheet in the parts folder: a rectangle symbol (the first
placement's rectangle, ports in its pin order) and a schematic whose hierarchical labels are `Pin`s. A global net used on
several sheets is not joined across cells (circuitRF keeps a cell's nets local) and is reported; ground is global.

**Ports (D9).** `SchematicImportOptions.Ports` names references (a connector drawn as a Port where it was: first pin off
ground the signal, the next its reference) or nets (a Port to ground beside the drawing). With any, the default
S-parameter analysis `SP1`.

Free text and graphic lines become schematic text and lines; junctions become dots. Title blocks and borders are not
imported.

## 5. The check — `ImportConnectivity.cs`

Each produced schematic is extracted by `NetExtractor` (a black-box resolver answers every cell with one `Pin` per symbol
pin, so a model-less symbol cell still binds its nets), instance pins are translated back to source pins through the
build's own table, and the result is compared as **sets of pin sets** with the source's geometric nets — and, for a flat
design whose format declares nets, with those. Single-pin nets, no-connect pins and ground-symbol pins are left out of
both sides. Each difference names its pins: "joined in the source, split in the import" or the converse. The import still
completes; the report carries the verdict.

## 6. The writes — `SchematicImport.cs`

`Import(source, parentDir, cellName, options)` refuses a name `NameValidator` rejects or one already there; decides every
cell (D4: `<source-stem>_parts/` beside the target, one cell per distinct symbol unit, reused when its `.csym` is
byte-identical, refused naming both when a same-named cell differs) before writing anything; writes the symbol and sheet
cells, then the target cell through `CellCreate` with a `SchematicImportSource` block (format, file NAME, UTC date). Then
the check, then the report.

`BuildInto(source, into, topLeft, options)` builds the same, writes its parts beside `into`'s cell (so an unsaved
schematic is refused), moves the drawing so its top-left lands on `topLeft` by whole grid squares, drops analyses, and
renames every instance, net label and variable `into` already has (`SchematicMerge`, shared with the image paste). The
edit carries the `GridSize` the drawing needs; the editor adds it as one undo step (SI-5).

## 7. The classifier — `SchematicFileClassifier.cs`

CFBF signature → `BinaryDsn` (SI-4 confirms by its root streams); text starting `(kicad_sch` → `KicadSch`; text starting
`(edif` → `Edif200` when its `edifVersion` is `2 0 0`, else refused naming the version. Anything else, an ASCII `.dsn` kit
symbol and a routing `.dsn` included, is `Unknown` with "not a schematic design".

## 8. The `.kicad_sch` reader — `SchematicSexprReader.cs` (SI-2)

`SchematicSexprReader.Read(path) → ForeignSchematicRead` (a `ForeignSchematic`, or a refusal). Parsed with `PcbSexpr`;
each `lib_symbols` entry goes through `ComponentSymbolSexprReader.ReadSymbol`/`ReadUnit` (the symbol library's own
reader, now taking a parsed node) for its drawing, while the reader takes the pins itself because the neutral model needs
their type, `hide`, length and side. Nothing outside the file is looked up. `DeclaredNets` is null: the format declares
no nets, so the geometric check is the check.

- **Units and axes.** Millimetres → mils by `mm / 0.0254` (to 1e-4 mil). The sheet is Y down and is not flipped; the
  embedded symbols are Y up and stay so.
- **A placed symbol's transform.** The format turns counter-clockwise on screen by its angle, THEN mirrors on screen
  (`mirror x` top-to-bottom, `mirror y` left-right). In `ForeignInstance`'s terms: no mirror → rotation `−angle`;
  `mirror y` → mirrored, rotation `angle`; `mirror x` → mirrored, rotation `angle + 180`. Held by
  `opamp_units.kicad_sch`.
- **References.** The symbol's own `instances` block on the sheet path that reached it, else the root file's
  `symbol_instances` keyed by path (the older epoch), else its Reference property. A child file placed by several sheets
  is read ONCE (D5), by the first path; it is one `ForeignSheet` named after its file stem.
- **Power symbols** (`(power)` in `lib_symbols`) become `ForeignLabel(Power)` named by their Value (a local power symbol a
  local label) — ground-named ones are ground through the map. One whose pin is not a power input (a rule-check flag)
  is left out and counted. A hidden power-input pin on any other part carries `ImplicitNet` = its pin name, in every
  version of the format to date; the report says how many and under which version.
- **Buses.** Bus segments, bus entries and labels on a bus are drawing: each member connects by its own label on its own
  wire, exactly as the source's editor requires. A bus-named sheet pin or hierarchical label (`D[0..7]`, `{A B}`) is not
  expanded; the report lists those buses, because their members are not joined across the sheet boundary (D11).
  `~{X}`, `_{x}`, `^{x}` are text markup, not bus groups.
- **Not carried, one counted sentence per kind (R-si2-3):** images, tables, rule areas, net-class directives, circles,
  arcs and curves drawn on the sheet, pinless parts (mounting holes, logos), alternate body styles (imported with the
  standard body). The file's version is reported and branched on nowhere.

Fixtures: `testdata/schematic-import/sexpr/` (authored, tiny). The gate is
`tests/Ui.Tests/SchematicImport/SchematicSexprReaderTests.cs`.
