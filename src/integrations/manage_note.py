#!/usr/bin/env python3
"""manage_note.py

CLI and core service to attach subjective notes, RPE, and feel to running activities.
Synchronizes with Intervals.icu API (PUT /api/v1/activity/{id}) and updates local
activity files and 4-stage coach reviews.
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
from pathlib import Path
import sys
from typing import Any, Dict, List, Optional

PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.analysis.coach_history import load_coach_history
from src.analysis.run_reviewer import (
    evaluate_run_deep,
    extract_deep_run_metrics,
    load_athlete_profile,
    load_run_reviews,
    resolve_run_intent,
    save_run_reviews,
)
from src.analysis.trends import filter_runs, load_activities
from src.athletes import resolve_athlete_context
from src.integrations.download_intervals import update_intervals_activity


def load_env_file(filepath: Path) -> None:
    if not filepath.is_file():
        return
    with open(filepath, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            k, v = line.split("=", 1)
            k, v = k.strip(), v.strip().strip("'\"")
            if k not in os.environ and v:
                os.environ[k] = v


def attach_note_to_run(
    note_text: str,
    activities_path: Path,
    reviews_path: Optional[Path] = None,
    activity_id: Optional[str] = None,
    feel: Optional[int] = None,
    perceived_exertion: Optional[int] = None,
    api_key: Optional[str] = None,
    api_base_url: str = "https://intervals.icu/api/v1",
    athlete_profile_path: Optional[Path] = None,
    coach_history_path: Optional[Path] = None,
    dry_run: bool = False,
) -> Dict[str, Any]:
    """Attaches a subjective note to an activity, updates Intervals.icu and recalculates coach review."""
    if not activities_path.is_file():
        return {"status": "error", "message": f"Activities file not found: {activities_path}"}

    all_activities = load_activities(activities_path)
    runs = filter_runs(all_activities)
    if not runs:
        return {"status": "error", "message": "No running activities found to attach note to."}

    target_run: Optional[Dict[str, Any]] = None
    if activity_id:
        clean_id = str(activity_id).strip()
        for r in runs:
            if str(r.get("id")).strip() == clean_id:
                target_run = r
                break
        if not target_run:
            return {"status": "error", "message": f"Activity with id '{activity_id}' not found."}
    else:
        # Default to latest run sorted by start_date_local descending
        target_run = sorted(runs, key=lambda r: str(r.get("start_date_local") or ""), reverse=True)[0]

    act_id = str(target_run.get("id"))
    act_name = str(target_run.get("name") or "Lauf")
    start_date = str(target_run.get("start_date_local") or "")

    # Update local activity representation
    target_run["description"] = note_text
    target_run["athlete_note"] = note_text
    if feel is not None:
        target_run["feel"] = feel
    if perceived_exertion is not None:
        target_run["perceived_exertion"] = perceived_exertion

    # Save activities JSON
    if not dry_run:
        with open(activities_path, "w", encoding="utf-8") as f:
            json.dump(all_activities, f, indent=2, ensure_ascii=False)

    # Sync to Intervals.icu
    intervals_result = None
    if api_key and not dry_run:
        intervals_result = update_intervals_activity(
            activity_id=act_id,
            api_key=api_key,
            description=note_text,
            feel=feel,
            perceived_exertion=perceived_exertion,
            api_base_url=api_base_url,
        )

    # Re-evaluate review
    profile = load_athlete_profile(athlete_profile_path)
    hist = load_coach_history(coach_history_path) if coach_history_path and coach_history_path.is_file() else []
    metrics = extract_deep_run_metrics(target_run, profile)
    intent = resolve_run_intent(target_run, metrics, profile, hist)
    evaluation = evaluate_run_deep(metrics, profile, run_raw=target_run, coach_history=hist, run_intent=intent)

    if reviews_path:
        rev_cache = load_run_reviews(reviews_path)
        rev_cache[act_id] = {
            "id": act_id,
            "date": metrics["date"],
            "name": metrics["name"],
            "distance_km": metrics["distance_km"],
            "moving_time_s": metrics["moving_time_s"],
            "moving_time_str": metrics["moving_time_str"],
            "pace_str": metrics["pace_str"],
            "avg_hr": metrics["avg_hr"],
            "max_hr": metrics["max_hr"],
            "cadence_spm": metrics["cadence_spm"],
            "elevation_gain_m": metrics["elevation_gain_m"],
            "training_load": metrics["training_load"],
            "icu_ctl": metrics.get("icu_ctl"),
            "icu_atl": metrics.get("icu_atl"),
            "tsb_at_run": metrics.get("tsb_at_run"),
            "aerobic_ef": metrics.get("aerobic_ef"),
            "z1_z2_pct": metrics["z1_z2_pct"],
            "zone_breakdown": metrics["zone_breakdown"],
            "intervals": metrics["intervals"],
            "intensity_class": metrics["intensity_class"],
            "athlete_note": note_text,
            "review": evaluation,
            "analyzed_at": datetime.datetime.now().isoformat(),
        }
        if not dry_run:
            save_run_reviews(rev_cache, reviews_path)

    return {
        "status": "ok",
        "activity_id": act_id,
        "activity_name": act_name,
        "date": start_date,
        "note": note_text,
        "feel": feel,
        "perceived_exertion": perceived_exertion,
        "evaluation": evaluation,
        "intervals_result": intervals_result,
    }


def main() -> int:
    load_env_file(PROJECT_ROOT / ".env")
    parser = argparse.ArgumentParser(description="Attach subjective notes and feedback to a running activity.")
    parser.add_argument("note", nargs="?", default="", help="Note text to attach to the activity")
    parser.add_argument("--id", "--activity-id", dest="activity_id", default=None, help="Specific activity ID (defaults to latest run)")
    parser.add_argument("--athlete", "--athlete-slug", dest="athlete_slug", default=None, help="Athlete slug (defaults to default athlete)")
    parser.add_argument("--feel", type=int, choices=[1, 2, 3, 4, 5], default=None, help="Feel score 1-5 (1=Terrible, 5=Great)")
    parser.add_argument("--rpe", type=int, choices=range(1, 11), default=None, help="Perceived Exertion 1-10")
    parser.add_argument("--activities-file", default=None, help="Path to activities JSON file")
    parser.add_argument("--reviews-file", default=None, help="Path to run reviews JSON file")
    parser.add_argument("--athlete-config", default=None, help="Path to athlete config JSON file")
    parser.add_argument("--coach-history", default=None, help="Path to coach history JSON file")
    parser.add_argument("--api-key", default=os.getenv("INTERVALS_API_KEY"), help="Intervals.icu API key")
    parser.add_argument("--api-base", default=os.getenv("INTERVALS_API_BASE", "https://intervals.icu/api/v1"), help="Intervals API base URL")
    parser.add_argument("--dry-run", action="store_true", help="Do not write files or invoke API")

    args = parser.parse_args()
    if not args.note:
        sys.exit("Error: Note text is required (e.g. ./run note 'Wadenkrämpfe nach 1. Intervall').")

    ctx = resolve_athlete_context(athlete_slug=args.athlete_slug, project_root=PROJECT_ROOT)
    act_file = Path(args.activities_file) if args.activities_file else ctx.activities_path
    rev_file = Path(args.reviews_file) if args.reviews_file else ctx.run_reviews_path
    cfg_file = Path(args.athlete_config) if args.athlete_config else ctx.athlete_config_path
    hist_file = Path(args.coach_history) if args.coach_history else ctx.coach_history_path

    res = attach_note_to_run(
        note_text=args.note,
        activities_path=act_file,
        reviews_path=rev_file,
        activity_id=args.activity_id,
        feel=args.feel,
        perceived_exertion=args.rpe,
        api_key=args.api_key,
        api_base_url=args.api_base,
        athlete_profile_path=cfg_file,
        coach_history_path=hist_file,
        dry_run=args.dry_run,
    )

    if res.get("status") != "ok":
        sys.exit(f"Error: {res.get('message')}")

    print("======================================================================")
    print("  Lauf-Notiz erfolgreich gespeichert")
    print("======================================================================")
    print(f"🏃 Lauf:       {res['activity_name']} (ID: {res['activity_id']})")
    print(f"📅 Datum:      {res['date']}")
    print(f"📝 Notiz:      {res['note']}")
    if res.get("feel"):
        print(f"🙂 Feel:       {res['feel']}/5")
    if res.get("perceived_exertion"):
        print(f"⚡ RPE:        {res['perceived_exertion']}/10")

    eval_data = res.get("evaluation") or {}
    st4 = (eval_data.get("stages") or {}).get("stage4_next_run") or {}
    if st4.get("directive"):
        print("\n🧠 Coach-Direktive für nächste Einheit:")
        print(f"• {st4['directive']}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
