"""The value grammar, ported from circuitRF's reader (brief-iml-1 R-iml1-5).

A value word is read exactly as ValueGrammar reads it in C#: through the bill-of-materials reader's unit rules
(BomTablePaste.TryReadValue / TryReadValueOfAnyKind), a parameter-named value (R=17.4Ω), or a set of line parameters
(Z=50Ω,E=90°). The tables come from units.json, which tests/Ui.Tests/Vision/UnitsTableTests holds to the C# side, so
a label the validator accepts is a label circuitRF's own reader would read the same way.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path

UNITS_PATH = Path(__file__).resolve().parent.parent / "units.json"

_NUMBER = re.compile(r"^[+]?(\d+(\.\d*)?|\.\d+)$")
_SIGNED_NUMBER = re.compile(r"^[+-]?(\d+(\.\d*)?|\.\d+)$")
_NUMBER_WITH_TAIL = re.compile(r"^([+]?(?:\d+(?:\.\d*)?|\.\d+))(\S+)$")

RESISTOR, CAPACITOR, INDUCTOR = "Resistor", "Capacitor", "Inductor"


def _load(path: Path = UNITS_PATH) -> dict:
    with open(path, encoding="utf-8") as f:
        return json.load(f)


_T = _load()
_LETTER_AS_POINT = re.compile(r"^(\d+)([" + re.escape(_T["letterAsPoint"]["letters"]) + r"])(\d+)$")


@dataclass(frozen=True)
class Value:
    si: float
    dims: tuple[str, ...]
    not_fitted: bool = False


@dataclass(frozen=True)
class LineParameter:
    key: str
    si: float
    dim: str


def parse_unit(s: str) -> tuple[str, str] | None:
    """A unit on its own → (dimension, prefix). A bare R is not a unit here (BomTablePaste.ParseUnit)."""
    t = s.strip().rstrip(".")
    if not t:
        return None
    lower = t.lower()
    for ohm in _T["ohmWords"]:
        if not lower.endswith(ohm.lower()):
            continue
        p = t[: len(t) - len(ohm)]
        if p in _T["ohmPrefixes"]:
            return ("Resistance", _T["ohmPrefixes"][p])
        return None
    last = t[-1].upper()
    if last not in ("F", "H"):
        return None
    pre = t[:-1]
    dim = "Capacitance" if last == "F" else "Inductance"
    if pre == "f" and _T["femtoOnlyFarads"] and last != "F":
        return None
    prefix = _T["faradHenryPrefixes"].get(pre)
    return None if prefix is None else (dim, prefix)


def _scale(prefix: str) -> float:
    return _T["prefixScale"].get(prefix, 1.0)


def _try_parse(cell: str, kind: str | None) -> tuple[float, str] | None:
    """BomTablePaste.TryParseValue: a value with its unit in one cell → (number × prefix, dimension)."""
    t = cell.strip().rstrip(",;").strip()
    if not t:
        return None
    parts = t.split()
    if len(parts) == 2 and _NUMBER.match(parts[0]) and (u := parse_unit(parts[1])):
        return float(parts[0]) * _scale(u[1]), u[0]
    if len(parts) != 1:
        return None

    if m := _NUMBER_WITH_TAIL.match(t):
        number, tail = float(m.group(1)), m.group(2)
        if u := parse_unit(tail):
            return number * _scale(u[1]), u[0]
        if kind == RESISTOR and (u := parse_unit(tail + "Ω")):
            return number * _scale(u[1]), u[0]
        if tail in ("R", "r") and kind in (None, RESISTOR):
            return number, "Resistance"
        if kind in (CAPACITOR, INDUCTOR) and len(tail) == 1 and \
                (u := parse_unit(tail + ("F" if kind == CAPACITOR else "H"))):
            return number * _scale(u[1]), u[0]

    if m := _LETTER_AS_POINT.match(t):
        number = float(f"{m.group(1)}.{m.group(3)}")
        letter = m.group(2)
        lap = _T["letterAsPoint"]
        if kind == RESISTOR or (kind is None and letter in lap["unitlessResistorLetters"]):
            r = lap["resistorLetters"].get(letter)
            return None if r is None else (number * _scale(r), "Resistance")
        if kind in (CAPACITOR, INDUCTOR) and letter not in lap["unitlessResistorLetters"]:
            if u := parse_unit(letter + ("F" if kind == CAPACITOR else "H")):
                return number * _scale(u[1]), u[0]
    return None


def _letters_only(s: str) -> str:
    return "".join(ch.lower() for ch in s if ch.isalpha())


def is_not_fitted(s: str) -> bool:
    """BomTablePaste.IsDnpWord: DNP, NC, a dash-only cell — never a cell holding a digit."""
    t = s.strip()
    if not t:
        return False
    if all(ch in "-–—" for ch in t):
        return True
    if any(ch.isdigit() for ch in t):
        return False
    pieces = [p for p in re.split(r"[/,;]", t) if p]
    return bool(pieces) and all(_letters_only(p) in _T["notFittedWords"] for p in pieces)


def read_value(text: str) -> Value | None:
    """ValueGrammar.ReadValue: a value in any spelling, or a not-fitted marker."""
    if is_not_fitted(text) and not any(ch in "-–—" for ch in text):
        return Value(0.0, (), True)
    if (r := _try_parse(text, None)) is not None:
        return Value(r[0], (r[1],))
    found: list[str] = []
    first = 0.0
    for kind in (RESISTOR, CAPACITOR, INDUCTOR):
        if (r := _try_parse(text, kind)) is not None:
            if not found:
                first = r[0]
            found.append(r[1])
    return Value(first, tuple(found)) if found else None


def read_named_value(text: str) -> Value | None:
    """ValueGrammar.ReadNamedValue: R=17.4Ω, L=5nH, C=0.5pF."""
    if text.find("=") != 1 or len(text) < 3:
        return None
    dim = {"R": "Resistance", "L": "Inductance", "C": "Capacitance"}.get(text[0])
    v = read_value(text[2:])
    if dim is None or v is None or v.not_fitted or dim not in v.dims:
        return None
    return Value(v.si, (dim,))


def _plain(s: str) -> float | None:
    return float(s) if _SIGNED_NUMBER.match(s) else None


def _scaled(key: str, v: str, units: list, dim: str) -> LineParameter | None:
    for unit, scale in units:
        if v.endswith(unit) and (n := _plain(v[: len(v) - len(unit)].rstrip())) is not None:
            return LineParameter(key, n * scale, dim)
    return None


def _impedance(key: str, v: str) -> LineParameter | None:
    if (n := _plain(v)) is not None:
        return LineParameter(key, n, "Resistance")
    r = _try_parse(v, None)
    return LineParameter(key, r[0], "Resistance") if r and r[1] == "Resistance" and r[0] < 1e6 else None


def _angle(key: str, v: str) -> LineParameter | None:
    t = v[:-1] if v.endswith("°") else v[:-3] if v.endswith("deg") else v
    n = _plain(t)
    return None if n is None else LineParameter(key, n, "Angle")


def read_line_parameters(text: str) -> list[LineParameter] | None:
    """ValueGrammar.ReadLineParameters: Z=50Ω, E=90°, L=3.2mm, W=0.3mm, F=2GHz — every piece one, each key once."""
    pieces = [p for p in re.split(r"[, ;]", text) if p]
    if not pieces:
        return None
    result: list[LineParameter] = []
    for piece in pieces:
        eq = piece.find("=")
        if eq <= 0 or eq == len(piece) - 1:
            return None
        key, value = piece[:eq], piece[eq + 1:]
        if any(p.key == key for p in result):
            return None
        if key in ("Z", "Z0"):
            p = _impedance(key, value)
        elif key in ("E", "θ"):
            p = _angle(key, value)
        elif key in ("L", "W"):
            p = _scaled(key, value, _T["lineLengths"], "Length")
        elif key == "F":
            p = _scaled(key, value, _T["lineFrequencies"], "Frequency")
        else:
            p = None
        if p is None:
            return None
        result.append(p)
    return result


def parse_value_word(text: str) -> Value | list[LineParameter] | None:
    """What a label's value text may be, in ValueGrammar.Parse's order: a value, a line set, a named value."""
    t = text.strip()
    if not t:
        return None
    return read_value(t) or read_line_parameters(t) or read_named_value(t)


_AROUND_EQUALS = re.compile(r"\s*=\s*")
_BEFORE_UNIT = re.compile(r"(?<=\d)\s+(?=[A-Za-zΩµ°])")


def read_value_text(text: str) -> Value | list[LineParameter] | None:
    """A label's value text as circuitRF draws one — `R = 50 Ohm`, `Z = 50 Ω` — read under the grammar: as drawn, then
    with the spaces round its `=` closed up, then with the space before its unit closed up too. The text stays as drawn
    in the label; the reader closes it up the same way before the grammar reads it (docs/design/schematic-vision.md §4).
    """
    t = text.strip()
    closed = _AROUND_EQUALS.sub("=", t)
    for candidate in (t, closed, _BEFORE_UNIT.sub("", closed)):
        if (v := parse_value_word(candidate)) is not None:
            return v
    return None

