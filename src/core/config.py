"""Central configuration and athlete profile utilities.

Provides standardized environment loading, file path constants, and athlete profile access
across CLI scripts, analysis engines, and planner tools.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any, Dict, Optional

PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent

DEFAULT_ATHLETE_PATH = PROJECT_ROOT / "config" / "athlete.json"
DEFAULT_ACTIVITIES_PATH = PROJECT_ROOT / "data" / "intervals_activities.json"
DEFAULT_WELLNESS_PATH = PROJECT_ROOT / "data" / "intervals_wellness.json"
DEFAULT_REVIEWS_PATH = PROJECT_ROOT / "data" / "run_reviews.json"
DEFAULT_HISTORY_PATH = PROJECT_ROOT / "data" / "coach_history.json"


def load_env_file(filepath: Optional[Path] = None) -> None:
    """Loads key-value pairs from a .env file into os.environ if not already present."""
    target_path = filepath or (PROJECT_ROOT / ".env")
    if not target_path.is_file():
        return
    with open(target_path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, val = line.split("=", 1)
            key = key.strip()
            val = val.strip().strip("'\"")
            if key not in os.environ and val:
                os.environ[key] = val


def load_athlete_profile(file_path: Optional[Path] = None) -> Dict[str, Any]:
    """Safely loads athlete profile JSON configuration, returning empty dict on failure."""
    athlete_path = file_path or DEFAULT_ATHLETE_PATH
    if athlete_path.is_file():
        try:
            with open(athlete_path, "r", encoding="utf-8") as f:
                data = json.load(f)
                return data if isinstance(data, dict) else {}
        except Exception:
            return {}
    return {}
