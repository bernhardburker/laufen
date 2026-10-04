#!/usr/bin/env python3
"""Recent running activities tool.

Parses activity data (Intervals.icu format) to display recent individual runs
with metrics including distance, duration, pace, heart rate, and zone distribution.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Display recent running activities and summary metrics."
    )
    parser.add_argument(
        "--input",
        "-i",
        type=Path,
        default=Path(os.getenv("ACTIVITIES_PATH", "data/intervals_activities.json")),
        help="Path to activity JSON file (default: data/intervals_activities.json or ACTIVITIES_PATH)",
    )
    parser.add_argument(
        "--limit",
        "-n",
        type=int,
        default=10,
        help="Number of recent runs to display (default: 10)",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="Output results as JSON instead of formatted text table",
    )
    return parser.parse_args()


def load_activities(file_path: Path) -> List[Dict[str, Any]]:
    if not file_path.exists():
        raise FileNotFoundError(f"Activities file not found: {file_path}")
    with open(file_path, "r", encoding="utf-8") as f:
        data = json.load(f)
    if not isinstance(data, list):
        raise ValueError("Activities JSON must be a list of activity objects.")
    return data


def filter_and_sort_runs(activities: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    runs = []
    for item in activities:
        if not isinstance(item, dict):
            continue
        if item.get("type") != "Run":
            continue
        dist = item.get("distance") or 0.0
        duration = item.get("moving_time") or 0
        if dist > 0 and duration > 0:
            runs.append(item)
    # Sort descending by start date (newest first)
    runs.sort(key=lambda r: r.get("start_date_local") or "", reverse=True)
    return runs


def format_pace(seconds_per_km: float) -> str:
    if not math.isfinite(seconds_per_km) or seconds_per_km <= 0:
        return "-:-- /km"
    m, s = divmod(int(round(seconds_per_km)), 60)
    return f"{m}:{s:02d} /km"


def format_duration(seconds: int) -> str:
    if seconds <= 0:
        return "0s"
    hours, remainder = divmod(seconds, 3600)
    mins, secs = divmod(remainder, 60)
    if hours > 0:
        return f"{hours}h {mins:02d}m {secs:02d}s"
    if mins > 0:
        return f"{mins}m {secs:02d}s"
    return f"{secs}s"


def format_date(iso_date: Optional[str]) -> str:
    if not iso_date:
        return "Unknown"
    try:
        dt = datetime.fromisoformat(iso_date[:19])
        return dt.strftime("%Y-%m-%d %H:%M")
    except Exception:
        return iso_date[:16]


def parse_run_details(run: Dict[str, Any]) -> Dict[str, Any]:
    distance_m = float(run.get("distance") or 0.0)
    moving_time_s = int(run.get("moving_time") or 0)
    distance_km = distance_m / 1000.0

    pace_s_per_km = moving_time_s / distance_km if distance_km > 0 else 0.0
    avg_hr = float(run.get("average_heartrate") or 0.0)
    max_hr = float(run.get("max_heartrate") or 0.0)

    zone_times = run.get("icu_hr_zone_times") or []
    z1_z2_seconds = 0
    total_zone_seconds = 0
    z1_z2_pct = None
    if isinstance(zone_times, list) and len(zone_times) >= 2:
        z1_z2_seconds = sum(zone_times[:2])
        total_zone_seconds = sum(zone_times)
        if total_zone_seconds > 0:
            z1_z2_pct = round((z1_z2_seconds / total_zone_seconds) * 100.0, 1)

    training_load = float(run.get("icu_training_load") or 0.0)

    return {
        "id": run.get("id"),
        "name": run.get("name") or "Run",
        "date": format_date(run.get("start_date_local")),
        "raw_date": run.get("start_date_local"),
        "distance_km": round(distance_km, 2),
        "moving_time_s": moving_time_s,
        "duration": format_duration(moving_time_s),
        "avg_pace": format_pace(pace_s_per_km),
        "avg_hr": round(avg_hr, 1) if avg_hr > 0 else None,
        "max_hr": round(max_hr, 1) if max_hr > 0 else None,
        "training_load": round(training_load, 1),
        "z1_z2_pct": z1_z2_pct,
    }


def render_runs_table(runs: List[Dict[str, Any]]) -> str:
    lines = [
        "=" * 98,
        "                                     RECENT RUNNING ACTIVITIES                                    ",
        "=" * 98,
        f"{'Date & Time':<17} | {'Name':<25} | {'Distance':<9} | {'Duration':<10} | {'Ø Pace':<9} | {'Ø HR':<6} | {'Max HR':<6} | {'Z1-Z2 %':<8} | {'Load':<5}",
        "-" * 17 + "-+-" + "-" * 25 + "-+-" + "-" * 9 + "-+-" + "-" * 10 + "-+-" + "-" * 9 + "-+-" + "-" * 6 + "-+-" + "-" * 6 + "-+-" + "-" * 8 + "-+-" + "-" * 5,
    ]

    for r in runs:
        name = r["name"]
        if len(name) > 25:
            name = name[:22] + "..."
        avg_hr_str = f"{r['avg_hr']:.0f}" if r["avg_hr"] is not None else "-"
        max_hr_str = f"{r['max_hr']:.0f}" if r["max_hr"] is not None else "-"
        z_pct_str = f"{r['z1_z2_pct']:.1f}%" if r["z1_z2_pct"] is not None else "-"

        lines.append(
            f"{r['date']:<17} | "
            f"{name:<25} | "
            f"{r['distance_km']:>6.2f} km | "
            f"{r['duration']:>10} | "
            f"{r['avg_pace']:<9} | "
            f"{avg_hr_str:>6} | "
            f"{max_hr_str:>6} | "
            f"{z_pct_str:>8} | "
            f"{r['training_load']:>5.0f}"
        )

    lines.append("=" * 98)
    return "\n".join(lines)


def main() -> int:
    args = parse_arguments()

    try:
        activities = load_activities(args.input)
    except FileNotFoundError as e:
        print(f"Error: {e}", file=sys.stderr)
        return 1
    except Exception as e:
        print(f"Error loading activities: {e}", file=sys.stderr)
        return 1

    runs = filter_and_sort_runs(activities)
    if not runs:
        if args.json:
            print(json.dumps({"runs": [], "message": "No running activities found"}))
        else:
            print("No running activities found in dataset.")
        return 0

    if args.limit > 0:
        runs = runs[: args.limit]

    parsed_runs = [parse_run_details(r) for r in runs]

    if args.json:
        print(json.dumps({"runs": parsed_runs}, indent=2))
    else:
        print(render_runs_table(parsed_runs))

    return 0


if __name__ == "__main__":
    sys.exit(main())
