#!/usr/bin/env python3
"""download_wellness.py

Downloads wellness and recovery metrics (sleep duration, sleep score, HRV, resting HR)
from Intervals.icu via REST API and persists them locally to 'data/intervals_wellness.json'.

Features:
- Fetches wellness data for a customizable date range (default: past 30 days).
- Resolves API key from environment variable or .env file.
- Formats sleep duration from seconds to human-readable hours and minutes (e.g., 7h 45m).
- Prints a clean summary table of recent sleep and health metrics in the terminal.
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


def format_sleep_duration(seconds: Optional[int | float]) -> str:
    """Formats sleep seconds into human-readable hours and minutes (e.g. 7h 35m)."""
    if seconds is None or seconds <= 0:
        return "--"
    total_minutes = int(seconds) // 60
    hours = total_minutes // 60
    minutes = total_minutes % 60
    return f"{hours}h {minutes:02d}m"


def fetch_intervals_wellness(
    api_key: str,
    athlete_id: str = "0",
    oldest: Optional[str] = None,
    newest: Optional[str] = None,
    api_base_url: str = "https://intervals.icu/api/v1",
) -> List[Dict[str, Any]]:
    """Fetches daily wellness records from Intervals.icu REST API."""
    today = datetime.date.today()
    if not newest:
        newest = today.isoformat()
    if not oldest:
        oldest = (today - datetime.timedelta(days=30)).isoformat()

    url = f"{api_base_url.rstrip('/')}/athlete/{athlete_id}/wellness?oldest={oldest}&newest={newest}"

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
            res = json.loads(data)
            if isinstance(res, list):
                return res
            elif isinstance(res, dict):
                return [res]
            return []
    except urllib.error.HTTPError as e:
        error_msg = e.read().decode("utf-8", errors="replace")
        if e.code in (401, 403):
            sys.exit(f"Error {e.code}: Authentication with Intervals.icu failed. Please check INTERVALS_API_KEY.")
        sys.exit(f"HTTP Error {e.code} while fetching wellness data: {error_msg}")
    except urllib.error.URLError as e:
        sys.exit(f"Network error while connecting to Intervals.icu: {e.reason}")


def print_wellness_table(records: List[Dict[str, Any]], limit: int = 14) -> None:
    """Prints a formatted summary table of recent wellness and sleep records."""
    if not records:
        print("\nNo wellness records found for the requested date range.")
        return

    # Sort newest first
    sorted_records = sorted(
        records,
        key=lambda r: str(r.get("id") or r.get("date") or ""),
        reverse=True,
    )

    header = f"{'Date':<12} | {'Sleep':>8} | {'Score':>6} | {'Rest HR':>7} | {'HRV':>6} | {'Body Batt':>9} | {'Notes / State'}"
    print("\n" + "=" * len(header))
    print("  Intervals.icu Wellness & Sleep Summary")
    print("=" * len(header))
    print(header)
    print("-" * len(header))

    sleep_count = 0
    total_sleep_secs = 0.0
    rhr_sum = 0.0
    rhr_count = 0
    hrv_sum = 0.0
    hrv_count = 0

    for rec in sorted_records[:limit]:
        date_str = str(rec.get("id") or rec.get("date") or "--")[:10]
        sleep_secs = rec.get("sleepSecs")
        sleep_str = format_sleep_duration(sleep_secs)

        score = rec.get("sleepScore")
        score_str = str(score) if score is not None else "--"

        rhr = rec.get("restingHR")
        rhr_str = f"{rhr} bpm" if rhr is not None else "--"

        hrv = rec.get("hrv")
        hrv_str = f"{hrv:.0f} ms" if isinstance(hrv, (int, float)) else "--"

        bb = rec.get("bodyBattery")
        bb_str = str(bb) if bb is not None else "--"

        comments = rec.get("comments") or ""
        readiness = rec.get("readiness")
        state_notes = []
        if readiness is not None:
            state_notes.append(f"Readiness: {readiness}")
        if comments:
            state_notes.append(comments[:25])
        note_str = " | ".join(state_notes) if state_notes else ""

        print(f"{date_str:<12} | {sleep_str:>8} | {score_str:>6} | {rhr_str:>7} | {hrv_str:>6} | {bb_str:>9} | {note_str}")

    for rec in sorted_records:
        s_secs = rec.get("sleepSecs")
        if s_secs and s_secs > 0:
            total_sleep_secs += s_secs
            sleep_count += 1
        rhr = rec.get("restingHR")
        if rhr and rhr > 0:
            rhr_sum += rhr
            rhr_count += 1
        hrv = rec.get("hrv")
        if hrv and hrv > 0:
            hrv_sum += hrv
            hrv_count += 1

    print("-" * len(header))
    avg_sleep_str = format_sleep_duration(total_sleep_secs / sleep_count) if sleep_count > 0 else "--"
    avg_rhr_str = f"{rhr_sum / rhr_count:.1f} bpm" if rhr_count > 0 else "--"
    avg_hrv_str = f"{hrv_sum / hrv_count:.1f} ms" if hrv_count > 0 else "--"

    print(
        f"Averages across {len(sorted_records)} days -> "
        f"Avg Sleep: {avg_sleep_str} ({sleep_count}/{len(sorted_records)} logged) | "
        f"Avg RHR: {avg_rhr_str} | "
        f"Avg HRV: {avg_hrv_str}"
    )
    print("=" * len(header) + "\n")


def main() -> None:
    project_root = Path(__file__).resolve().parent.parent.parent
    dotenv_path = os.getenv("DOTENV_PATH")
    load_env_file(Path(dotenv_path) if dotenv_path else project_root / ".env")

    parser = argparse.ArgumentParser(
        description="Download wellness, recovery, and sleep data from Intervals.icu."
    )
    parser.add_argument(
        "--days",
        type=int,
        default=30,
        help="Number of days in the past to fetch (default: 30)",
    )
    parser.add_argument(
        "--oldest",
        default=None,
        help="Earliest date to fetch (YYYY-MM-DD). Overrides --days if set.",
    )
    parser.add_argument(
        "--newest",
        default=None,
        help="Latest date to fetch (YYYY-MM-DD). Defaults to today.",
    )
    parser.add_argument(
        "--output",
        default=str(project_root / "data" / "intervals_wellness.json"),
        help="Target file path for JSON export (default: data/intervals_wellness.json)",
    )
    parser.add_argument(
        "--athlete-id",
        default=os.getenv("INTERVALS_ATHLETE_ID", "0"),
        help="Athlete ID on Intervals.icu (default: '0' for authenticated user)",
    )
    parser.add_argument(
        "--api-base",
        default=os.getenv("INTERVALS_API_BASE", "https://intervals.icu/api/v1"),
        help="Base URL of Intervals.icu API (default: https://intervals.icu/api/v1)",
    )

    args = parser.parse_args()

    api_key = os.getenv("INTERVALS_API_KEY")
    if not api_key:
        sys.exit(
            "Error: INTERVALS_API_KEY is not set in environment or .env file.\n"
            "Please export it in your shell or add it to .env."
        )

    oldest = args.oldest
    newest = args.newest or datetime.date.today().isoformat()
    if not oldest:
        oldest = (datetime.date.today() - datetime.timedelta(days=args.days)).isoformat()

    print(f"Connecting to Intervals.icu (Athlete ID: {args.athlete_id})...")
    print(f"Fetching wellness data from {oldest} to {newest}...")

    records = fetch_intervals_wellness(
        api_key=api_key,
        athlete_id=args.athlete_id,
        oldest=oldest,
        newest=newest,
        api_base_url=args.api_base,
    )

    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(records, f, indent=2, ensure_ascii=False)

    print(f"Successfully downloaded {len(records)} wellness records and saved to:")
    print(f"-> {output_path}")

    print_wellness_table(records)


if __name__ == "__main__":
    main()
