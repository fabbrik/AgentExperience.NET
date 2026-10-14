"""The Python quick start (``samples/quickstart.py``) runs, and its second run is handed the lesson."""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

import pytest

pytest.importorskip("agent_framework")

SAMPLES = Path(__file__).resolve().parents[1] / "samples"
EXPECTED = SAMPLES / "quickstart.expected.txt"
UPDATE_VARIABLE = "AGENTEXPERIENCE_QUICKSTART_GOLDEN_UPDATE"


def test_the_quick_start_hands_the_second_run_the_lesson_naming_both_calls() -> None:
    environment = {
        key: value for key, value in os.environ.items() if key not in ("DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT")
    }
    completed = subprocess.run(
        [sys.executable, str(SAMPLES / "quickstart.py")],
        capture_output=True,
        check=False,
        env=environment,
        timeout=120,
    )
    output = completed.stdout.decode("utf-8")
    assert completed.returncode == 0, completed.stderr.decode("utf-8", "replace")

    assert "Run 1 (no memory yet)" in output
    assert "Run 2 (lesson injected)" in output
    assert "  - attempt 0: run_tests [failed: exit 3], run_tests [returned] → completed" in output

    if os.environ.get(UPDATE_VARIABLE) == "1":
        EXPECTED.write_text(output, encoding="utf-8")
    assert output == EXPECTED.read_text(encoding="utf-8"), f"set {UPDATE_VARIABLE}=1 to rewrite it on purpose"
