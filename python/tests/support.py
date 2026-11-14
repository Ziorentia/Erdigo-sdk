"""Shared paths and helpers for the tests."""

from __future__ import annotations

from pathlib import Path

PYTHON_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY = PYTHON_ROOT.parent
VECTORS = REPOSITORY / "spec" / "vectors"
