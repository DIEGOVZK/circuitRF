"""The annotation page's server (brief-iml-4 R-iml4-2).

    python -m crfml.annotate [--data <dir>] [--port 8765] [--open]

Serves one page, ``page.html``, on 127.0.0.1 only, with Python's standard-library HTTP server: no web framework, no
file from the network, nothing reachable from another machine. The page shows a real picture with its label drawn
over it and edits the label; this server reads and writes the files and answers the questions only Python can — the
validator's findings, the ink centreline under a wire, a part's body fitted to its ink.

Pictures are addressed by their manifest id only, never by a path, so the page can reach nothing outside the data
home's ``real/`` folder. Saving writes ``<id>.mllabel.json`` (the previous one kept as ``<id>.mllabel.json.bak``) and
the netlist truth beside it (``crfml.label_to_cnl``).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import tempfile
import threading
import webbrowser
from functools import lru_cache
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from .. import geometry as G
from ..contact_sheet import load_review
from ..data import CLASSES_PATH, DataHomeError, load_classes, load_manifest, resolve_data_home
from ..label_to_cnl import convert, target_for, write as write_cnl
from ..validate import check_label

PAGE = Path(__file__).resolve().parent / "page.html"
LABEL_SUFFIX = ".mllabel.json"
DRAFT_SUFFIX = ".draft.mllabel.json"
_ID = re.compile(r"^[a-z0-9-]+$")


class Store:
    """The data home as the page sees it: pictures by id, their labels and drafts."""

    def __init__(self, home: Path):
        self.home = home
        self.classes = load_classes()
        self.lock = threading.Lock()

    def pictures(self) -> list[dict]:
        manifest = load_manifest(self.home)
        review = load_review(self.home)["pictures"]
        out = []
        for p in manifest["pictures"]:
            folder = self.home / "real" / p["family"]
            state = "label" if (folder / f"{p['id']}{LABEL_SUFFIX}").is_file() else \
                "draft" if (folder / f"{p['id']}{DRAFT_SUFFIX}").is_file() else "none"
            out.append({"id": p["id"], "family": p["family"], "split": p["split"], "state": state,
                        "verdict": review.get(p["id"], {}).get("verdict"), "note": review.get(p["id"], {}).get("note")})
        return out

    def picture(self, pid: str) -> Path | None:
        if not _ID.match(pid):
            return None
        for p in load_manifest(self.home)["pictures"]:
            if p["id"] == pid:
                folder = self.home / "real" / p["family"]
                for ext in (".png", ".jpg"):
                    if (folder / f"{pid}{ext}").is_file():
                        return folder / f"{pid}{ext}"
        return None

    def label(self, pid: str) -> dict | None:
        pic = self.picture(pid)
        if pic is None:
            return None
        for suffix, source in ((LABEL_SUFFIX, "label"), (DRAFT_SUFFIX, "draft")):
            p = pic.parent / f"{pid}{suffix}"
            if p.is_file():
                return {"source": source, "label": json.loads(p.read_text(encoding="utf-8"))}
        w, h = _size(str(pic))
        return {"source": "new", "label": {
            "version": 1, "image": pic.name, "width": w, "height": h, "origin": "real", "family": pic.parent.name,
            "style": None, "parts": [], "wires": [], "junctions": [], "crossings": [], "texts": [], "annotations": [],
            "ignore": []}}

    def findings(self, pid: str, label: dict) -> list[dict]:
        """The validator's findings on a label that is not on disk yet: checked beside a link to its picture."""
        pic = self.picture(pid)
        with tempfile.TemporaryDirectory() as td:
            link = Path(td) / pic.name
            try:
                os.symlink(pic, link)
            except OSError:
                link.write_bytes(pic.read_bytes())
            lp = Path(td) / f"{pid}{LABEL_SUFFIX}"
            lp.write_text(json.dumps(label, ensure_ascii=False), encoding="utf-8")
            return [{"severity": f.severity, "message": f.message} for f in check_label(lp, self.classes)]

    def save(self, pid: str, label: dict) -> dict:
        pic = self.picture(pid)
        lp = pic.parent / f"{pid}{LABEL_SUFFIX}"
        with self.lock:
            if lp.is_file():
                (pic.parent / f"{pid}{LABEL_SUFFIX}.bak").write_bytes(lp.read_bytes())
            tmp = lp.with_suffix(".json.tmp")
            tmp.write_text(json.dumps(label, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
            tmp.replace(lp)
            cnl = write_cnl(lp, self.classes)
        return {"saved": lp.name, "netlist": cnl.name, "findings": [{"severity": f.severity, "message": f.message}
                                                                    for f in check_label(lp, self.classes)]}


@lru_cache(maxsize=64)
def _size(path: str) -> tuple[int, int]:
    from PIL import Image
    with Image.open(path) as im:
        return im.size


@lru_cache(maxsize=4)
def _mask(path: str):
    return G.picture_ink(Path(path))


def make_handler(store: Store):
    class Handler(BaseHTTPRequestHandler):
        server_version = "crfml-annotate"

        def log_message(self, fmt, *args):  # quiet: one line per save is enough
            pass

        def _send(self, code: int, body: bytes, ctype: str) -> None:
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def _json(self, obj, code: int = 200) -> None:
            self._send(code, json.dumps(obj, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8")

        def _body(self) -> dict:
            n = int(self.headers.get("Content-Length", "0"))
            return json.loads(self.rfile.read(n).decode("utf-8")) if n else {}

        def do_GET(self):
            path = self.path.split("?", 1)[0]
            if path == "/":
                return self._send(200, PAGE.read_bytes(), "text/html; charset=utf-8")
            if path == "/api/pictures":
                return self._json(store.pictures())
            if path == "/api/classes":
                return self._send(200, CLASSES_PATH.read_bytes(), "application/json; charset=utf-8")
            m = re.fullmatch(r"/api/(label|image|netlist)/([a-z0-9-]+)", path)
            if m:
                kind, pid = m.groups()
                pic = store.picture(pid)
                if pic is None:
                    return self._json({"error": f"no picture {pid}"}, 404)
                if kind == "image":
                    ctype = "image/png" if pic.suffix == ".png" else "image/jpeg"
                    return self._send(200, pic.read_bytes(), ctype)
                if kind == "netlist":
                    lp = pic.parent / f"{pid}{LABEL_SUFFIX}"
                    text = target_for(lp).read_text(encoding="utf-8") if target_for(lp).is_file() else ""
                    return self._send(200, text.encode("utf-8"), "text/plain; charset=utf-8")
                return self._json(store.label(pid))
            self._json({"error": "not found"}, 404)

        def do_POST(self):
            path = self.path.split("?", 1)[0]
            m = re.fullmatch(r"/api/(validate|save|snap|fit|netlist)/([a-z0-9-]+)", path)
            if not m:
                return self._json({"error": "not found"}, 404)
            kind, pid = m.groups()
            pic = store.picture(pid)
            if pic is None:
                return self._json({"error": f"no picture {pid}"}, 404)
            try:
                body = self._body()
                if kind == "validate":
                    return self._json({"findings": store.findings(pid, body["label"])})
                if kind == "netlist":
                    return self._send(200, convert(body["label"], store.classes, pid).encode("utf-8"), "text/plain; charset=utf-8")
                if kind == "save":
                    out = store.save(pid, body["label"])
                    print(f"saved {out['saved']} ({sum(f['severity'] == 'error' for f in out['findings'])} error(s))")
                    return self._json(out)
                if kind == "snap":
                    return self._json({"points": G.centreline(_mask(str(pic)), body["points"], float(body.get("reach", 4)))})
                if kind == "fit":
                    box = G.body_box(_mask(str(pic)), body["box"], body.get("strip", ""), float(body.get("pen", 2)))
                    return self._json({"box": [round(v, 1) for v in box] if box else None})
            except (KeyError, ValueError, TypeError, json.JSONDecodeError) as e:
                return self._json({"error": f"{type(e).__name__}: {e}"}, HTTPStatus.BAD_REQUEST)

    return Handler


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m crfml.annotate", description=__doc__.split("\n\n")[0])
    ap.add_argument("--data", help="the data home (default $CRF_ML_DATA, else ~/circuitRF-ml-data)")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--open", action="store_true", help="open the page in the default browser")
    args = ap.parse_args(argv)
    try:
        home = resolve_data_home(args.data, create=False)
    except DataHomeError as e:
        print(f"refused: {e}", file=sys.stderr)
        return 1
    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(Store(home)))
    url = f"http://127.0.0.1:{args.port}/"
    print(f"annotating {home / 'real'} at {url}  (Ctrl-C to stop)")
    if args.open:
        webbrowser.open(url)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0
