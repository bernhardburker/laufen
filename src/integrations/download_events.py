#!/usr/bin/env python3
"""download_events.py

Downloads planned and completed calendar events (workouts, rest day notes)
from Intervals.icu REST API and persists them locally to 'calendar_events.json'.

Features:
- Fetches calendar events for a customizable date range (default: -7 days to +14 days).
- Resolves API key from environment variable or .env file.
- Distinguishes workouts, rest notes, and paired (completed) activities.
- Uses only Python standard library (no external dependencies required).
"""

from __future__ import annotations

import argparse
import base64
import datetime
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Dict, List, Optional


def load_env_file(filepath: Path) -> None:
    """Loads key-value pairs from a .env file into os.environ if not already present."""
    if not filepath.is_file():
        return
    with open(filepath, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, val = line.split("=", 1)
            key = key.strip()
            val = val.strip().strip("'\"")
            if key not in os.environ and val:
                os.environ[key] = val


def fetch_intervals_events(
    api_key: str,
    athlete_id: str = "0",
    oldest: Optional[str] = None,
    newest: Optional[str] = None,
    api_base_url: str = "https://intervals.icu/api/v1",
    raise_on_error: bool = False,
) -> List[Dict[str, Any]]:
    """Fetches calendar events from the Intervals.icu REST API."""
    today = datetime.date.today()
    if not oldest:
        oldest = (today - datetime.timedelta(days=7)).isoformat()
    if not newest:
        newest = (today + datetime.timedelta(days=14)).isoformat()

    url = f"{api_base_url.rstrip('/')}/athlete/{athlete_id}/events?oldest={oldest}&newest={newest}"

    auth_string = f"API_KEY:{api_key}"
    auth_header = "Basic " + base64.b64encode(auth_string.encode("utf-8")).decode("ascii")

    req = urllib.request.Request(
        url,
        headers={
            "Authorization": auth_header,
            "User-Agent": "Mozilla/5.0 (compatible; LaufenSync/1.0)",
            "Accept": "application/json",
        },
        method="GET",
    )

    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = resp.read().decode("utf-8")
            return json.loads(data)
    except urllib.error.HTTPError as e:
        if raise_on_error:
            raise
        error_msg = e.read().decode("utf-8", errors="replace")
        if e.code in (401, 403):
            sys.exit(f"Error {e.code}: Authentication with Intervals.icu failed. Please verify INTERVALS_API_KEY.")
        sys.exit(f"HTTP Error {e.code} while fetching calendar events: {error_msg}")
    except urllib.error.URLError as e:
        if raise_on_error:
            raise
        sys.exit(f"Network error while connecting to Intervals.icu: {e.reason}")


def save_events_to_file(events: List[Dict[str, Any]], filepath: Path) -> None:
    """Saves calendar events to the target file ensuring parent directories exist."""
    filepath.parent.mkdir(parents=True, exist_ok=True)
    with open(filepath, "w", encoding="utf-8") as f:
        json.dump(events, f, indent=2, ensure_ascii=False)


def print_events_summary(events: List[Dict[str, Any]]) -> None:
    """Prints a terminal summary of fetched calendar events."""
    workouts = [e for e in events if e.get("category") == "WORKOUT"]
    notes = [e for e in events if e.get("category") == "NOTE"]
    completed = [w for w in workouts if w.get("paired_activity_id")]
    planned = [w for w in workouts if not w.get("paired_activity_id")]

    print("\n" + "=" * 60)
    print("  Intervals.icu Kalender-Events Synchronisation")
    print("=" * 60)
    print(f"Gesamtanzahl Events: {len(events)}")
    print(f"• Workouts geplant:   {len(planned)}")
    print(f"• Workouts absolviert: {len(completed)}")
    print(f"• Notizen / Ruhetage: {len(notes)}")
    print("=" * 60 + "\n")


def main() -> None:
    project_root = Path(__file__).resolve().parent.parent.parent
    dotenv_path = os.getenv("DOTENV_PATH")
    load_env_file(Path(dotenv_path) if dotenv_path else project_root / ".env")

    parser = argparse.ArgumentParser(
        description="Download calendar events from Intervals.icu and persist locally as JSON."
    )
    parser.add_argument(
        "--oldest",
        default=None,
        help="Start date in YYYY-MM-DD format (default: 7 days ago)",
    )
    parser.add_argument(
        "--newest",
        default=None,
        help="End date in YYYY-MM-DD format (default: 14 days ahead)",
    )
    parser.add_argument(
        "--output",
        default=str(project_root / "data" / "calendar_events.json"),
        help="Destination path for JSON export (default: data/calendar_events.json)",
    )
    parser.add_argument(
        "--athlete-id",
        default=os.getenv("INTERVALS_ATHLETE_ID", "0"),
        help="Athlete ID on Intervals.icu (default: '0' for authenticated user)",
    )
    parser.add_argument(
        "--api-base",
        default=os.getenv("INTERVALS_API_BASE", "https://intervals.icu/api/v1"),
        help="API Base URL (default: https://intervals.icu/api/v1)",
    )

    args = parser.parse_args()

    api_key = os.getenv("INTERVALS_API_KEY")
    if not api_key:
        sys.exit(
            "Error: INTERVALS_API_KEY is not set in environment or .env file.\n"
            "Please configure your API key to access Intervals.icu."
        )

    output_path = Path(args.output)
    events = fetch_intervals_events(
        api_key=api_key,
        athlete_id=args.athlete_id,
        oldest=args.oldest,
        newest=args.newest,
        api_base_url=args.api_base,
    )

    save_events_to_file(events, output_path)
    print_events_summary(events)
    print(f"Calendar events successfully saved to: {output_path}")


if __name__ == "__main__":
    main()
