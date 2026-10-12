"""Running circuitRF's own command line from the kit (brief-iml-4).

The label tools call the application for what only it knows — reading a picture (`recognize`), checking a netlist
(`check`), drawing one (`netlist --toSchematic`, `render`). ``$CRF_CLI`` names the command when set (a path to an
installed circuitRF, or ``dotnet /path/CircuitRF.Cli.dll``); otherwise the CLI built in this repository is used.
Build it first: ``dotnet build src/Cli``.
"""

from __future__ import annotations

import os
import shlex
import shutil
import subprocess
from pathlib import Path

from .data import repo_root


class CliMissing(Exception):
    """No circuitRF command line could be found; the message says how to get one."""


def command() -> list[str]:
    if os.environ.get("CRF_CLI"):
        return shlex.split(os.environ["CRF_CLI"])
    root = repo_root()
    if root is not None:
        # the newer of the two builds: a Release build left from an old packaging run must not shadow today's Debug one
        built = [dll for config in ("Release", "Debug")
                 if (dll := root / "src" / "Cli" / "bin" / config / "net10.0" / "CircuitRF.Cli.dll").is_file()]
        if built:
            return ["dotnet", str(max(built, key=lambda d: d.stat().st_mtime))]
    exe = shutil.which("circuitRF") or shutil.which("circuitrf")
    if exe:
        return [exe]
    raise CliMissing("no circuitRF command line found: run `dotnet build src/Cli` in the repository, or set CRF_CLI")


def run(args: list[str], timeout: float = 300.0) -> subprocess.CompletedProcess:
    """The CLI run with ``args``; stdout and stderr captured as text, never raised on a non-zero exit."""
    return subprocess.run(command() + [str(a) for a in args], capture_output=True, text=True, timeout=timeout)


def check(path: Path) -> tuple[bool, str]:
    """``circuitrf check <path>``: whether it passed, and what it said."""
    p = run(["check", path])
    return p.returncode == 0, (p.stdout + p.stderr).strip()
