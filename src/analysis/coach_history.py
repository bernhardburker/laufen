#!/usr/bin/env python3
"""Coach Memory and Adaptive Feedback Loop.

Persists structured coaching recommendations over time, tracks compliance
of subsequent runs against past advice, detects training adaptations
(e.g., unplanned tempo/interval sessions triggering recovery guidance),
and calculates multi-week aerobic efficiency trends.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import sys
from typing import Any, Dict, List, Optional

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.analysis.trends import calculate_run_metrics, filter_runs, format_duration, format_pace, load_activities


def load_coach_history(history_file: Path) -> List[Dict[str, Any]]:
    """Loads coaching history entries from a JSON file.

    Returns an empty list if the file does not exist or contains invalid JSON.
    """
    if not history_file.is_file():
        return []
    try:
        with open(history_file, "r", encoding="utf-8") as f:
            data = json.load(f)
            if isinstance(data, list):
                return data
    except Exception:
        return []
    return []


def save_coach_history(history: List[Dict[str, Any]], history_file: Path) -> None:
    """Saves coaching history entries to a JSON file."""
    history_file.parent.mkdir(parents=True, exist_ok=True)
    with open(history_file, "w", encoding="utf-8") as f:
        json.dump(history, f, ensure_ascii=False, indent=2)


def get_open_recommendation(history: List[Dict[str, Any]]) -> Optional[Dict[str, Any]]:
    """Finds the most recent open/active recommendation awaiting execution."""
    for entry in reversed(history):
        if entry.get("status") == "offen":
            return entry
    return None


def get_evaluated_run_ids(history: List[Dict[str, Any]]) -> set:
    """Returns set of all run IDs that have already been evaluated in past coaching cycles."""
    evaluated = set()
    for entry in history:
        for rid in entry.get("evaluated_run_ids", []):
            evaluated.add(str(rid))
    return evaluated


def find_runs_since(
    runs: List[Dict[str, Any]],
    since_date_str: str,
    exclude_run_ids: Optional[Any] = None,
) -> List[Dict[str, Any]]:
    """Filters running activities completed on or after a given ISO date string (YYYY-MM-DD),
    excluding runs that were already evaluated in previous coaching cycles.
    """
    target_prefix = since_date_str[:10]
    matched: List[Dict[str, Any]] = []
    excluded = set(str(x) for x in exclude_run_ids) if exclude_run_ids else set()

    for r in runs:
        r_id = str(r.get("id"))
        if r_id in excluded:
            continue
        date_raw = str(r.get("start_date_local") or r.get("date") or "")[:10]
        if date_raw and date_raw >= target_prefix:
            matched.append(r)
    matched.sort(
        key=lambda r: str(r.get("start_date_local") or r.get("date") or "")
    )
    return matched


def calculate_multiweek_efficiency(
    runs: List[Dict[str, Any]], weeks: int = 4
) -> Dict[str, Any]:
    """Computes aerobic efficiency (speed [m/s] per bpm) across runs in recent weeks.

    Higher values indicate greater aerobic economy (more meters covered per heartbeat).
    """
    if not runs:
        return {"avg_ef": 0.0, "runs_count": 0, "summary": "Keine Läufe im Zeitraum vorhanden"}

    ef_values: List[float] = []
    for r in runs:
        dist_m = float(r.get("distance_km", 0.0) * 1000.0 if "distance_km" in r else r.get("distance", 0.0))
        moving_s = float(r.get("moving_time_s", 0.0) if "moving_time_s" in r else r.get("moving_time", 0.0))
        hr = float(r.get("avg_hr", 0.0) if "avg_hr" in r else r.get("average_heartrate", 0.0))

        if dist_m > 0 and moving_s > 0 and hr > 0:
            speed_ms = dist_m / moving_s
            ef = speed_ms / hr
            ef_values.append(ef)

    if not ef_values:
        return {"avg_ef": 0.0, "runs_count": 0, "summary": "Unzureichende Herzfrequenzdaten"}

    avg_ef = sum(ef_values) / len(ef_values)
    return {
        "avg_ef": round(avg_ef, 4),
        "runs_count": len(ef_values),
        "summary": f"Ø {avg_ef * 1000.0:.2f} m/s pro 1000 bpm ({len(ef_values)} Einheiten)",
    }


def initialize_cold_start_history(
    activities: List[Dict[str, Any]],
    athlete_profile: Dict[str, Any],
    history_file: Optional[Path] = None,
) -> List[Dict[str, Any]]:
    """Creates a baseline cold-start history entry if history is empty."""
    runs = filter_runs(activities)
    athlete_info = athlete_profile.get("athlete", {})
    z2_max = athlete_info.get("heart_rate", {}).get("zones", {}).get("z2_aerobic", {}).get("max", 148)

    processed_runs = [calculate_run_metrics(r) for r in runs]
    processed_runs.sort(key=lambda r: str(r.get("date") or ""), reverse=True)

    latest_date = (
        str(processed_runs[0].get("date", ""))[:10]
        if processed_runs
        else datetime.now(timezone.utc).strftime("%Y-%m-%d")
    )

    total_z1_z2 = sum(r.get("z1_z2_seconds", 0) for r in processed_runs)
    total_zone_sec = sum(r.get("total_zone_seconds", 0) for r in processed_runs)
    z_pct = round((total_z1_z2 / total_zone_sec) * 100.0, 1) if total_zone_sec > 0 else 0.0

    actual_text = (
        f"Ausgangsbasis: Nur {z_pct}% in Zone 1/2. Jüngste Läufe fanden überwiegend an der Schwelle statt."
        if z_pct < 50.0
        else f"Ausgangsbasis: Solide {z_pct}% in Zone 1/2 registriert."
    )

    baseline_entry = {
        "id": "cycle-001",
        "date": latest_date,
        "recommendation": f"Grundlagenläufe strikt unter {z2_max} bpm steuern (Zone 2). Pace ignorieren, bei Steigungen gehen.",
        "target_type": "easy_run",
        "target_hr_max": z2_max,
        "status": "offen",
        "actual": actual_text,
        "effect": "Initialer Startpunkt (Basisvermessung). Noch keine Effekte aufgezeichnet.",
    }

    history = [baseline_entry]
    if history_file:
        save_coach_history(history, history_file)
    return history


def update_history_with_coach_review(
    history: List[Dict[str, Any]],
    review_data: Dict[str, Any],
    runs_since: List[Dict[str, Any]],
    athlete_profile: Optional[Dict[str, Any]] = None,
    history_file: Optional[Path] = None,
) -> List[Dict[str, Any]]:
    """Updates the open history entry with compliance and effect, then appends the next cycle."""
    open_entry = get_open_recommendation(history)
    today_str = datetime.now(timezone.utc).strftime("%Y-%m-%d")

    # If an open recommendation exists, close it with the evaluation
    if open_entry:
        status_val = review_data.get("compliance_status", "erfüllt")
        open_entry["status"] = status_val
        if review_data.get("actual_summary"):
            open_entry["actual"] = review_data["actual_summary"]
        elif runs_since:
            # Deterministic fallback summary if actual_summary is empty
            run_summaries = []
            for r in runs_since:
                m = calculate_run_metrics(r)
                date_s = str(m.get("date", ""))[:10]
                run_summaries.append(f"{date_s}: {m.get('distance_km', 0):.1f} km (Ø {int(round(m.get('avg_hr', 0)))} bpm)")
            open_entry["actual"] = f"{len(runs_since)} Läufe absolviert ({', '.join(run_summaries)})"

        if runs_since:
            open_entry["evaluated_run_ids"] = [str(r.get("id")) for r in runs_since]

        if review_data.get("effect_analysis"):
            open_entry["effect"] = review_data["effect_analysis"]
        else:
            eff_info = calculate_multiweek_efficiency(runs_since)
            open_entry["effect"] = eff_info.get("summary", "Keine Auffälligkeiten.")

    # Create next recommendation entry if provided
    next_rec = review_data.get("next_recommendation")
    if next_rec:
        new_cycle_id = f"cycle-{len(history) + 1:03d}"
        new_entry = {
            "id": new_cycle_id,
            "date": today_str,
            "recommendation": next_rec,
            "target_type": review_data.get("next_workout_type", "easy_run"),
            "target_hr_max": review_data.get("next_target_hr", 148),
            "status": "offen",
            "actual": "Ausstehend (wird nach den nächsten Läufen analysiert)",
            "effect": "-",
        }
        history.append(new_entry)

    if history_file:
        save_coach_history(history, history_file)

    return history


def main() -> int:
    parser = argparse.ArgumentParser(description="Manage Coach History & Adaptive Feedback Loop.")
    parser.add_argument(
        "--history",
        type=Path,
        default=PROJECT_ROOT / "data" / "coach_history.json",
        help="Path to coach history JSON",
    )
    parser.add_argument(
        "--input",
        "-i",
        type=Path,
        default=PROJECT_ROOT / "data" / "intervals_activities.json",
        help="Path to activities JSON",
    )
    parser.add_argument(
        "--athlete",
        "-a",
        type=Path,
        default=PROJECT_ROOT / "config" / "athlete.json",
        help="Path to athlete profile JSON",
    )
    parser.add_argument(
        "--init-cold-start",
        action="store_true",
        help="Initialize coach history if currently missing or empty",
    )
    args = parser.parse_args()

    history = load_coach_history(args.history)

    if args.init_cold_start or not history:
        if args.input.is_file():
            activities = load_activities(args.input)
            profile = {}
            if args.athlete.is_file():
                try:
                    profile = json.loads(args.athlete.read_text(encoding="utf-8"))
                except Exception:
                    profile = {}
            history = initialize_cold_start_history(activities, profile, args.history)
            print(f"Initialized cold-start coach history with {len(history)} entries at {args.history}")
            return 0

    print(f"Coach history contains {len(history)} entries:")
    for entry in history:
        print(f"- [{entry.get('date')}] {entry.get('status', 'offen').upper()}: {entry.get('recommendation')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
