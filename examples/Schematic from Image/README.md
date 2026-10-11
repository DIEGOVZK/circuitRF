# Schematic from Image

Two pictures of one small network — a capacitor and an inductor in series on a line — and the cells circuitRF made
from them with **Create Schematic from Image**.

| | what it is |
|---|---|
| `pictures/matching-network.png` | the schematic as a datasheet prints it. The inductor's value is left off, on purpose. |
| `pictures/matching-network-board.png` | its board as a Gerber viewer shows it: the copper and the silkscreen's designators, 20 µm to a pixel. |
| **Matching network** | the schematic made from the first picture, drawn as the picture draws it, over it. |
| **Matching board** | the layout traced from the second picture, and the schematic recognised from it, each over the picture. |

---

## 1. Open what was made

**Tools ▸ Examples ▸ Schematic from Image**, choose a folder for your copy, and open **Matching network ▸ schematic**.

The picture lies under the drawing, locked and faded, so every part sits on the symbol it was read from. Select it to
hide or fade it, as any bitmap. C1 is 1.5 pF, read from the picture. L1 has no value printed, so it arrived as the
global variable **`L1_L`** with a tune entry, starting at 100 pH — small enough that the first simulation shows the
circuit as if L1 were a plain wire.

Open **Matching board ▸ layout**: the copper traced from the picture, on the technology's top layer, with the picture
under it. Its schematic has the two lines, both parts — named by the silkscreen, their land patterns read as 0603 —
and a port where each line leaves the picture. A board prints no values, so both are variables: **`C1_C`** and
**`L1_L`**.

## 2. Tune

Open the **Tuning** panel on either schematic and press **Simulate**. Drag `L1_L` (and on the board, `C1_C`) and watch
S21 move. The variables are ordinary globals: type the value once you know it.

## 3. Make them again yourself

**Design ▸ Create ▸ Schematic from Image…**, then drop `pictures/matching-network.png` on the dialog — or browse to it.
It is read at once; **Create** makes a new cell. Drop it onto an open schematic instead, as a bitmap, and right-click
it: the command then draws the circuit over the picture where it lies.

The board picture needs two answers the picture cannot give:

- **Scale.** A picture has no millimetres in it. Type `20um` per pixel in the scale row, or pick two points a known
  distance apart.
- **The blue.** The automatic map gives the copper layers by area, so the blue designators land on a copper layer.
  Set that colour's row to **Silk Top**: the designators are then read, and the parts get their kinds.

The same from a terminal:

```
circuitrf recognize pictures/matching-network.png --into new:Again
circuitrf recognize pictures/matching-network-board.png --scale 20um/px --layer-map "#ffffff=background,#c87533=Top Copper,#203090=silk" --into new:Again_board
```
