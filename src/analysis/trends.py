#!/usr/bin/env python3
"""Running trends and weekly training analysis tool.

Processes activity data (Intervals.icu format) to calculate weekly training volume,
aerobic efficiency (EF), and Zone 1/2 distribution.
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
        description="Analyze running volume, zone distribution, and aerobic efficiency trends."
    )
    parser.add_argument(
        "--input",
        "-i",
        type=Path,
        default=Path(os.getenv("ACTIVITIES_PATH", "data/intervals_activities.json")),
        help="Path to activity JSON file (default: data/intervals_activities.json or ACTIVITIES_PATH)",
    )
    parser.add_argument(
        "--weeks",
        "-w",
        type=int,
        default=12,
        help="Number of recent calendar weeks to analyze (default: 12)",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="Output results as JSON instead of formatted text tables",
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


def filter_runs(activities: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
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
    return runs


def format_pace(seconds_per_km: float) -> str:
    if not math.isfinite(seconds_per_km) or seconds_per_km <= 0:
        return "-:-- /km"
    m, s = divmod(int(round(seconds_per_km)), 60)
    return f"{m}:{s:02d} /km"


def format_duration(seconds: int) -> str:
    if seconds <= 0:
        return "0m"
    hours, remainder = divmod(seconds, 3600)
    mins, _ = divmod(remainder, 60)
    if hours > 0:
        return f"{hours}h {mins:02d}m"
    return f"{mins}m"


def extract_iso_week(date_str: Optional[str]) -> str:
    if not date_str:
        return "Unknown"
    dt = datetime.fromisoformat(date_str[:19])
    year, week, _ = dt.isocalendar()
    return f"{year}-W{week:02d}"


def calculate_run_metrics(run: Dict[str, Any]) -> Dict[str, Any]:
    distance_m = float(run.get("distance") or 0.0)
    moving_time_s = int(run.get("moving_time") or 0)
    distance_km = distance_m / 1000.0

    pace_s_per_km = moving_time_s / distance_km if distance_km > 0 else 0.0
    avg_hr = float(run.get("average_heartrate") or 0.0)

    # Aerobic Efficiency Factor: speed (m/min) / heartrate (bpm)
    ef = None
    if avg_hr > 0 and moving_time_s > 0:
        speed_m_per_min = distance_m / (moving_time_s / 60.0)
        ef = round(speed_m_per_min / avg_hr, 3)

    # Zone 1 and 2 calculation from icu_hr_zone_times
    zone_times = run.get("icu_hr_zone_times") or []
    z1_z2_seconds = 0
    total_zone_seconds = 0
    if isinstance(zone_times, list) and len(zone_times) >= 2:
        z1_z2_seconds = sum(zone_times[:2])
        total_zone_seconds = sum(zone_times)

    training_load = float(run.get("icu_training_load") or 0.0)

    return {
        "id": run.get("id"),
        "name": run.get("name") or "Run",
        "date": run.get("start_date_local"),
        "iso_week": extract_iso_week(run.get("start_date_local")),
        "distance_km": round(distance_km, 2),
        "moving_time_s": moving_time_s,
        "avg_hr": round(avg_hr, 1),
        "pace_s_per_km": pace_s_per_km,
        "training_load": round(training_load, 1),
        "aerobic_ef": ef,
        "z1_z2_seconds": z1_z2_seconds,
        "total_zone_seconds": total_zone_seconds,
    }


def aggregate_weekly_trends(
    runs: List[Dict[str, Any]], max_weeks: int = 12
) -> List[Dict[str, Any]]:
    if not runs:
        return []

    parsed_runs = [calculate_run_metrics(r) for r in runs]
    # Sort runs chronologically
    parsed_runs.sort(key=lambda r: r["date"] or "")

    weeks_dict: Dict[str, List[Dict[str, Any]]] = {}
    for r in parsed_runs:
        week = r["iso_week"]
        weeks_dict.setdefault(week, []).append(r)

    sorted_weeks = sorted(weeks_dict.keys())
    if max_weeks > 0 and len(sorted_weeks) > max_weeks:
        sorted_weeks = sorted_weeks[-max_weeks:]

    trends = []
    for week in sorted_weeks:
        week_runs = weeks_dict[week]
        total_distance = sum(r["distance_km"] for r in week_runs)
        total_time = sum(r["moving_time_s"] for r in week_runs)
        total_load = sum(r["training_load"] for r in week_runs)

        # Weighted avg pace
        avg_pace_s = (total_time / total_distance) if total_distance > 0 else 0.0

        # Weighted avg HR by moving time
        hr_weight_sum = sum(
            r["avg_hr"] * r["moving_time_s"] for r in week_runs if r["avg_hr"] > 0
        )
        hr_time_sum = sum(r["moving_time_s"] for r in week_runs if r["avg_hr"] > 0)
        avg_hr = (hr_weight_sum / hr_time_sum) if hr_time_sum > 0 else 0.0

        # Zone 1 & 2 percentage
        week_z1_z2 = sum(r["z1_z2_seconds"] for r in week_runs)
        week_zone_total = sum(r["total_zone_seconds"] for r in week_runs)
        z1_z2_pct = (
            round((week_z1_z2 / week_zone_total) * 100.0, 1)
            if week_zone_total > 0
            else None
        )

        # Average efficiency factor
        ef_list = [r["aerobic_ef"] for r in week_runs if r["aerobic_ef"] is not None]
        avg_ef = round(sum(ef_list) / len(ef_list), 3) if ef_list else None

        trends.append(
            {
                "iso_week": week,
                "runs_count": len(week_runs),
                "total_distance_km": round(total_distance, 2),
                "total_time_s": total_time,
                "total_load": round(total_load, 1),
                "avg_pace_s": avg_pace_s,
                "avg_pace": format_pace(avg_pace_s),
                "avg_hr": round(avg_hr, 1),
                "z1_z2_pct": z1_z2_pct,
                "aerobic_ef": avg_ef,
            }
        )

    return trends


def render_trends_table(trends: List[Dict[str, Any]]) -> str:
    lines = [
        "=========================================================================================",
        "                                  WEEKLY RUNNING TRENDS                                  ",
        "=========================================================================================",
        f"{'Week':<10} | {'Runs':<4} | {'Distance':<10} | {'Duration':<9} | {'Ø Pace':<10} | {'Ø HR':<7} | {'Z1-Z2 %':<8} | {'EF':<6} | {'Load':<5}",
        "-----------+------+------------+-----------+------------+---------+----------+--------+------",
    ]

    for t in trends:
        z_pct_str = f"{t['z1_z2_pct']:.1f}%" if t["z1_z2_pct"] is not None else "-"
        ef_str = f"{t['aerobic_ef']:.3f}" if t["aerobic_ef"] is not None else "-"
        lines.append(
            f"{t['iso_week']:<10} | "
            f"{t['runs_count']:<4} | "
            f"{t['total_distance_km']:>7.2f} km | "
            f"{format_duration(t['total_time_s']):>9} | "
            f"{t['avg_pace']:<10} | "
            f"{t['avg_hr']:>5.1f}   | "
            f"{z_pct_str:>8} | "
            f"{ef_str:>6} | "
            f"{t['total_load']:>5.0f}"
        )

    lines.append(
        "========================================================================================="
    )
    lines.append(
        "Notes: Z1-Z2 % = Time in Zone 1 + 2 (Target: ~80%). EF = Aerobic Efficiency Factor (m/min / bpm)."
    )
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

    runs = filter_runs(activities)
    if not runs:
        if args.json:
            print(json.dumps({"trends": [], "message": "No running activities found"}))
        else:
            print("No running activities found in dataset.")
        return 0

    trends = aggregate_weekly_trends(runs, max_weeks=args.weeks)

    if args.json:
        print(json.dumps({"trends": trends}, indent=2))
    else:
        print(render_trends_table(trends))

    return 0


if __name__ == "__main__":
    sys.exit(main())
