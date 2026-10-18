"""Shared helpers for the tools/ci script tests (imported by the test_*.py modules)."""
from __future__ import annotations

import contextlib
import io
import os
import sys
import tempfile
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))


def call(run, argv: list[str], env: dict[str, str] | None = None, **kwargs) -> tuple[int, str]:
    """Runs a script's run(argv) with GITHUB_STEP_SUMMARY unset (unless env sets it); returns (exit code, stdout)."""
    environ = {k: v for k, v in os.environ.items() if k not in ("GITHUB_STEP_SUMMARY", "JOB_START")}
    environ.update(env or {})
    with mock.patch.dict(os.environ, environ, clear=True), contextlib.redirect_stdout(io.StringIO()) as out:
        code = run(argv, **kwargs)
    return code, out.getvalue()


class TempDir:
    def __init__(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.path = Path(self._tmp.name)

    def write(self, name: str, text: str) -> str:
        path = self.path / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return str(path)

    def cleanup(self) -> None:
        self._tmp.cleanup()
