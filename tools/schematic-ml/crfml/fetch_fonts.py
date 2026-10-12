"""Open-licensed fonts for generated text (brief-iml-3 R-iml3-5).

    python -m crfml.fetch_fonts [--data DIR]

Downloads a pinned list of open-licensed families into ``$CRF_ML_DATA/fonts`` with their licence files, verifying every
download by SHA-256 before it is unpacked or kept, and writes ``fonts/index.json``: each face's file and the styles it
answers to (``sans``, ``serif-italic``, ``mono-bold``, ``condensed``, ``ui``…). ``tools/SchematicSynth``'s FontBook reads
that index; a style pack names styles, never a family. Nothing here is ever committed (L4): the fonts live in the data
home, and circuitRF's own shipped faces are always available besides them.

The families: serif, mono and condensed cuts of the DejaVu family circuitRF already ships the sans of; a metric-compatible
sans/serif/mono set (Liberation), whose shapes stand in for the faces common on desktops and in documents; Fira (sans,
condensed, mono), Crimson (a book serif) and Barlow Condensed (a narrow UI face).
All are under the SIL Open Font License or, for Liberation, the same licence; each folder keeps its licence text.

Run it again any time: a file already present with the right hash is kept, a wrong one is fetched again.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import sys
import shutil
import subprocess
import tarfile
import urllib.error
import urllib.request
from pathlib import Path

from .data import DataHomeError, resolve_data_home

# Each single-file family from its own project's repository, at a pinned commit: the same bytes on every machine.
_FIRA = "https://raw.githubusercontent.com/bBoxType/FiraSans/f54eeb3124c63fe9b5bcd36d09d1cd46788cd15e/"
_BARLOW = "https://raw.githubusercontent.com/jpt/barlow/dc2940e2e04ef4ec96c07e23e0f02aefbddd343b/"
_CRIMSON = "https://raw.githubusercontent.com/skosch/Crimson/685b5a2478a7213a806e9bbf1e36f6a9e7f5c7ef/"
_FIRA_TTF = _FIRA + "Fira_Sans_4_3/Fonts/Fira_Sans_TTF_4301/"

ARCHIVES = [
    {
        "family": "dejavu",
        "url": "https://github.com/dejavu-fonts/dejavu-fonts/releases/download/version_2_37/dejavu-fonts-ttf-2.37.tar.bz2",
        "sha256": "fa9ca4d13871dd122f61258a80d01751d603b4d3ee14095d65453b4e846e17d7",
        "licence": "dejavu-fonts-ttf-2.37/LICENSE",
        "faces": {
            "dejavu-fonts-ttf-2.37/ttf/DejaVuSerif.ttf": ["serif"],
            "dejavu-fonts-ttf-2.37/ttf/DejaVuSerif-Bold.ttf": ["serif-bold"],
            "dejavu-fonts-ttf-2.37/ttf/DejaVuSerif-Italic.ttf": ["serif-italic"],
            "dejavu-fonts-ttf-2.37/ttf/DejaVuSerif-BoldItalic.ttf": ["serif-bold-italic"],
            "dejavu-fonts-ttf-2.37/ttf/DejaVuSansMono.ttf": ["mono"],
            "dejavu-fonts-ttf-2.37/ttf/DejaVuSansMono-Bold.ttf": ["mono-bold"],
            "dejavu-fonts-ttf-2.37/ttf/DejaVuSansCondensed.ttf": ["condensed"],
            "dejavu-fonts-ttf-2.37/ttf/DejaVuSansCondensed-Bold.ttf": ["condensed-bold"],
            "dejavu-fonts-ttf-2.37/ttf/DejaVuSansCondensed-Oblique.ttf": ["condensed-oblique", "condensed-italic"],
        },
    },
    {
        "family": "liberation",
        "url": "https://github.com/liberationfonts/liberation-fonts/files/7261482/liberation-fonts-ttf-2.1.5.tar.gz",
        "sha256": "7191c669bf38899f73a2094ed00f7b800553364f90e2637010a69c0e268f25d0",
        "licence": "liberation-fonts-ttf-2.1.5/LICENSE",
        "faces": {
            "liberation-fonts-ttf-2.1.5/LiberationSans-Regular.ttf": ["sans", "ui"],
            "liberation-fonts-ttf-2.1.5/LiberationSans-Bold.ttf": ["sans-bold", "ui-bold"],
            "liberation-fonts-ttf-2.1.5/LiberationSans-Italic.ttf": ["sans-oblique", "sans-italic"],
            "liberation-fonts-ttf-2.1.5/LiberationSerif-Regular.ttf": ["serif"],
            "liberation-fonts-ttf-2.1.5/LiberationSerif-Bold.ttf": ["serif-bold"],
            "liberation-fonts-ttf-2.1.5/LiberationSerif-Italic.ttf": ["serif-italic"],
            "liberation-fonts-ttf-2.1.5/LiberationMono-Regular.ttf": ["mono"],
            "liberation-fonts-ttf-2.1.5/LiberationMono-Bold.ttf": ["mono-bold"],
        },
    },
]

# Single files: the folder they go in, url, sha256, styles (empty for a licence file).
FILES = [
    ("fira", _FIRA_TTF + "Normal/Roman/FiraSans-Regular.ttf", "43c240a110cf888686d13cd17400fd0ca376ce55656150e52abff95f67a9532e", ["sans", "ui"]),
    ("fira", _FIRA_TTF + "Normal/Roman/FiraSans-Bold.ttf", "2c41b665c7e1c20a0fb3fce3aa09b464b7c9c253db965979c45a6e8ee6a41546", ["sans-bold", "ui-bold"]),
    ("fira", _FIRA_TTF + "Normal/Italic/FiraSans-Italic.ttf", "5d55f0530d51d071e498acbefed420fe2c30c1d16c68a1ab86bcd88206ad688f", ["sans-oblique", "sans-italic"]),
    ("fira", _FIRA_TTF + "Condensed/Roman/FiraSansCondensed-Regular.ttf", "2135a07448a9a45f471262db1a6470c03ee78dac00eb83f60d8cb1d8bf9e7e44", ["condensed"]),
    ("fira", _FIRA_TTF + "Condensed/Roman/FiraSansCondensed-Bold.ttf", "0c3ad16d3ec6b5673271cee4696657f9a1d4adb74f3016c10d44c1035c7d5320", ["condensed-bold"]),
    ("fira", _FIRA_TTF + "Condensed/Italic/FiraSansCondensed-Italic.ttf", "d40d2adb7d64f8b9adcefe30d3d30bf784baa0075b9a11c224dd1b773364b756", ["condensed-oblique", "condensed-italic"]),
    ("fira", _FIRA + "Fira_Mono_3_2/Fonts/FiraMono_WEB_32/FiraMono-Regular.ttf", "8c86f2963208a353c3435e28ecb38a99ab68f14d7433ba00c1822cef9a9c1b44", ["mono"]),
    ("fira", _FIRA + "Fira_Mono_3_2/Fonts/FiraMono_WEB_32/FiraMono-Bold.ttf", "2a28efd740e8da1da75d40ac79f0db4f60a0f1aead0b15ca16b4694a11b45fc6", ["mono-bold"]),
    ("fira", _FIRA + "OFL.txt", "5c29650250730778eccb5475b112d32a8e0c9dd1860d9509693329652bf8e9eb", []),
    ("crimson", _CRIMSON + "Desktop%20Fonts/TTF/Crimson-Roman.ttf", "8d9166677acd315cddbebe9f013ad89f212d66fb1438c2507da18f2e9416c5e6", ["serif"]),
    ("crimson", _CRIMSON + "Desktop%20Fonts/TTF/Crimson-Italic.ttf", "5484d0b6cce50d6eafa5508a43a6f55a3b98e3d385fd42156d7814722ec87b91", ["serif-italic"]),
    ("crimson", _CRIMSON + "Desktop%20Fonts/TTF/Crimson-Bold.ttf", "08e67561c98a558e404804a67c3030956139716951e2bf4a1f10e5066ffcf062", ["serif-bold"]),
    ("crimson", _CRIMSON + "LICENSE.txt", "26ed7bb6be478f73d55c2cade75214c186ea28c06a57db75b9e378a16f0e2119", []),
    ("barlow", _BARLOW + "fonts/ttf/BarlowCondensed-Regular.ttf", "b4bb48af7c964a35150de461708162cfbd3a1ec95f6b769b320325ce0972c93c", ["condensed", "ui"]),
    ("barlow", _BARLOW + "fonts/ttf/BarlowCondensed-SemiBold.ttf", "31f11ff391c790a36b977fc01e0880656762ac26f6634a0e6ff4f80c7d4c534f", ["condensed-bold", "ui-bold"]),
    ("barlow", _BARLOW + "OFL.txt", "186d750eb496a4c17a76385f82be6aea2ac1cf2de074a811d63786cf374ea73f", []),
]


class FontFetchError(Exception):
    """A download that could not be verified; the message says which."""


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _download(url: str, sha256: str) -> bytes:
    try:
        with urllib.request.urlopen(url, timeout=120) as r:
            data = r.read()
    except urllib.error.URLError as e:
        # A Python without a CA bundle (common on macOS) cannot verify the server; the system curl can. Integrity does
        # not rest on the transport either way: the bytes are held to their pinned SHA-256 below.
        if "CERTIFICATE_VERIFY_FAILED" not in str(e) or shutil.which("curl") is None:
            raise
        data = subprocess.run(["curl", "-fsSL", "--max-time", "300", url], check=True, capture_output=True).stdout
    got = _sha256(data)
    if got != sha256:
        raise FontFetchError(f"{url}: SHA-256 {got}, expected {sha256} — not kept")
    return data


def _kept(path: Path, sha256: str | None) -> bool:
    return path.is_file() and (sha256 is None or _sha256(path.read_bytes()) == sha256)


def fetch(fonts: Path, log=print) -> list[dict]:
    """Every pinned face into ``fonts``, verified; returns the index's face list."""
    faces: list[dict] = []
    for a in ARCHIVES:
        folder = fonts / a["family"]
        folder.mkdir(parents=True, exist_ok=True)
        wanted = {m: folder / Path(m).name for m in [*a["faces"], a["licence"]]}
        if not all(p.is_file() for p in wanted.values()):
            log(f"[fonts] {a['family']}: downloading {a['url']}")
            data = _download(a["url"], a["sha256"])
            mode = "r:bz2" if a["url"].endswith(".bz2") else "r:gz"
            with tarfile.open(fileobj=io.BytesIO(data), mode=mode) as tar:
                for member, dest in wanted.items():
                    f = tar.extractfile(member)
                    if f is None:
                        raise FontFetchError(f"{a['url']}: no member {member}")
                    dest.write_bytes(f.read())
        for member, styles in a["faces"].items():
            faces.append({"file": f"{a['family']}/{Path(member).name}", "styles": styles})

    for family, url, sha, styles in FILES:
        dest = fonts / family / Path(url).name
        dest.parent.mkdir(parents=True, exist_ok=True)
        if not _kept(dest, sha):
            log(f"[fonts] {family}: downloading {dest.name}")
            dest.write_bytes(_download(url, sha))
        if styles:
            faces.append({"file": f"{family}/{dest.name}", "styles": styles})
    return faces


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m crfml.fetch_fonts", description=__doc__.split("\n\n")[0])
    ap.add_argument("--data", help="the data home (default $CRF_ML_DATA, else ~/circuitRF-ml-data)")
    args = ap.parse_args(argv)
    try:
        home = resolve_data_home(args.data)
        fonts = home / "fonts"
        faces = fetch(fonts)
    except (DataHomeError, FontFetchError, OSError, subprocess.CalledProcessError) as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    index = {"version": 1, "faces": faces}
    tmp = fonts / "index.json.tmp"
    tmp.write_text(json.dumps(index, indent=2) + "\n", encoding="utf-8")
    tmp.replace(fonts / "index.json")
    styles = sorted({s for f in faces for s in f["styles"]})
    print(f"[fonts] {len(faces)} face(s) in {fonts}; styles: {', '.join(styles)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
