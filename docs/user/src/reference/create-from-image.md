---
title: Create from Image
slug: reference/create-from-image.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Create from Image
lede: Drop a picture of a schematic or a layout and get its circuit as a schematic, or a layout's copper as a layout — what was read drawn over the picture, and every guess one click from being corrected.
keywords: image, picture, screenshot, plot, digitise, digitize, Touchstone from plot, Smith chart, datasheet plot, minimum phase, PNG, JPEG, paste, trace, bitmap, scale, two points, line impedance, layers, colours, preset, underlay, schematic from image, layout from image, recognise, recognize, schematic picture, datasheet, crossing, junction dot, as drawn, tidy
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it does</a></li>
<li><a href="#ways-in">Ways in</a></li>
<li><a href="#dialog">The dialog</a></li>
<li><a href="#kind">What kind of picture it is</a></li>
<li><a href="#make">What to make of it</a></li>
<li><a href="#canvas">The picture, and what was read</a></li>
<li><a href="#schematic">Schematic pictures</a></li>
<li><a href="#scale">Scale</a></li>
<li><a href="#plots">Plots</a></li>
<li><a href="#layers">Layers and presets</a></li>
<li><a href="#technology">Technology</a></li>
<li><a href="#advanced">Advanced</a></li>
<li><a href="#underlay">The picture under the result</a></li>
<li><a href="#reads">What reads well, and what does not</a></li>
<li><a href="#good-picture">Getting a good picture</a></li>
<li><a href="#failure">When nothing can be read</a></li>
<li><a href="#troubleshooting">Troubleshooting by report line</a></li>
<li><a href="#limits">What it does not read</a></li>
</ol>
</nav>

## What it does {#what}

A picture of a circuit &mdash; a datasheet's application circuit, another tool's screenshot, a board viewer's
screenshot, a die plot, a figure in an application note &mdash; holds the circuit, but not in a form a simulator can
use. **Create from Image** reads it:

- **Create Schematic from Image**, on a picture of a **schematic**, reads its wires, symbols, designators and values
  and draws them as a schematic of native components, laid out as the picture lays them out &mdash; see
  [Schematic pictures](#schematic).
- **Create Layout from Image** traces a **layout** picture's copper into a layout: filled shapes on your technology's
  layers, drill holes as vias, sitting exactly on the picture.
- **Create Schematic from Image**, on a picture of a **layout**, traces it the same way and then reads the circuit out of
  the traced copper with [Create Schematic from Artwork](artwork-to-schematic.html) &mdash; the same recognition, the
  same parts table, the same report. The new cell holds both: the traced layout and the schematic recognised from it.
- **Create Touchstone from Plot**, on pictures of S-parameter **plots** &mdash; dB and phase against frequency, a Smith
  chart &mdash; digitises their curves into a Touchstone file &mdash; see [Plots](#plots).

Like reading artwork, it is a best attempt that you then check. Everything it guessed is shown on the picture and
said in the report.

## Ways in {#ways-in}

Wherever there is a picture, the command is one gesture away &mdash; and it is only offered where it can work.

| Where | Rows | What opens |
|---|---|---|
| **Design ▸ Create** | **Schematic from Image…**, **Layout from Image…** | The dialog, empty, ready for a drop, a paste or **Browse…** &mdash; every option, and the parts table to review. Enabled whenever a workspace is open; no document needs to be in front. |
| **Edit** menu | **Paste Image as Schematic**, **Paste Image as Layout** | No dialog: the clipboard's picture is read with every default and pasted into the schematic or layout in front, with the picture itself under it as an ordinary bitmap. Everything pasted is selected and touches nothing already there, so it can be dragged into place &mdash; and in a layout, scaled as one with a corner handle or **Scale…**. Each is enabled only while a document of its own kind is in front and the clipboard holds a picture &mdash; a copied image, or one picture file copied in a file manager (two files, or a file that is not a picture, leave them greyed). The clipboard is looked at when the Edit menu opens and when you come back to the window. |
| Right-click a picture placed in a **layout** | **Trace Image into This Layout…**, **Create Schematic from Image…** | Tracing into this layout puts the copper in this layout, over the picture, at the size the picture is placed (scale **As placed**), in this layout's technology &mdash; one undo step. **Create Schematic from Image…** makes a new cell and offers **As placed** as its scale. |
| Right-click a picture placed in a **schematic** | **Create Schematic from Image…**, **Create Layout from Image…** | A picture of a schematic is drawn into this schematic, over the picture, at the size the picture is placed &mdash; one undo step, and what was added is selected. A picture of a layout makes a new cell; a schematic has no physical size, so **As placed** is not offered, and the scale is set in the dialog. |
| Right-click a picture file in the **Project** panel | **Create Schematic from Image…**, **Create Layout from Image…** | The dialog, already reading that file. Offered on a Known File and on a file inside the workspace folder whose extension is one circuitRF reads. |

A placed picture whose file cannot be found offers none of these rows &mdash; point it at its file first (on a layout,
**Resolve Path…** on the same menu). A locked picture is offered like any other: locking stops it moving, not being
read.

Dropping a picture onto a canvas still places it as a bitmap, and dropping one onto the Project panel still adds it
as a Known File; the right-click rows above are the next step. Dropping one onto the dialog reads it.

## The dialog {#dialog}

The dialog opens empty, as one drop zone. Drop a picture on it, paste one (**Cmd/Ctrl+V**), or **Browse…**. It reads
PNG, JPEG, BMP, GIF (the first frame) and WebP; a file that is not a picture is refused in the status line, and
nothing else changes.

{{ui: image-dialog-empty}}

With a picture, the top of the dialog is the picture and what to do with it; below it is the body the
[artwork dialog](artwork-to-schematic.html) has &mdash; target, options, parts table, report and **Create** &mdash;
with the picture itself taking the left two-thirds. **Replace…** and **Paste** read another picture in its place.

{{ui: image-dialog-layout-picture}}

## What kind of picture it is {#kind}

**It is** says what the picture was read as. **Auto** decides from the picture &mdash; how much of it is ink, whether
the ink is filled or stroked, how many colours it has, whether its lines run straight &mdash; and its label says what
it decided, with a dot for how sure it is: green sure, amber fairly sure, red a guess. Hover over it to see what it
measured. **Schematic**, **Layout** and **Plot** override it; a picture Auto reads wrongly is read your way after one
click.

A sparse board of one trace width can look to Auto like a schematic drawing. If it does, choose **Layout**.

A photograph of a board reads as **Auto (board photo)**, with a less certain dot than a drawing gets: it is read as a
layout, and is best set square on its corners first &mdash; see [Photographs of boards](#photos).

## What to make of it {#make}

**Make** is **Schematic** or **Layout**. A schematic picture makes a schematic only: draw the schematic first, then use
**Update Layout from Schematic**. **Layout** is disabled for it, and its tooltip says so.

The **Target** is a new cell, named after the picture (a pasted picture is `pasted_image_1`, `_2`, …). A name that is a
cell this command made relabels **Create** as **Replace**, and a history checkpoint is taken before anything is
replaced. A cell this command did not make is never replaced.

Making a schematic also shows the recognition's options &mdash; ground, ground vias, coplanar lines, the frequency
sweep and the digits values are written with. They are the artwork dialog's, and mean exactly what they mean there. A
schematic picture has no copper, so in place of ground, vias and coplanar lines it shows **Arrange** and **Crossings
connect** ([Schematic pictures](#schematic)).

## The picture, and what was read {#canvas}

What was read is drawn over the picture, each kind toggled from the legend at the top left:

| Legend | What it shows |
|---|---|
| **Copper** | Each traced region, outlined in its layer's colour. |
| **Drills** | Each drill hole, as the circle read. |
| **Wires** | A schematic picture's wires, junction dots and crossings &mdash; a crossing read as connected is filled. |
| **Parts** | Each recognised part, boxed with its designator &mdash; on a schematic picture, with its kind and which way it faces. |
| **Lines** | Each line, as its centre line, labelled with its component and width &mdash; `MLIN W=450 µm`. |
| **Ports** | Each port, with its number. |
| **Text** | A schematic picture's words, boxed with what was read. |
| **Ignored** | Colours read as *Ignore*, hatched grey. |
| **Unknown** | Whatever was not read &mdash; a part of unknown kind, a value not found, a word not read, a wire end reaching nothing, a colour with no layer &mdash; in amber. |

Click a part on the picture to select its row in the parts table; select a row, or an anchor in the report, to
highlight its part on the picture.

| Gesture | Does |
|---|---|
| Wheel, or pinch | Zoom about the pointer. |
| Right- or middle-drag, or Space with a drag | Pan. |
| Drag | On a layout picture, read only the rectangle dragged; **Whole picture** reads all of it again. |
| Click a crossing | On a schematic picture, connect or disconnect it. |
| Drag a wire end | On a schematic picture, drop a wire end that reaches nothing onto a pin to connect it. |
| Double-click, **F** or the Zoom to Fit button | Fit the picture. It opens fitted and stays fitted as the dialog is resized, until it is zoomed or panned. |
| Escape | Put down a measuring tool. |

## Schematic pictures {#schematic}

A picture of a schematic is read as its wires, its symbols and its words, and drawn as a schematic of native
components.

**What is recognised.** Resistors, inductors and capacitors in both the US and the IEC drawings, grounds, ports and
terminals, transmission lines and stubs, and diodes. A transistor, an amplifier triangle or a box with three or more
pins is an **S-parameter block** (SnP) with one port per wired pin and a grounded pin as its reference &mdash; an S2P
drawn as a box with pins 1, 2 and Ref reads as an S2P &mdash; ready for its Touchstone file. A two-pin symbol nothing
matches is shown as **?** in the parts table and drawn as a capacitor until you say what it is.

{{ui: image-dialog-schematic-picture}}

**How wires are read.** A T-junction always connects. Where two wires cross, **Crossings connect** decides: *With a
dot* (the default) connects a crossing only where a junction dot is drawn; *Never* and *Always* do what they say. A
hop never connects. Wires with the same net label, or the same supply name, are one net. A wire end that reaches
nothing is shown in amber.

**Correcting a connection.** Click a crossing on the picture to connect or disconnect that one; drag a wire end that
reaches nothing onto a part's pin to connect it. Each correction is kept through every later reading, and **Clear
corrections** forgets them all. Nothing else is edited on the picture &mdash; the schematic editor is where a drawing
is fixed.

**Values.** Designators, values, net names and port names are read; prose is not. A value may be written alone
(`10pF`, `4R7`, `2n2`) or with its parameter's name and spaces (`L = 5 nH`, `C = 0.5 pF`); a value printed on the line
under a part's designator is that part's, wherever the label stands. A port's `Z = 75 Ohm` under its designator sets
its impedance. A value that cannot be read becomes
a global variable named after its part &mdash; `C1_C`, `L2_L` &mdash; with a tune entry, starting at a value that leaves
the circuit as if the part were not there, so the schematic simulates at once and the Tuning panel lists what is
missing. A transmission line takes its Z and E (or its length) from the text beside it, and each one not read is a
variable in the same way. The parts table lists the parts with their position on the picture in pixels; edit a kind or
a value there exactly as for [artwork](artwork-to-schematic.html).

**Ports.** Port and terminal symbols are ports, and so are net names that name a port (`RFIN`, `OUT`, `P1`); nothing
else is &mdash; a wire running off the edge of the picture stays a wire. A schematic picture with no port at all is still made:
it is written without its S-parameter analysis, and the report says *No port found: add Port components to simulate*.
A supply net, such as `VCC`, has no source in the circuit and is left as a named net.

**Arrange.** **As drawn** puts each part where the picture has it, turned as it is drawn, and draws the picture's own
wires &mdash; every corner, junction and loose end &mdash; snapped to the schematic grid, with a ground symbol where the
picture draws one, a net label only where the picture writes a net's name, and the picture under it. A screenshot of a
schematic editor is drawn at the scale of its own grid, so its wires land on the schematic's grid lines and the
picture lines up under the drawing. A part that would land on another
is placed beside the drawing instead, and the report names it. **Tidy** draws the same circuit in circuitRF's own
arrangement, signal path left to right, without the picture under it &mdash; it would not line up.

**Making a layout of it.** A schematic picture makes a schematic only. To get a layout, create the schematic, then use
**Update Layout from Schematic**.

## Scale {#scale}

A picture has no millimetres in it, and a wrong scale is wrong by a factor while looking entirely right &mdash; so the
scale is never a silent guess. The **Scale** row shows the length of one pixel and what it was taken from, and the
combo beside it lists every piece of evidence found:

| Evidence | Chosen by itself? |
|---|---|
| **From parts** &mdash; chip land patterns that agree on one scale, at least three of them | Yes |
| **Two points…** &mdash; click two points on the picture, then type the distance between them | You choose it |
| **Line impedance…** &mdash; click a trace and type its Z0; the technology gives its width | You choose it |
| **Board corners…** &mdash; drag four handles onto a photographed board's corners and type its size | You choose it |
| **File resolution** &mdash; the dots per inch the file states | Never: a screenshot's resolution describes a screen |

The distance needs its unit &mdash; `1.6 mm`, `62 mil`, `500 um`. A bare number is refused where you typed it: it could
be any of them. Beside the value, the resolution line says how finely the picture can be read: *1 px = 20 µm; a
451 µm trace is read to ± 2 %*.

With no usable evidence, the row is highlighted, the picture asks *Set the scale: Two points…*, and **Create** waits.

{{ui: image-dialog-needs-scale}}

## Photographs of boards {#photos}

A phone photo of a board is taken at an angle, in uneven light, with most of the copper under the solder mask. Choose
**Board corners…** in the Scale combo: four handles appear on the photo as taken. Drag them onto the board's corners,
clockwise from its top-left &mdash; or onto any rectangle whose size you know, such as a part or a connector's outline
&mdash; type its size beside the last handle (`50 mm × 30 mm`, each side with its unit), and press **Set**. The first
handle to the second is the width.

{{ui: image-dialog-board-corners}}

The photo is then read square: the picture shows the rectified board, the rectangle's size is the scale, and the light
is evened out across it. Its pixels are read as six classes, each a row under **Layers** in a fixed colour, so a preset
saved from one board photo applies to the next:

| Class | Read as, by default |
|---|---|
| Off the board | Background |
| Mask | Background |
| Copper under the mask &mdash; the mask's own colour, lighter | Top copper |
| Exposed copper &mdash; gold, copper or tin | Top copper |
| Silkscreen | Silkscreen |
| A part's body | Ignore |

Glare &mdash; a white glint &mdash; is filled from the surface around it, so a line running under it stays one line.
A part's body hides its pads: each one is counted in the report and not read. The report also gives how far each
corner of the rectangle lies from the corner the photo shows there, in µm; a large one means a handle is off its
corner. **Read as taken**, beside the size, puts the photo back as it was.

{{ui: image-dialog-board-photo}}

Widths read from a photo are good enough to start a schematic from, not to trust for impedance: a line's Z0 from its
photographed width is an estimate.

## Plots {#plots}

A datasheet often shows a part's S-parameters as plots and ships no file. A picture of a plot &mdash; dB or phase
against frequency, a Smith chart, a polar chart &mdash; is read as **Plot**, and **Make** is **Touchstone** or **CSV**.

| Where | Row |
|---|---|
| **File** menu | **Import ▸ Image ▸ As Touchstone…** &mdash; a picture browsed to. The dialog's **Paste** reads the clipboard's. |
| **Tools** menu | **Digitise Plot…** &mdash; the dialog, empty, in plot mode. |
| Right-click a picture file in the **Project** panel | **Create Touchstone from Plot…** |

**Several pictures make one file.** A datasheet's S21 and S11 are often separate figures: **Add picture…** under
**Pictures**, or drop or paste another, adds it to the same session; **✕** takes one out. Plots and traces are
numbered across all of them &mdash; plot 1 is the first picture's first plot, and traces are T1, T2 and so on.

**On the picture**, each plot is boxed and numbered, its tick labels green where the axis used them and amber where they
were not read or were outvoted, and each trace is drawn over its curve in its own colour and labelled with what it was read as
(`S21 dB`). Markers are ringed, a gap bridged over a label or another curve is dashed, and a crossing of two curves of
one colour that could not be told apart is amber. Under **Plots**, clear a plot's box to leave it out: it is drawn and
not read.

**An axis whose labels did not read** waits, and its plot's **x axis** or **y axis** row takes two positions along
it, in pixels, with the values there: `120px=1GHz,900px=5GHz`. A logarithmic axis is read as one from its labels.

**Traces.** Each row is one curve: its colour, its range, its points and its resolution per pixel. **Entry** and
**quantity** say what it is &mdash; S21 in dB, S11 as phase, a chart's complex value, VSWR, return loss and so on
&mdash; taken from a legend entry in its colour, a label along it, the y axis's title or the plot's title, and the dot
says how sure that is. A curve nothing names is **Unassigned**, and the Touchstone waits for you to say which entry it is.

**Frequency along a Smith or polar trace.** A chart has no frequency axis. It comes from markers on the trace whose
readouts show their frequency, or from a rectangular plot of the same entry in the session, matched point by point; with
neither, the trace's **Start** and **Stop** are highlighted and wait for you. Between known frequencies they are spread
along the curve by the angle it sweeps round the chart's centre &mdash; exact for a plain line, an approximation for
anything that resonates &mdash; so a chart with three or four markers reads far better than one with none. Frequency
runs clockwise round the chart unless the markers say otherwise. **Smith Z0** is the chart's own `Z0 =` when one is
printed, else 50 Ω, said in the report.

**Matrix.** A Touchstone needs every entry, and a datasheet rarely plots them all. The grid shows each entry of the
file and where it comes from &mdash; *plot 2*, *= S21*, *zero*, *min phase* &mdash; and each is a menu:

| Not plotted | Filled with, unless you choose otherwise |
|---|---|
| S12 | S21, when S21 stays at or below 0 dB throughout (a passive part is reciprocal); zero when it rises above (an amplifier's isolation) |
| S22 | S11 |
| A three- or four-port's entries | zero |
| The phase of an entry plotted only as a magnitude | **Minimum phase**, *estimated*; or zero, or a linear phase from a delay you type |

**Minimum phase** works the phase out from the magnitude curve. It is a fair estimate for a filter or a matching network
and a poor one for anything with a delay in it, and it is least sure near the ends of the plotted band: the report
gives the middle 80 % of the band as the part to trust. **If the datasheet also plots the phase, add that picture
too** &mdash; a phase read from a picture is always better than one estimated.

**Output.** The grid is the frequency range every trace covers, at the finest trace's resolution, rounded to a tidy
step; type a start, stop and number of points to change it, and **log** for logarithmic spacing. The Touchstone is
written in dB and angle unless you choose magnitude and angle or real and imaginary. **Create…** asks where to save it;
its header records each picture's name, how every trace was read and every fill and phase rule. With **Keep the source
pictures beside the file**, each picture is copied beside it as `<name>.source.png`. The new file opens in the Data
Display, and its passivity, reciprocity and causality check goes to Messages.

**CSV** writes the curves exactly as read, in each plot's own units, and never waits: any plot digitises, S-parameters
or not &mdash; a temperature against time comes out just as well.

| Reads well | Reads less well |
|---|---|
| A rectangular plot with labelled ticks and distinctly coloured curves | Two curves of one colour that run together for a stretch |
| A Smith chart whose markers show their frequencies | A Smith chart with no markers and no rectangular plot of the same entry |
| A legend, or each curve labelled | A JPEG, read a little less exactly |

## Layers and presets {#layers}

A picture's colours are its only layer information. **Layers** lists each colour with its share of the picture and
what it is read as: one of your technology's layers, **Drill**, **Board outline**, **Silkscreen**, **Background** or
**Ignore**. The first reading maps the colour touching most of the picture's border to the background, round holes to
**Drill**, and the rest to your copper layers by area, top first; a colour that is two translucent layers drawn over
each other is read as both. Hover over a row to see its colour on the picture.

Change a row and it is marked with a dot: every later reading keeps it.

Pictures from one source use the same colours every time, so a map can be kept. **Save Preset…** keeps the map and the
options under a name; **Preset** applies one. When a picture's colours match a saved preset, the status line offers
it &mdash; *Colours match preset "viewer dark"* &mdash; and applies it only when you click **Apply**. Presets belong to
you, not to a workspace.

## Technology {#technology}

A picture says nothing about the stackup. The traced layout and the recognised schematic use the technology chosen
here &mdash; the workspace's default to begin with &mdash; and the report says once that the stackup was not read from
the picture. Changing it reads the picture again.

## Advanced {#advanced}

Collapsed until you open it, and it stays as you left it. Each change reads the picture again.

| Option | Does |
|---|---|
| **Minimum feature** | Regions and holes smaller than this, in square pixels, are specks and are removed. |
| **Simplify** | How far, in pixels, a traced outline may move to lose a vertex. |
| **Snap edges**, **and 45°** | Edges near 0° and 90° (and 45°) are made exact. |
| **Snap within** | How near, in degrees, an edge must be to be snapped. |
| **Colours, at most** | The most colours the picture is separated into. |
| **Merge within ΔE** | Colours closer than this are one colour. |
| **Edge stroke** | A picture that outlines each shape with a line drawn along its edge &mdash; a layout editor's screenshot, circuitRF's own layout among them, which draws that line 2 px wide &mdash; makes every shape that much fat. Type the line's width in pixels and it is taken back off. 0 for a picture of filled shapes alone. |
| **Threshold**, **Window** | A schematic picture: how dark, against its neighbourhood of this many pixels, a pixel must be to be ink. |
| **Keep the source picture under the result** | See [below](#underlay). |

## The picture under the result {#underlay}

The written layout and schematic carry the source picture behind them, faded to 35 %, placed so each
shape lies on what it was read from. A schematic picture drawn **Tidy** has none, since its parts are not where the
picture has them; one drawn into the schematic it is placed in needs none, since the picture is already there. Checking the result is then comparing in place. The picture is copied into the
cell's folder; it is an ordinary bitmap, which you can move, resize, fade or delete like any other &mdash; select it and
**Properties** shows its file, position, size and opacity &mdash; and it is never exported,
checked or meshed. Clear **Keep the source picture under the result** to leave it out.

## What reads well, and what does not {#reads}

| Picture | Expect |
|---|---|
| A layout drawn by a program &mdash; a board viewer, a layout editor, a die plot, a figure | **Good.** Flat colours and sharp edges trace to within half a pixel, and the circuit is then read from the copper exactly as from artwork. |
| A schematic drawn by a program &mdash; a datasheet circuit, another tool's screenshot, a circuitRF picture | **Good wiring and symbols, most values.** Values read when the type is clean and regular; one that does not read becomes a variable to tune. |
| A picture saved as a JPEG, turned slightly, or speckled | **Good**, a little less exactly: compression smudges colour edges by about a pixel. |
| A photograph of a real board, set square on its corners | **Partial**: lines and pads come back, fine detail does not, and pads under parts are not read. |
| A hand-drawn schematic | **Not a goal.** It may read if it is neat. |

## Getting a good picture {#good-picture}

- **Crop to the circuit.** A title block, a page of notes or a second circuit beside the first is read too.
- **PNG rather than JPEG**, and the original rather than a copy of a copy. A JPEG's smudged edges are read, less
  exactly.
- **Big enough.** Lines 2 pixels wide or more, and text at least 14 pixels tall to its capitals. Zoom in before taking a
  screenshot rather than scaling the picture up afterwards: scaling adds no detail. A circuitRF schematic reads well
  exported at 3× and above.
- **One colour per layer** on a layout, distinct from the background and from each other; drill holes drawn as filled
  circles.
- **Something of known size** on a layout picture &mdash; a scale bar, a dimension, a part whose size you know, a line
  of known impedance &mdash; so the scale can be set. A picture of ordinary chip parts often sets its own.
- **If you have the original file** &mdash; the PDF, the SVG, the board's Gerber &mdash; it is the better source. A
  board's files go to [Create Schematic from Artwork](artwork-to-schematic.html); a vector drawing, exported as a PNG at
  a high resolution, reads better than a screenshot of it.

## When nothing can be read {#failure}

A picture with no drawing in it is shown dimmed, with one sentence saying what was seen &mdash; *No drawing found: a
photograph-like picture with no straight line work*, *a blank picture*, *a page of text* &mdash; and two links, **Read
it as a schematic** and **Read it as a layout**, for a picture you know better than that. A picture of line work that
holds no circuit &mdash; a block diagram, a drawing &mdash; is read as a schematic and refused the same way: *No circuit: most of
the lines in this picture curve or slant*, with the one link that reads it as a layout. A reading that finds nothing to make &mdash; no copper at the mapped
colours, no technology &mdash; is shown the same way, with the control that answers it highlighted. **Create** stays
disabled, and its tooltip is that sentence.

{{ui: image-dialog-no-drawing}}

## Troubleshooting by report line {#troubleshooting}

| The report says | What to do |
|---|---|
| *Set the scale: Two points…* | Click two points a known distance apart and type the distance with its unit, or choose **Line impedance…** and click a trace. |
| *1 px = 40 µm; a 450 µm trace is read to ± 9 %* | The picture is coarse for its narrowest trace. Take it again zoomed in, or accept the uncertainty. |
| *The picture draws no solder mask or paste, so land patterns were read from the copper itself* | Parts were found from pad-shaped copper. If the picture does show the mask, map that colour to your mask layer: parts are then found from its openings. |
| *N designators on the silkscreen name no part* | The text was read but no land pattern was found under it. Check the overlay; add the part in the schematic afterwards. |
| *N two-pin symbols matching no drawing, read as ?* | Set each one's kind in the parts table. Until you do it is drawn as a capacitor. |
| *N values were not read and are global variables with a tune entry* | Type the values into the parts table, or keep them as variables and tune them. |
| *No designator was read for N parts* | Their designators were generated; rename them in the parts table. |
| *N words are in type too heavy for its size to read reliably* | Bold or small type. Take the picture larger; the parts are kept, their values become variables. |
| *N dangling wire ends* | A wire stops short of a pin. Drag the amber end onto its pin on the picture. |
| *N words named no part and no wire* | Text the reader could not place, often a note or a title. Nothing to do unless it was a value. |
| *No port found: add Port components to simulate* | The picture shows no port symbol or port name. The schematic is made without its analysis; add ports and an S-parameter analysis. |
| *No connected line work: this picture has no wires to read* | The picture holds words only, or its lines are too faint: crop to the circuit, or lower **Threshold** under Advanced. |
| *No symbols: this picture has wires but nothing drawn between them to read as a part* | The picture may be a layout drawn in lines: read it as a layout. |

## What it does not read {#limits}

- **Hand-drawn schematics** are not a goal; a neat one may read.
- **Photographs** of printed pages, of a board's underside aligned to its top, and X-ray pictures. A photograph of a
  board's top reads its lines and pads only once set square on its corners.
- **Serif and script type**, and type that is bold, very small or slanted at a small size. A value printed in such type
  becomes a variable or, rarely, reads wrong: check values against the picture.
- **A value printed far from its part**, other than on the line under its designator, may be given to the part nearest
  it.
- **Dense layouts** where a trace is under about four pixels wide: widths are read, but loosely.
- **The stackup.** A picture never states one; the technology you choose supplies it.
- **Vector files** &mdash; PDF, SVG, EMF. Export the page as a PNG.
- **Vias drawn as two thin rings**, as circuitRF's layout view draws them, below about 10 µm to a pixel; draw drill
  holes as filled circles.
- **A part on a short stub to a via, with no solder mask in the picture**, may not be found; a part in a line is.
