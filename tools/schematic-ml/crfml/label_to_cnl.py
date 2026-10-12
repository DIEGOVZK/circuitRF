"""A label's circuit as a netlist — the picture's netlist truth (brief-iml-4 R-iml4-3).

    python -m crfml.label_to_cnl <label.mllabel.json | folder>… [--check] [--stdout]
    python -m crfml.label_to_cnl [--data <dir>] [--check]        every label in the data home's real/

Writes ``<id>.cnl`` beside each label. When ``<id>.cnl`` already holds a truth the importer copied (a corpus
picture's ``answer.cnl``), the derived one goes to ``<id>.derived.cnl`` instead and the two are compared by
``NetlistShape.SameTopology`` in tests/Ui.Tests/Vision/LabelToCnlTests. ``--check`` runs ``circuitrf check`` on each.

The rules (docs/design/schematic-vision.md §6):

- **Nets from wires.** Touching segments are one net: a polyline is one piece, an end on another wire (a corner or a
  T-join) joins them, two pins together join. Two wires that cross join **only at a listed junction**. A pin is on
  the wire it touches, end or middle, within a pen width.
- **Parts by kind**, from classes.json's ``circuitrf`` and cnl-types.json, pins in the class's order; the system
  blocks bind each port with ground as its minus, as circuitRF's own netlists do. A ground symbol is node 0. A box
  (op-amp, IC, block) is an S-parameter block whose pins on ground fold into its reference and whose unwired pins
  are left out. A stub's missing end is open or on ground; a transformer is two inductors and a Mutual.
  ``probe`` joins every wire it touches (an inline current probe is a short); ``controller`` and ``no-connect`` are
  nothing.
- **Names.** A ``net`` text names the wire nearest it, a ``port`` text the terminal or port nearest it; one name on
  two wires is one net. ``GND`` is ground.
- **Ports, by the reader's rules** (IMF-6): a ``port`` part is a port, referenced to ground when its minus is wired
  to nothing; a ``terminal`` is a port to ground unless its
  name is a supply (``VCC``, ``+5V``), when it is only a name; a ``power-port`` is only a name; a net named as an
  input or output (``IN``, ``RF OUT``) with no port of its own gets one. AC sources are numbered first.
- **Values** from the part's value texts read by the grammar, else its ``params``; a part with neither keeps its
  kind's default.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path

from .data import KIT_DIR, DataHomeError, load_classes, resolve_data_home
from .values import Value, read_value_text

CNL_TYPES_PATH = KIT_DIR / "cnl-types.json"
LABEL_SUFFIX = ".mllabel.json"
MARK = "; netlist truth from"


def _load_types() -> dict:
    with open(CNL_TYPES_PATH, encoding="utf-8") as f:
        return json.load(f)


# ── the reader's port words (PictureTerminals.PortWords) ────────────────────────────────────────────────────────────

_INPUTS = ["IN", "INPUT", "RFIN", "RFINPUT", "VIN", "VI"]
_OUTPUTS = ["OUT", "OUTPUT", "RFOUT", "RFOUTPUT", "VOUT", "VO"]


def _normal(name: str) -> str:
    t = name.strip().upper().replace(" ", "").replace("_", "")
    return t.replace("-", "") if len(t) > 2 else t


def is_input(name: str) -> bool:
    return _normal(name) in _INPUTS


def is_output(name: str) -> bool:
    return _normal(name) in _OUTPUTS


def is_supply(name: str) -> bool:
    if is_input(name) or is_output(name):
        return False
    t = name.strip().lstrip("+-−")
    return bool(t) and (t[0].upper() == "V" or t[0].isdigit()) and "GND" not in t.upper()


def identifier(text: str, prefix: str) -> str:
    """A name the netlist takes: letters, digits and underscores, never starting with a digit."""
    t = re.sub(r"[^A-Za-z0-9_]", "_", text.strip().replace("+", "p").replace("−", "m"))
    t = re.sub(r"_+", "_", t).strip("_")
    if not t:
        return prefix
    return prefix + t if t[0].isdigit() else t


# ── geometry ────────────────────────────────────────────────────────────────────────────────────────────────────────

def _seg_dist(p, a, b) -> float:
    (px, py), (ax, ay), (bx, by) = p, a, b
    dx, dy = bx - ax, by - ay
    L2 = dx * dx + dy * dy
    t = 0.0 if L2 == 0 else max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / L2))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def _poly_dist(p, pts) -> float:
    if len(pts) == 1:
        return math.hypot(p[0] - pts[0][0], p[1] - pts[0][1])
    return min(_seg_dist(p, pts[i], pts[i + 1]) for i in range(len(pts) - 1))


def _intersections(pa, pb):
    """Points where polyline ``pa`` crosses polyline ``pb``."""
    out = []
    for i in range(len(pa) - 1):
        (x1, y1), (x2, y2) = pa[i], pa[i + 1]
        for j in range(len(pb) - 1):
            (x3, y3), (x4, y4) = pb[j], pb[j + 1]
            d = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4)
            if abs(d) < 1e-9:
                continue
            t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / d
            u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / d
            if -1e-6 <= t <= 1 + 1e-6 and -1e-6 <= u <= 1 + 1e-6:
                out.append((x1 + t * (x2 - x1), y1 + t * (y2 - y1)))
    return out


def _box_dist(p, box) -> float:
    x0, y0, x1, y1 = box
    dx = max(x0 - p[0], 0.0, p[0] - x1)
    dy = max(y0 - p[1], 0.0, p[1] - y1)
    return math.hypot(dx, dy)


def _quad_box(q):
    xs, ys = [p[0] for p in q], [p[1] for p in q]
    return min(xs), min(ys), max(xs), max(ys)


def _text_height(t) -> float:
    q = t["quad"]
    return max(1.0, math.hypot(q[3][0] - q[0][0], q[3][1] - q[0][1]))


class _Union:
    def __init__(self):
        self.parent: dict = {}

    def find(self, a):
        self.parent.setdefault(a, a)
        while self.parent[a] != a:
            self.parent[a] = self.parent[self.parent[a]]
            a = self.parent[a]
        return a

    def join(self, a, b):
        ra, rb = self.find(a), self.find(b)
        if ra != rb:
            self.parent[rb] = ra


# ── the conversion ──────────────────────────────────────────────────────────────────────────────────────────────────

def _si_text(v: float) -> str:
    return f"{v:.6g}"


def _value_of(part, texts, dim: str, key: str) -> str | None:
    for tid in part.get("values", []):
        t = texts.get(tid)
        if t is None or t["string"] is None:
            continue
        v = read_value_text(t["string"])
        if isinstance(v, Value) and not v.not_fitted and (dim in v.dims or not v.dims):
            return _si_text(v.si)
    if key in part.get("params", {}):
        return part["params"][key]
    return None


def convert(label: dict, classes: dict | None = None, name: str = "") -> str:
    """The netlist text for ``label``."""
    classes = classes or load_classes()
    types = _load_types()
    by_name = {c["name"]: c for c in classes["classes"]}
    texts = {t["id"]: t for t in label["texts"]}
    wires = [w["points"] for w in label["wires"]]
    pens = sorted(w["pen"] for w in label["wires"] if "pen" in w)
    pen = pens[len(pens) // 2] if pens else 2.0
    reach = max(pen, 1.5) + 1.0
    junctions = label.get("junctions", [])
    W, H = label["width"], label["height"]

    u = _Union()
    # wires among themselves
    for i, a in enumerate(wires):
        u.find(("w", i))
        for j in range(i + 1, len(wires)):
            b = wires[j]
            if any(_poly_dist(e, b) <= reach for e in (a[0], a[-1])) or any(_poly_dist(e, a) <= reach for e in (b[0], b[-1])):
                u.join(("w", i), ("w", j))
                continue
            for x in _intersections(a, b):
                if any(math.hypot(x[0] - jx, x[1] - jy) <= reach for jx, jy in junctions):
                    u.join(("w", i), ("w", j))
                    break

    parts = label["parts"]
    pins = []  # (part index, pin name, x, y)
    for k, p in enumerate(parts):
        for q in p["pins"]:
            pins.append((k, q["name"], q["x"], q["y"]))
    off_picture = lambda x, y: x < 0 or y < 0 or x > W or y > H

    for k, nm, x, y in pins:
        node = ("p", k, nm)
        u.find(node)
        if off_picture(x, y) and parts[k].get("cut"):
            continue
        for i, w in enumerate(wires):
            if _poly_dist((x, y), w) <= reach:
                u.join(node, ("w", i))
    for a in range(len(pins)):
        for b in range(a + 1, len(pins)):
            if pins[a][0] != pins[b][0] and math.hypot(pins[a][2] - pins[b][2], pins[a][3] - pins[b][3]) <= reach:
                u.join(("p", pins[a][0], pins[a][1]), ("p", pins[b][0], pins[b][1]))

    # a probe is a short across everything it touches
    for k, p in enumerate(parts):
        if p["class"] != "probe":
            continue
        # its body and its leads: the box round the body and its pin, so an inline probe's far lead is inside too
        xs = [p["body"][0], p["body"][2]] + [q["x"] for q in p["pins"]]
        ys = [p["body"][1], p["body"][3]] + [q["y"] for q in p["pins"]]
        box = (min(xs), min(ys), max(xs), max(ys))
        touching = [("p", k, q["name"]) for q in p["pins"]]
        for i, w in enumerate(wires):
            if any(_box_dist(e, box) <= reach for e in (w[0], w[-1])):
                touching.append(("w", i))
        for t in touching[1:]:
            u.join(touching[0], t)

    # ground
    GROUND = ("ground",)
    u.find(GROUND)
    for k, p in enumerate(parts):
        if p["class"] == "ground":
            for q in p["pins"]:
                u.join(GROUND, ("p", k, q["name"]))

    # names
    def nearest_wire(t):
        box = _quad_box(t["quad"])
        h = _text_height(t)
        best, bd = None, None
        for i, w in enumerate(wires):
            d = min(_box_dist(pt, box) for pt in _dense(w, step=max(2.0, h / 2)))
            if bd is None or d < bd:
                best, bd = i, d
        return best if bd is not None and bd <= 2.0 * h else None

    def nearest_part(t, kinds):
        box = _quad_box(t["quad"])
        h = _text_height(t)
        best, bd = None, None
        for k, p in enumerate(parts):
            if p["class"] not in kinds:
                continue
            b = p["body"]
            d = max(0.0, max(b[0] - box[2], box[0] - b[2], 0.0)) + max(0.0, max(b[1] - box[3], box[1] - b[3], 0.0))
            if bd is None or d < bd:
                best, bd = k, d
        return best if bd is not None and bd <= 4.0 * h else None

    named: dict[str, list] = {}
    part_name: dict[int, str] = {}
    for t in label["texts"]:
        if t["string"] is None or t["role"] not in ("net", "port"):
            continue
        s = t["string"].strip()
        if not s:
            continue
        k = nearest_part(t, ("terminal", "power-port", "port")) if t["role"] == "port" else None
        if k is None and t["role"] == "net":
            k = nearest_part(t, ("power-port", "terminal"))
            if k is not None:
                # a name beside a power port or terminal names it; else the wire it sits on
                kb = parts[k]["body"]
                tb = _quad_box(t["quad"])
                h = _text_height(t)
                if _box_dist(((tb[0] + tb[2]) / 2, (tb[1] + tb[3]) / 2), kb) > 2.5 * h:
                    k = None
        if k is not None:
            part_name.setdefault(k, s)
            node = ("p", k, parts[k]["pins"][0]["name"]) if parts[k]["pins"] else None
        else:
            i = nearest_wire(t)
            node = ("w", i) if i is not None else None
        if node is None:
            continue
        if s.upper() in ("GND", "0"):
            u.join(GROUND, node)
        else:
            named.setdefault(s, []).append(node)
    for s, nodes in named.items():
        for n in nodes[1:]:
            u.join(nodes[0], n)

    # net names: ground 0; a named net its (first) name; the rest n1, n2, …
    root_name: dict = {u.find(GROUND): "0"}
    used = {"0"}
    for s, nodes in sorted(named.items()):
        r = u.find(nodes[0])
        if r in root_name:
            continue
        nm = identifier(s, "n").lower()
        base, i = nm, 1
        while nm in used:
            i += 1
            nm = f"{base}_{i}"
        root_name[r] = nm
        used.add(nm)
    counter = [0]

    def net(node) -> str:
        r = u.find(node)
        if r not in root_name:
            while True:
                counter[0] += 1
                nm = f"n{counter[0]}"
                if nm not in used:
                    break
            root_name[r] = nm
            used.add(nm)
        return root_name[r]

    # how many things each net carries — a box's unwired pin is left out
    degree: dict = {}
    for k, nm, _, _ in pins:
        r = u.find(("p", k, nm))
        degree[r] = degree.get(r, 0) + 1
    wired = {u.find(("w", i)) for i in range(len(wires))}

    def connected(k, nm) -> bool:
        r = u.find(("p", k, nm))
        return r == u.find(GROUND) or r in wired or degree.get(r, 0) > 1

    # designators
    names_used: set[str] = set()
    counters: dict[str, int] = {}

    def designator(p, prefix: str, register: bool = True) -> str:
        t = texts.get(p.get("designator") or "")
        cand = identifier(t["string"], prefix) if t and t["string"] else None
        if not register:
            return cand or ""
        if not cand or cand in names_used:
            while True:
                counters[prefix] = counters.get(prefix, 0) + 1
                cand = f"{prefix}{counters[prefix]}"
                if cand not in names_used:
                    break
        names_used.add(cand)
        return cand

    lines: list[str] = []
    ports: list[tuple[str, str, list[str]]] = []  # (type, name, nets) numbered at the end
    ported: set = set()
    tone_lines = []

    prefix_of = {"R": "R", "C": "C", "L": "L", "Diode": "D", "BJT_NPN": "Q", "BJT_PNP": "Q", "FET_Curtice": "Q",
                 "PFET_Curtice": "Q", "SnP": "U", "TLIN": "TL", "MLIN": "TL", "MTEE": "TEE", "MCROSS": "CROSS",
                 "MBEND": "BEND", "MTAPER": "TAPER", "VIAGND": "V", "Vdc": "V", "P1Tone": "V", "I_1Tone": "I",
                 "Port": "P", "Mutual": "K"}

    for k, p in enumerate(parts):
        cls = by_name.get(p["class"])
        if cls is None:
            continue
        emit = cls["circuitrf"]
        if emit == "ignore" or p["class"] == "ground":
            continue
        kind = emit["kind"]
        pin_order = [q["name"] for q in cls["pins"]] if cls["pins"] != "perimeter" else [q["name"] for q in p["pins"]]
        have = {q["name"] for q in p["pins"]}
        nets = [net(("p", k, nm)) if nm in have else net(("open", k, nm)) for nm in pin_order]

        if p["class"] == "terminal":
            nm = part_name.get(k)
            if nm is not None and is_supply(nm):
                continue
            r = u.find(("p", k, pin_order[0]))
            if r == u.find(GROUND) or r in ported:
                continue
            ported.add(r)
            ports.append(("Port", identifier(nm, "P") if nm else "", [nets[0], "0"]))
            continue
        if p["class"] == "power-port":
            continue
        typ = types["types"][kind]
        if kind == "Term":
            r = u.find(("p", k, pin_order[0]))
            ported.add(r)
            if not connected(k, pin_order[1]):
                nets = [nets[0], "0"]   # a minus wired to nothing: the port's own ground (a ground drawn in its box)
            nm = part_name.get(k)
            ports.append(("Port", designator(p, "P", register=False) if p.get("designator") else (identifier(nm, "P") if nm else ""), nets))
            continue
        if kind == "P1Tone":
            ported.add(u.find(("p", k, pin_order[0])))
            tone_lines.append((designator(p, "V"), nets))
            continue
        if kind == "Inductor" and emit.get("count") == 2:
            base = designator(p, "T")
            la, lb = base + "a", base + "b"
            names_used.update((la, lb))
            lines.append(f"L:{la} {nets[0]} {nets[1]} L=1e-06")
            lines.append(f"L:{lb} {nets[2]} {nets[3]} L=1e-06")
            lines.append(f'Mutual:K{base} M=9.9e-07 Inductor1="{la}" Inductor2="{lb}"')
            continue
        if kind == "Snp":
            keep = [(nm, n) for nm, n in zip(pin_order, nets) if n != "0" and connected(k, nm)]
            if not keep:
                continue
            lines.append(f"SnP:{designator(p, 'U')} {' '.join(n for _, n in keep)} 0 NumPorts={len(keep)}")
            continue
        if "open" in emit:
            nets = nets + [f"open_{designator_hint(p, k)}"]
        if "grounded" in emit:
            nets = nets + ["0"]
        if kind in types["paired"]:
            nets = [x for n in nets for x in (n, "0")]
        nets = nets + types["tail"].get(kind, [])
        name_ = designator(p, prefix_of.get(typ, typ[:1].upper()))
        params = []
        if typ in ("R", "C", "L"):
            dim = {"R": "Resistance", "C": "Capacitance", "L": "Inductance"}[typ]
            v = _value_of(p, texts, dim, typ)
            if v is not None:
                params.append(f"{typ}={v}")
        elif typ == "Vdc":
            params.append(f"Vdc={p.get('params', {}).get('Vdc', '1')}")
        if kind in types["params"]:
            params.append(types["params"][kind])
        lines.append(f"{typ}:{name_} {' '.join(nets)}" + "".join(" " + q for q in params))

    # names that call a net an input or an output are a port (D13)
    for s, nodes in sorted(named.items()):
        r = u.find(nodes[0])
        if r == u.find(GROUND) or r in ported or not (is_input(s) or is_output(s)):
            continue
        if any(n[0] == "p" and parts[n[1]]["class"] == "power-port" for n in nodes):
            continue
        ported.add(r)
        ports.append(("Port", identifier(s, "P"), [net(nodes[0]), "0"]))

    out = [f"{MARK} {name or label.get('image', '')} (crfml.label_to_cnl)"]
    number = 0
    for nm, nets in tone_lines:
        number += 1
        out.append(f"P1Tone:{nm} {' '.join(nets)} Num={number} Z=50 Ohm")
    for typ, nm, nets in ports:
        number += 1
        pn = nm if nm and nm not in names_used else f"P{number}"
        while pn in names_used:
            pn = pn + "_"
        names_used.add(pn)
        out.append(f"Port:{pn} {' '.join(nets)} Num={number} Z=50 Ohm")
    out.extend(lines)
    return "\n".join(out) + "\n"


def designator_hint(p, k) -> str:
    return identifier(p["id"], "p") + f"_{k}"


def _dense(pts, step: float):
    """Points along a polyline every ``step`` pixels, ends included."""
    out = [tuple(pts[0])]
    for i in range(len(pts) - 1):
        (x0, y0), (x1, y1) = pts[i], pts[i + 1]
        n = max(1, int(math.hypot(x1 - x0, y1 - y0) / step))
        for j in range(1, n + 1):
            out.append((x0 + (x1 - x0) * j / n, y0 + (y1 - y0) * j / n))
    return out


# ── files ───────────────────────────────────────────────────────────────────────────────────────────────────────────

def target_for(label_path: Path) -> Path:
    """Where a label's derived netlist goes: ``<id>.cnl``, or ``<id>.derived.cnl`` beside an imported truth."""
    stem = label_path.name[: -len(LABEL_SUFFIX)]
    truth = label_path.parent / f"{stem}.cnl"
    if truth.is_file() and not truth.read_text(encoding="utf-8").startswith(MARK):
        return label_path.parent / f"{stem}.derived.cnl"
    return truth


def write(label_path: Path, classes: dict | None = None) -> Path:
    with open(label_path, encoding="utf-8") as f:
        label = json.load(f)
    text = convert(label, classes, label_path.name[: -len(LABEL_SUFFIX)])
    out = target_for(label_path)
    out.write_text(text, encoding="utf-8")
    return out


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m crfml.label_to_cnl", description=__doc__.split("\n\n")[0])
    ap.add_argument("paths", nargs="*", help="labels, or folders searched for *.mllabel.json")
    ap.add_argument("--data", help="with no paths: every label in this data home's real/")
    ap.add_argument("--check", action="store_true", help="run `circuitrf check` on each netlist written")
    ap.add_argument("--stdout", action="store_true", help="print the netlist instead of writing it")
    args = ap.parse_args(argv)
    paths = [Path(p) for p in args.paths]
    if not paths:
        try:
            paths = [resolve_data_home(args.data, create=False) / "real"]
        except DataHomeError as e:
            print(f"refused: {e}", file=sys.stderr)
            return 1
    labels = []
    for p in paths:
        labels.extend(sorted(p.rglob("*" + LABEL_SUFFIX)) if p.is_dir() else [p])
    labels = [p for p in labels if not p.name.endswith(".draft" + LABEL_SUFFIX)]
    classes = load_classes()
    failed = 0
    for lp in labels:
        if args.stdout:
            print(convert(json.loads(lp.read_text(encoding="utf-8")), classes, lp.name[: -len(LABEL_SUFFIX)]), end="")
            continue
        out = write(lp, classes)
        line = f"{out}"
        if args.check:
            from .cli import check
            ok, said = check(out)
            failed += not ok
            line += "  check: ok" if ok else f"  check: FAILED\n{said}"
        print(line)
    print(f"{len(labels)} label(s) converted" + (f", {failed} failed check" if args.check else "") + ".", file=sys.stderr)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
