"""Draw the self-test's three tiny labelled pictures (brief-iml-1 §3).

    python -m crfml.fixtures          rewrites tests/fixtures/*.png and *.mllabel.json

They are drawn here, by our own code, so the repository holds no picture anyone else made (L1, L2). Each label is
built from the same coordinates the drawing used, so it is exact by construction. Rewrite them only when the label
format changes; the committed files are what crfml.selftest checks.
"""

from __future__ import annotations

import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

FIXTURES = Path(__file__).resolve().parent.parent / "tests" / "fixtures"
INK = (20, 20, 20)
PEN = 2


class Sheet:
    def __init__(self, name: str, w: int, h: int):
        self.name, self.w, self.h = name, w, h
        self.img = Image.new("RGB", (w, h), (255, 255, 255))
        self.d = ImageDraw.Draw(self.img)
        self.font = ImageFont.load_default()
        self.parts, self.wires, self.texts, self.junctions, self.crossings = [], [], [], [], []

    # drawing ──────────────────────────────────────────────────────────────────────────────────────────────────────

    def wire(self, *pts):
        self.d.line(list(pts), fill=INK, width=PEN, joint="curve")
        self.wires.append({"points": [list(p) for p in pts], "pen": PEN})

    def text(self, tid, xy, s, role):
        l, t, r, b = self.d.textbbox(xy, s, font=self.font)
        self.d.text(xy, s, fill=INK, font=self.font)
        self.texts.append({"id": tid, "quad": [[l, t], [r, t], [r, b], [l, b]], "string": s, "role": role})
        return tid

    def part(self, pid, cls, body, pins, turn=0, mirror=False, **extra):
        rec = {"id": pid, "class": cls, "orientation": {"turn": turn, "mirror": mirror}, "body": list(body),
               "pins": [{"name": n, "x": x, "y": y} for n, x, y in pins]}
        rec.update(extra)
        self.parts.append(rec)

    def junction(self, x, y):
        self.d.ellipse([x - 3, y - 3, x + 3, y + 3], fill=INK)
        self.junctions.append([x, y])

    # symbols ──────────────────────────────────────────────────────────────────────────────────────────────────────

    def resistor_v(self, x, y0, y1):
        pts = [(x, y0), (x, y0 + 6)]
        n = 6
        for i in range(n):
            pts.append((x + (6 if i % 2 == 0 else -6), y0 + 6 + (i + 0.5) * (y1 - y0 - 12) / n))
        pts += [(x, y1 - 6), (x, y1)]
        self.d.line(pts, fill=INK, width=PEN)
        return [x - 8, y0, x + 8, y1]

    def inductor_v(self, x, y0, y1):
        self.d.line([(x, y0), (x, y0 + 6)], fill=INK, width=PEN)
        self.d.line([(x, y1 - 6), (x, y1)], fill=INK, width=PEN)
        n, span = 4, (y1 - y0 - 12)
        for i in range(n):
            a = y0 + 6 + i * span / n
            self.d.arc([x - span / n / 2, a, x + span / n / 2, a + span / n], 270, 90, fill=INK, width=PEN)
        return [x - 2, y0, x + span / n / 2 + 2, y1]

    def capacitor_h(self, x0, x1, y):
        m = (x0 + x1) / 2
        self.d.line([(x0, y), (m - 3, y)], fill=INK, width=PEN)
        self.d.line([(m + 3, y), (x1, y)], fill=INK, width=PEN)
        self.d.line([(m - 3, y - 9), (m - 3, y + 9)], fill=INK, width=PEN)
        self.d.line([(m + 3, y - 9), (m + 3, y + 9)], fill=INK, width=PEN)
        return [x0, y - 10, x1, y + 10]

    def ground(self, x, y):
        self.d.line([(x - 10, y), (x + 10, y)], fill=INK, width=PEN)
        self.d.line([(x - 6, y + 5), (x + 6, y + 5)], fill=INK, width=PEN)
        self.d.line([(x - 2, y + 10), (x + 2, y + 10)], fill=INK, width=PEN)
        return [x - 10, y - 1, x + 10, y + 11]

    def terminal(self, cx, cy):
        self.d.ellipse([cx - 4, cy - 4, cx + 4, cy + 4], outline=INK, width=PEN)
        return [cx - 4, cy - 4, cx + 4, cy + 4]

    # output ───────────────────────────────────────────────────────────────────────────────────────────────────────

    def save(self, folder: Path = FIXTURES):
        folder.mkdir(parents=True, exist_ok=True)
        self.img.save(folder / f"{self.name}.png")
        label = {"version": 1, "image": f"{self.name}.png", "width": self.w, "height": self.h,
                 "origin": "synth", "family": None, "style": "fixture",
                 "parts": self.parts, "wires": self.wires, "junctions": self.junctions,
                 "crossings": self.crossings, "texts": self.texts, "annotations": [], "ignore": []}
        with open(folder / f"{self.name}.mllabel.json", "w", encoding="utf-8") as f:
            json.dump(label, f, indent=1, ensure_ascii=False)
            f.write("\n")


def divider() -> Sheet:
    """A terminal, two resistors in series to ground, the midpoint wired out to a second terminal through a dot."""
    s = Sheet("divider", 200, 200)
    s.part("p1", "terminal", s.terminal(60, 15), [("1", 60, 19)], turn=180)
    s.wire((60, 19), (60, 40))
    s.part("p2", "resistor", s.resistor_v(60, 40, 80), [("1", 60, 40), ("2", 60, 80)],
           designator=s.text("t1", (76, 46), "R1", "designator"), values=[s.text("t2", (76, 60), "1k", "value")])
    s.wire((60, 80), (60, 110))
    s.wire((60, 100), (150, 100))
    s.junction(60, 100)
    s.part("p3", "terminal", s.terminal(154, 100), [("1", 150, 100)], turn=270)
    s.text("t3", (128, 84), "OUT", "net")
    s.part("p4", "resistor", s.resistor_v(60, 110, 150), [("1", 60, 110), ("2", 60, 150)],
           designator=s.text("t4", (76, 116), "R2", "designator"), values=[s.text("t5", (76, 130), "2k2", "value")])
    s.wire((60, 150), (60, 165))
    s.part("p5", "ground", s.ground(60, 165), [("1", 60, 165)])
    return s


def rc_port() -> Sheet:
    """A port, a series capacitor and a shunt inductor to ground."""
    s = Sheet("rc-port", 200, 140)
    s.d.ellipse([20, 64, 44, 88], outline=INK, width=PEN)
    s.d.line([(32, 60), (32, 64)], fill=INK, width=PEN)
    s.d.line([(32, 88), (32, 100)], fill=INK, width=PEN)
    s.d.line([(28, 72), (36, 72)], fill=INK, width=1)
    s.d.line([(32, 68), (32, 76)], fill=INK, width=1)
    s.part("p1", "port", [20, 60, 44, 100], [("+", 32, 60), ("−", 32, 100)],
           designator=s.text("t1", (2, 46), "P1", "designator"))
    s.wire((32, 60), (32, 30), (80, 30))
    s.part("p2", "capacitor", s.capacitor_h(80, 102, 30), [("1", 102, 30), ("2", 80, 30)], turn=90,
           designator=s.text("t2", (82, 4), "C1", "designator"), values=[s.text("t3", (82, 42), "10p", "value")])
    s.wire((102, 30), (150, 30), (150, 45))
    s.part("p3", "inductor", s.inductor_v(150, 45, 85), [("1", 150, 45), ("2", 150, 85)],
           designator=s.text("t4", (166, 50), "L1", "designator"), values=[s.text("t5", (166, 64), "4n7", "value")])
    s.wire((150, 85), (150, 100))
    s.part("p4", "ground", s.ground(150, 100), [("1", 150, 100)])
    s.wire((32, 100), (32, 110))
    s.part("p5", "ground", s.ground(32, 110), [("1", 32, 110)])
    return s


def crossing() -> Sheet:
    """A battery and a diode whose wires cross without a dot — a crossing that does not connect — and the battery's
    minus wired into the middle of the diode's ground wire with no dot — a T-join, which does."""
    s = Sheet("crossing", 200, 200)
    s.d.line([(32, 70), (32, 76)], fill=INK, width=PEN)
    s.d.line([(20, 76), (44, 76)], fill=INK, width=PEN)
    s.d.line([(26, 83), (38, 83)], fill=INK, width=PEN + 2)
    s.d.line([(32, 83), (32, 90)], fill=INK, width=PEN)
    s.part("p1", "source-dc", [20, 70, 44, 90], [("+", 32, 70), ("−", 32, 90)], variant="battery",
           designator=s.text("t1", (2, 56), "V1", "designator"))
    s.wire((32, 70), (32, 40), (199, 40))
    s.wire((100, 1), (100, 120))
    s.crossings.append([100, 40])
    s.d.line([(100, 120), (100, 128)], fill=INK, width=PEN)
    s.d.polygon([(90, 128), (110, 128), (100, 142)], outline=INK, width=PEN)
    s.d.line([(90, 142), (110, 142)], fill=INK, width=PEN)
    s.d.line([(100, 142), (100, 150)], fill=INK, width=PEN)
    s.part("p2", "diode", [90, 120, 110, 150], [("a", 100, 120), ("c", 100, 150)],
           designator=s.text("t2", (116, 128), "D1", "designator"))
    s.wire((100, 150), (100, 170))
    s.part("p3", "ground", s.ground(100, 170), [("1", 100, 170)])
    s.wire((32, 90), (32, 160), (100, 160))
    return s


ALL = (divider, rc_port, crossing)


def main() -> int:
    for make in ALL:
        make().save()
    print(f"wrote {len(ALL)} fixtures to {FIXTURES}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
