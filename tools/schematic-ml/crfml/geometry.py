"""Picture geometry the label tools share (brief-iml-4): ink, wires traced from ink, bodies and pins fitted to ink.

The pre-labeller drafts wires with it, the annotation page's snap key calls it, and a scripted correction uses it to
turn "a resistor about here" into a body box that follows the label guide's rule. Nothing here decides what a part is:
it measures ink a person (or the reader) has already said is a part, a word or a wire.

Coordinates are continuous picture pixels, origin top-left, y down: pixel (i, j) covers [i, i+1) × [j, j+1), so a
one-pixel line drawn in column 30 has its centreline at x = 30.5.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
from PIL import Image

INK_THRESHOLD = 48


def load_rgb(path: Path) -> np.ndarray:
    """The picture as H×W×3 uint8. A picture with transparency is laid on white first: a transparent PNG stores its
    paper as transparent black, and read as plain RGB its ink and its paper are the same colour."""
    with Image.open(path) as img:
        img.load()
        if img.mode in ("RGBA", "LA") or (img.mode == "P" and "transparency" in img.info):
            rgba = img.convert("RGBA")
            paper = Image.new("RGBA", rgba.size, (255, 255, 255, 255))
            return np.asarray(Image.alpha_composite(paper, rgba).convert("RGB"))
        return np.asarray(img.convert("RGB"))


def background(rgb: np.ndarray) -> np.ndarray:
    """The paper colour: the commonest colour on the picture's border, quantised to 16 levels a channel."""
    rgb = rgb.astype(np.int16)
    border = np.concatenate([rgb[0], rgb[-1], rgb[:, 0], rgb[:, -1]])
    quant = border // 16
    keys = quant[:, 0] * 256 + quant[:, 1] * 16 + quant[:, 2]
    k, counts = np.unique(keys, return_counts=True)
    return border[keys == k[np.argmax(counts)]].mean(axis=0)


def ink_mask(rgb: np.ndarray, threshold: int = INK_THRESHOLD) -> np.ndarray:
    """Pixels that differ from the paper by more than ``threshold`` in some channel."""
    return np.abs(rgb.astype(np.int16) - background(rgb)).max(axis=2) > threshold


def picture_ink(path: Path, threshold: int = INK_THRESHOLD) -> np.ndarray:
    return ink_mask(load_rgb(path), threshold)


# ── boxes ───────────────────────────────────────────────────────────────────────────────────────────────────────────

def quad_box(quad) -> tuple[float, float, float, float]:
    xs, ys = [p[0] for p in quad], [p[1] for p in quad]
    return min(xs), min(ys), max(xs), max(ys)


def blank(mask: np.ndarray, boxes, grow: float = 0.0) -> np.ndarray:
    """A copy of ``mask`` with every box (x0, y0, x1, y1) cleared, each grown by ``grow`` pixels."""
    out = mask.copy()
    h, w = mask.shape
    for x0, y0, x1, y1 in boxes:
        a, b = max(0, int(math.floor(x0 - grow))), min(w, int(math.ceil(x1 + grow)))
        c, d = max(0, int(math.floor(y0 - grow))), min(h, int(math.ceil(y1 + grow)))
        if a < b and c < d:
            out[c:d, a:b] = False
    return out


def ink_box(mask: np.ndarray, box) -> tuple[float, float, float, float] | None:
    """The tight box round the ink inside ``box``, or None when there is none."""
    h, w = mask.shape
    x0, y0 = max(0, int(math.floor(box[0]))), max(0, int(math.floor(box[1])))
    x1, y1 = min(w, int(math.ceil(box[2]))), min(h, int(math.ceil(box[3])))
    if x0 >= x1 or y0 >= y1:
        return None
    sub = mask[y0:y1, x0:x1]
    rows, cols = np.flatnonzero(sub.any(axis=1)), np.flatnonzero(sub.any(axis=0))
    if rows.size == 0:
        return None
    return float(x0 + cols[0]), float(y0 + rows[0]), float(x0 + cols[-1] + 1), float(y0 + rows[-1] + 1)


def body_box(mask: np.ndarray, box, strip: str = "", pen: float = 2.0) -> tuple[float, float, float, float] | None:
    """A part's body by the label guide's rule — its drawing without the straight leads that end on its pins.

    ``box`` is roughly the part (leads may cross its edge); ``strip`` names the sides its pins are on, from ``"tblr"``.
    The ink inside ``box`` is boxed, then each named side is pulled in past every row (or column) whose ink is no
    wider than a lead — a straight line one pen wide — so the box ends where the drawing stops being a lead."""
    tight = ink_box(mask, box)
    if tight is None:
        return None
    x0, y0, x1, y1 = (int(v) for v in tight)
    sub = mask[y0:y1, x0:x1]
    lead = 1.5 * pen + 1.0

    def width(line: np.ndarray) -> int:
        idx = np.flatnonzero(line)
        return 0 if idx.size == 0 else int(idx[-1] - idx[0] + 1)

    top, bottom, left, right = 0, sub.shape[0], 0, sub.shape[1]
    if "t" in strip:
        while top < bottom - 1 and width(sub[top]) <= lead:
            top += 1
    if "b" in strip:
        while bottom - 1 > top and width(sub[bottom - 1]) <= lead:
            bottom -= 1
    if "l" in strip:
        while left < right - 1 and width(sub[top:bottom, left]) <= lead:
            left += 1
    if "r" in strip:
        while right - 1 > left and width(sub[top:bottom, right - 1]) <= lead:
            right -= 1
    inner = ink_box(sub[top:bottom, left:right], (0, 0, right - left, bottom - top))
    if inner is None:
        return None
    return (x0 + left + inner[0], y0 + top + inner[1], x0 + left + inner[2], y0 + top + inner[3])


def lead_centre(mask: np.ndarray, x: float, y: float, side: str, reach: float = 6.0) -> tuple[float, float]:
    """The centre of the lead crossing a body's edge near (x, y): the run of ink just outside the edge on ``side``
    nearest to (x, y), measured along the edge. The point stays on the edge; only the cross coordinate moves."""
    h, w = mask.shape
    if side in "tb":
        row = int(math.floor(y)) + (-1 if side == "t" else 0)
        row = min(max(row, 0), h - 1)
        a, b = max(0, int(x - reach)), min(w, int(x + reach) + 1)
        idx = np.flatnonzero(mask[row, a:b])
        if idx.size == 0:
            return x, y
        runs = np.split(idx, np.flatnonzero(np.diff(idx) > 1) + 1)
        best = min(runs, key=lambda r: abs(a + (r[0] + r[-1] + 1) / 2 - x))
        return a + (best[0] + best[-1] + 1) / 2, y
    col = int(math.floor(x)) + (-1 if side == "l" else 0)
    col = min(max(col, 0), w - 1)
    a, b = max(0, int(y - reach)), min(h, int(y + reach) + 1)
    idx = np.flatnonzero(mask[a:b, col])
    if idx.size == 0:
        return x, y
    runs = np.split(idx, np.flatnonzero(np.diff(idx) > 1) + 1)
    best = min(runs, key=lambda r: abs(a + (r[0] + r[-1] + 1) / 2 - y))
    return x, a + (best[0] + best[-1] + 1) / 2


# ── wires ───────────────────────────────────────────────────────────────────────────────────────────────────────────

@dataclass
class Segment:
    """An orthogonal stroke: ``horizontal`` with centreline ``c`` (a y) from ``a`` to ``b`` (x), or vertical with
    centreline x = ``c`` from y = ``a`` to ``b``. ``pen`` is its measured thickness."""
    horizontal: bool
    c: float
    a: float
    b: float
    pen: float

    def points(self) -> list[list[float]]:
        if self.horizontal:
            return [[_r(self.a), _r(self.c)], [_r(self.b), _r(self.c)]]
        return [[_r(self.c), _r(self.a)], [_r(self.c), _r(self.b)]]

    def ends(self) -> list[tuple[float, float]]:
        return [(self.a, self.c), (self.b, self.c)] if self.horizontal else [(self.c, self.a), (self.c, self.b)]


def _r(v: float) -> float:
    return round(float(v), 1)


def _runs(line: np.ndarray, min_len: int) -> list[tuple[int, int]]:
    """Runs of True in a 1-D array at least ``min_len`` long, as [start, end) pairs."""
    d = np.diff(np.concatenate(([0], line.view(np.int8), [0])))
    starts, ends = np.flatnonzero(d == 1), np.flatnonzero(d == -1)
    keep = (ends - starts) >= min_len
    return list(zip(starts[keep].tolist(), ends[keep].tolist()))


def _bands(mask: np.ndarray, min_len: int, horizontal: bool) -> list[Segment]:
    """Strokes along one axis: runs of ink at least ``min_len`` long in consecutive rows (or columns) that overlap
    by half their length, as one segment each, its centreline the band's middle and its extent the band's."""
    m = mask if horizontal else mask.T
    active: list[dict] = []
    done: list[dict] = []
    for r in range(m.shape[0]):
        runs = _runs(m[r], min_len)
        nxt = []
        used = set()
        for s, e in runs:
            hit = None
            for k, band in enumerate(active):
                if k in used:
                    continue
                ps, pe = band["last"]
                overlap = min(e, pe) - max(s, ps)
                if overlap >= 0.5 * min(e - s, pe - ps):
                    hit = k
                    break
            if hit is None:
                nxt.append({"first": r, "rows": [(s, e)], "last": (s, e)})
            else:
                used.add(hit)
                band = active[hit]
                band["rows"].append((s, e))
                band["last"] = (s, e)
                nxt.append(band)
        done.extend(b for k, b in enumerate(active) if k not in used)
        active = nxt
    done.extend(active)
    segs = []
    for b in done:
        t = len(b["rows"])
        # the band's whole extent: a neighbouring anti-aliased row may be shorter, and an end that overshoots a corner
        # to the far edge of the stroke it meets is pulled back by snap_ends
        segs.append(Segment(horizontal, b["first"] + t / 2.0, float(min(s for s, _ in b["rows"])),
                            float(max(e for _, e in b["rows"])), float(t)))
    return segs


def trace_wires(mask: np.ndarray, exclude=(), min_len: int | None = None, max_pen: float | None = None) -> list[Segment]:
    """Orthogonal wire strokes in the ink outside ``exclude`` (boxes: part bodies, words, annotations).

    A stroke is a band of rows (or columns) of ink runs at least ``min_len`` long; a band thicker than ``max_pen``
    (a filled shape, a band of chrome) or shorter than three times its thickness is not a wire. Diagonal wires are not
    found — the guide says to draw them by hand."""
    free = blank(mask, exclude, grow=1.0)
    raw = _bands(free, max(min_len or 0, 6), True) + _bands(free, max(min_len or 0, 6), False)
    if not raw:
        return []
    pens = sorted(s.pen for s in raw)
    median = pens[len(pens) // 2]
    limit = max_pen if max_pen is not None else max(3.0 * median, median + 4.0)
    floor = min_len if min_len is not None else max(8, int(4 * median))
    return [s for s in raw if s.pen <= limit and (s.b - s.a) >= max(floor, 3 * s.pen)]


def merge_collinear(segs: list[Segment], gap: float | None = None) -> list[Segment]:
    """Join strokes on one line whose ends are within ``gap`` (default two pens) — a stroke broken by a dot or a word."""
    out: list[Segment] = []
    for horizontal in (True, False):
        group = sorted((s for s in segs if s.horizontal == horizontal), key=lambda s: (round(s.c), s.a))
        merged: list[Segment] = []
        for s in group:
            for m in merged:
                g = gap if gap is not None else 2 * max(m.pen, s.pen) + 1
                if abs(m.c - s.c) <= max(m.pen, s.pen, 1.5) and s.a - m.b <= g and m.a - s.b <= g:
                    m.c = (m.c * (m.b - m.a) + s.c * (s.b - s.a)) / ((m.b - m.a) + (s.b - s.a))
                    m.a, m.b, m.pen = min(m.a, s.a), max(m.b, s.b), max(m.pen, s.pen)
                    break
            else:
                merged.append(Segment(s.horizontal, s.c, s.a, s.b, s.pen))
        out.extend(merged)
    return out


def snap_ends(segs: list[Segment], points=(), reach: float | None = None) -> None:
    """Pull each stroke's ends onto what they meet, in place: onto a crossing stroke's centreline (a corner or a T, so
    an end that overshoots to the far edge of the stroke it meets comes back to its middle) and onto a point (a pin)."""
    for s in segs:
        r = reach if reach is not None else s.pen + 2.0
        for end in ("a", "b"):
            v = getattr(s, end)
            best, bestd = None, None
            for o in segs:
                if o is s or o.horizontal == s.horizontal:
                    continue
                rr = max(r, o.pen / 2 + s.pen)
                # o's centreline is a constant along s's axis; s's centreline must lie within o's extent
                if not (o.a - rr <= s.c <= o.b + rr):
                    continue
                d = abs(o.c - v)
                if d <= rr and (bestd is None or d < bestd):
                    best, bestd = o.c, d
            for px, py in points:
                along, cross = (px, py) if s.horizontal else (py, px)
                if abs(cross - s.c) <= r and abs(along - v) <= r and (bestd is None or abs(along - v) < bestd):
                    best, bestd = along, abs(along - v)
            if best is not None:
                setattr(s, end, best)
        if s.a > s.b:
            s.a, s.b = s.b, s.a


def meets(segs: list[Segment], tol: float = 0.75) -> tuple[list[tuple[float, float]], list[tuple[float, float]]]:
    """Where strokes meet: T-joins (an end on another's interior, or three or more ends together) and crossings
    (two interiors), each as a point. A corner — two ends together — is neither."""
    tees, crosses = [], []
    for i, s in enumerate(segs):
        for o in segs[i + 1:]:
            if o.horizontal == s.horizontal:
                continue
            h, v = (s, o) if s.horizontal else (o, s)
            x, y = v.c, h.c
            if not (h.a - tol <= x <= h.b + tol and v.a - tol <= y <= v.b + tol):
                continue
            h_end = min(abs(x - h.a), abs(x - h.b)) <= tol
            v_end = min(abs(y - v.a), abs(y - v.b)) <= tol
            if h_end and v_end:
                continue
            (tees if (h_end or v_end) else crosses).append((x, y))
    # an end shared by three strokes or more (two collinear ends and a third) is a T too
    ends: dict[tuple[int, int], int] = {}
    for s in segs:
        for x, y in s.ends():
            key = (round(x), round(y))
            ends[key] = ends.get(key, 0) + 1
    for (x, y), n in ends.items():
        if n >= 3 and not any(abs(x - a) <= 1 and abs(y - b) <= 1 for a, b in tees):
            tees.append((float(x), float(y)))
    return _unique(tees), _unique(crosses)


def _unique(pts, tol: float = 1.5):
    out = []
    for p in pts:
        if not any(abs(p[0] - q[0]) <= tol and abs(p[1] - q[1]) <= tol for q in out):
            out.append(p)
    return out


def has_dot(mask: np.ndarray, x: float, y: float, pen: float) -> bool:
    """Whether a filled dot is drawn at (x, y): the four diagonal points just off both strokes' edges are all ink.
    Two crossing lines leave those points as paper; a dot covers them."""
    h, w = mask.shape
    d = pen / 2.0 + 1.0
    for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
        px, py = int(math.floor(x + sx * d)), int(math.floor(y + sy * d))
        if not (0 <= px < w and 0 <= py < h) or not mask[py, px]:
            return False
    return True


def centreline(mask: np.ndarray, points, reach: float = 4.0) -> list[list[float]]:
    """The annotation page's snap key: each point of a drawn polyline moved onto the middle of the ink stroke under it.
    A horizontal leg moves its points' y, a vertical leg their x; a point with no ink within ``reach`` stays."""
    pts = [list(map(float, p)) for p in points]
    h, w = mask.shape
    for i in range(len(pts) - 1):
        (x0, y0), (x1, y1) = pts[i], pts[i + 1]
        horizontal = abs(x1 - x0) >= abs(y1 - y0)
        if horizontal:
            a, b = sorted((int(x0), int(x1)))
            a, b = max(0, a), min(w, b + 1)
            ys = []
            for y in range(max(0, int(y0 - reach)), min(h, int(y0 + reach) + 1)):
                if b > a and mask[y, a:b].mean() > 0.6:
                    ys.append(y)
            if ys:
                c = (min(ys) + max(ys) + 1) / 2.0
                pts[i][1] = pts[i + 1][1] = c
        else:
            a, b = sorted((int(y0), int(y1)))
            a, b = max(0, a), min(h, b + 1)
            xs = []
            for x in range(max(0, int(x0 - reach)), min(w, int(x0 + reach) + 1)):
                if b > a and mask[a:b, x].mean() > 0.6:
                    xs.append(x)
            if xs:
                c = (min(xs) + max(xs) + 1) / 2.0
                pts[i][0] = pts[i + 1][0] = c
    return [[_r(x), _r(y)] for x, y in pts]


def pen_of(segs: list[Segment]) -> float:
    if not segs:
        return 2.0
    pens = sorted(s.pen for s in segs)
    return pens[len(pens) // 2]


@dataclass
class Traced:
    """The wires a picture's ink gives: strokes, T-joins, dotted crossings (junctions) and bare crossings."""
    segments: list[Segment] = field(default_factory=list)
    junctions: list[tuple[float, float]] = field(default_factory=list)
    crossings: list[tuple[float, float]] = field(default_factory=list)
    pen: float = 2.0


def trace(mask: np.ndarray, exclude=(), pins=(), min_len: int | None = None) -> Traced:
    """Every orthogonal wire in the ink outside ``exclude``, ends pulled onto pins and onto each other; every T-join is a
    junction, every crossing with a dot drawn is a junction, every crossing without one is a crossing."""
    segs = merge_collinear(trace_wires(mask, exclude, min_len))
    snap_ends(segs, pins)
    snap_ends(segs, pins)
    segs = [s for s in segs if s.b - s.a >= 2]
    tees, crosses = meets(segs)
    pen = pen_of(segs)
    junctions = list(tees) + [p for p in crosses if has_dot(mask, p[0], p[1], pen)]
    bare = [p for p in crosses if not has_dot(mask, p[0], p[1], pen)]
    return Traced(segs, [(_r(x), _r(y)) for x, y in junctions], [(_r(x), _r(y)) for x, y in bare], pen)
