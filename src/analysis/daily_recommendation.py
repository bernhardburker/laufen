#!/usr/bin/env python3
"""Daily adaptive running recommendation engine ("Workout of the Day").

Evaluates recovery balance (TSB / ATL / CTL), athlete heart rate thresholds,
and days elapsed since the last running activity to generate a dynamic, daily
training recommendation (rest day, scheduled workout, or gentle re-entry).
"""

from __future__ import annotations

import argparse
from datetime import date, datetime, timezone
import json
import os
from pathlib import Path
import sys
from typing import Any, Dict, List, Optional

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.analysis.coach_history import get_open_recommendation, load_coach_history
from src.analysis.forecast import calculate_fitness_form_trend
from src.analysis.trends import filter_runs, load_activities


def load_athlete_profile(file_path: Optional[Path] = None) -> Dict[str, Any]:
    athlete_path = file_path or (PROJECT_ROOT / "config" / "athlete.json")
    if athlete_path.is_file():
        try:
            with open(athlete_path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return {}
    return {}


def calculate_days_since_last_run(
    runs: List[Dict[str, Any]], reference_date: Optional[date] = None
) -> int:
    """Calculates integer calendar days between the most recent run and a reference date."""
    if not runs:
        return 999

    ref = reference_date or datetime.now(timezone.utc).date()
    # Sort reverse chronological
    sorted_runs = sorted(
        runs,
        key=lambda r: str(r.get("start_date_local") or r.get("date") or ""),
        reverse=True,
    )
    latest_run = sorted_runs[0]
    date_str = str(latest_run.get("start_date_local") or latest_run.get("date") or "")[:10]
    if not date_str:
        return 999

    try:
        run_date = date.fromisoformat(date_str)
        delta = (ref - run_date).days
        return max(delta, 0)
    except Exception:
        return 999


def generate_daily_recommendation(
    runs: List[Dict[str, Any]],
    athlete_profile: Optional[Dict[str, Any]] = None,
    coach_history: Optional[List[Dict[str, Any]]] = None,
    reference_date: Optional[date] = None,
    form_data: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    """Evaluates the athlete's current state and produces today's targeted training advice."""
    profile = athlete_profile or {}
    history = coach_history or []
    ref_date = reference_date or datetime.now(timezone.utc).date()
    ref_date_str = ref_date.strftime("%d.%m.%Y")

    form = form_data or (calculate_fitness_form_trend(runs) if runs else {})
    tsb = float(form.get("current_tsb", 0.0))
    ctl = float(form.get("latest_ctl", 0.0))
    atl = float(form.get("latest_atl", 0.0))

    hr_info = profile.get("athlete", {}).get("heart_rate", {})
    z2_max = int(
        hr_info.get("zones", {}).get("z2_aerobic", {}).get("max") or 158
    )

    open_rec = get_open_recommendation(history)
    target_hr = int(open_rec.get("target_hr_max") or z2_max) if open_rec else z2_max
    planned_duration = int(open_rec.get("next_duration_min") or 45) if open_rec else 45

    if not runs:
        return {
            "date": ref_date.isoformat(),
            "date_display": ref_date_str,
            "days_since_last_run": 999,
            "action_type": "onboarding",
            "headline": "Heute: Bereit für den Einstieg",
            "badge": "Einstieg",
            "badge_class": "tag-cyan",
            "icon": "🌱",
            "details": "Noch keine Laufaktivitäten verzeichnet. Starte heute mit 20–25 Minuten lockerem Wechsel aus Gehen und Traben.",
            "suggested_duration_min": 20,
            "suggested_target_hr": target_hr,
            "tsb": tsb,
        }

    days_since = calculate_days_since_last_run(runs, ref_date)
    sorted_runs = sorted(
        runs,
        key=lambda r: str(r.get("start_date_local") or r.get("date") or ""),
        reverse=True,
    )
    last_run = sorted_runs[0]
    last_load = float(last_run.get("training_load") or last_run.get("icu_training_load") or 0.0)
    last_name = str(last_run.get("name") or "Lauf")

    # Case 0: Run was completed today
    if days_since == 0:
        return {
            "date": ref_date.isoformat(),
            "date_display": ref_date_str,
            "days_since_last_run": 0,
            "action_type": "rest_day",
            "headline": "Heute: Verdienter Ruhetag & Reizverarbeitung",
            "badge": f"Einheit absolviert (Load {int(round(last_load))})",
            "badge_class": "tag-cyan",
            "icon": "🛌",
            "details": (
                f"Du hast heute bereits eine Einheit absolviert ({last_name}, Load {int(round(last_load))}). "
                f"Dein Körper verarbeitet den Trainingsreiz (Form TSB: {tsb:+.1f}). "
                "Heute stehen Erholung, Dehnen, Flüssigkeit und Schlaf im Vordergrund."
            ),
            "suggested_duration_min": 0,
            "suggested_target_hr": 0,
            "tsb": tsb,
        }

    # Case 1: Exactly 1 day since last run
    if days_since == 1:
        if tsb < -5.0 or last_load >= 50.0:
            return {
                "date": ref_date.isoformat(),
                "date_display": ref_date_str,
                "days_since_last_run": 1,
                "action_type": "rest_day",
                "headline": "Heute: Gezielter Erholungstag",
                "badge": f"Erholung (TSB: {tsb:+.1f})",
                "badge_class": "tag-cyan",
                "icon": "🚶",
                "details": (
                    f"Nach dem gestrigen Lauf baut dein vegetatives Nervensystem die Ermüdung ab (ATL: {atl:.1f}). "
                    "Gönne deinem Körper heute eine Pause oder einen leichten Spaziergang, um für die nächste Einheit frisch zu sein."
                ),
                "suggested_duration_min": 0,
                "suggested_target_hr": 0,
                "tsb": tsb,
            }
        else:
            return {
                "date": ref_date.isoformat(),
                "date_display": ref_date_str,
                "days_since_last_run": 1,
                "action_type": "workout_day",
                "headline": "Heute: Optionale lockere Grundlageneinheit",
                "badge": f"Trainingsbereit (TSB: {tsb:+.1f})",
                "badge_class": "tag-green",
                "icon": "🏃",
                "details": (
                    f"Deine Erholung ist weit fortgeschritten (TSB: {tsb:+.1f}). "
                    f"Wenn du dich fit fühlst, kannst du heute 30–45 Minuten ganz locker in Zone 2 (< {target_hr} bpm) laufen."
                ),
                "suggested_duration_min": planned_duration,
                "suggested_target_hr": target_hr,
                "tsb": tsb,
            }

    # Case 2: 2 to 3 days since last run (optimal workout window)
    if 2 <= days_since <= 3:
        return {
            "date": ref_date.isoformat(),
            "date_display": ref_date_str,
            "days_since_last_run": days_since,
            "action_type": "workout_day",
            "headline": "Heute: Optimaler Tag für deinen Grundlagenlauf",
            "badge": f"Ideales Trainingsfenster (TSB: {tsb:+.1f})",
            "badge_class": "tag-green",
            "icon": "🏃",
            "details": (
                f"Deine Ermüdung ist vollständig abgebaut und deine Form ist im optimalen Bereich (TSB: {tsb:+.1f}). "
                f"Heute ist der perfekte Zeitpunkt für die nächste Einheit: {planned_duration} Minuten strikt unter {target_hr} bpm."
            ),
            "suggested_duration_min": planned_duration,
            "suggested_target_hr": target_hr,
            "tsb": tsb,
        }

    # Case 3: 4 to 6 days since last run (pause extending)
    if 4 <= days_since <= 6:
        return {
            "date": ref_date.isoformat(),
            "date_display": ref_date_str,
            "days_since_last_run": days_since,
            "action_type": "workout_day",
            "headline": "Heute: Empfohlener Lauftag (Pause zieht sich)",
            "badge": f"{days_since} Tage Pause (TSB: {tsb:+.1f})",
            "badge_class": "tag-amber",
            "icon": "⚡",
            "details": (
                f"Seit {days_since} Tagen hast du keinen Lauf registriert. Deine Akkus sind voll (TSB: {tsb:+.1f}), "
                "doch die aerobe Trainingsreiz-Kurve beginnt abzuflachen. Nutze den Tag für deinen Grundlagenlauf."
            ),
            "suggested_duration_min": planned_duration,
            "suggested_target_hr": target_hr,
            "tsb": tsb,
        }

    # Case 4: 7+ days (extended hiatus / illness / re-entry)
    reentry_duration = 25
    return {
        "date": ref_date.isoformat(),
        "date_display": ref_date_str,
        "days_since_last_run": days_since,
        "action_type": "reentry_run",
        "headline": "Heute: Sanfter Wiedereinstieg nach Trainingspause",
        "badge": f"Wiedereinstieg ({days_since} Tage Pause)",
        "badge_class": "tag-amber",
        "icon": "🌱",
        "details": (
            f"Seit {days_since} Tagen hast du keine Laufeinheit absolviert. "
            f"Nach einer längeren Pause (oder Krankheit) ist Behutsamkeit Pflicht: Starte heute mit nur {reentry_duration}–30 Minuten "
            f"sehr lockerem Traben (< {target_hr} bpm), um Sehnen und das Herz-Kreislauf-System schonend wieder an Belastung zu gewöhnen."
        ),
        "suggested_duration_min": reentry_duration,
        "suggested_target_hr": target_hr,
        "tsb": tsb,
    }


def render_daily_recommendation_html(recommendation: Dict[str, Any]) -> str:
    """Renders a visually striking, responsive HTML card for today's recommendation."""
    headline = recommendation.get("headline", "Tagesempfehlung")
    badge = recommendation.get("badge", "Empfehlung")
    badge_class = recommendation.get("badge_class", "tag-green")
    icon = recommendation.get("icon", "🏃")
    details = recommendation.get("details", "")
    duration = recommendation.get("suggested_duration_min", 0)
    target_hr = recommendation.get("suggested_target_hr", 0)
    tsb_val = recommendation.get("tsb", 0.0)
    date_display = recommendation.get("date_display", "")

    metrics_chips = []
    if duration > 0:
        metrics_chips.append(
            f'<div class="daily-metric"><span class="daily-metric-lbl">Dauer</span><span class="daily-metric-val">{duration} Min.</span></div>'
        )
    if target_hr > 0:
        metrics_chips.append(
            f'<div class="daily-metric"><span class="daily-metric-lbl">Zielpuls</span><span class="daily-metric-val">&lt; {target_hr} bpm</span></div>'
        )
    metrics_chips.append(
        f'<div class="daily-metric"><span class="daily-metric-lbl">Form (TSB)</span><span class="daily-metric-val">{tsb_val:+.1f}</span></div>'
    )

    chips_html = "\n".join(metrics_chips)

    return f"""
    <div class="card daily-card">
      <div class="daily-header">
        <div class="daily-title-row">
          <span class="daily-icon">{icon}</span>
          <div>
            <div class="daily-kicker">Empfehlung für heute ({date_display})</div>
            <h3 class="daily-headline">{headline}</h3>
          </div>
        </div>
        <span class="tag {badge_class}">{badge}</span>
      </div>
      <p class="daily-details">{details}</p>
      <div class="daily-metrics-row">
        {chips_html}
      </div>
    </div>
    """


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Calculate daily adaptive running recommendation for today."
    )
    parser.add_argument(
        "--input",
        "-i",
        type=Path,
        default=Path(
            os.getenv("ACTIVITIES_PATH", str(PROJECT_ROOT / "data" / "intervals_activities.json"))
        ),
        help="Path to activity JSON file",
    )
    parser.add_argument(
        "--athlete",
        "-a",
        type=Path,
        default=PROJECT_ROOT / "config" / "athlete.json",
        help="Path to athlete configuration file",
    )
    parser.add_argument(
        "--history",
        type=Path,
        default=PROJECT_ROOT / "data" / "coach_history.json",
        help="Path to coach history JSON",
    )
    parser.add_argument(
        "--date",
        type=str,
        help="Reference date for evaluation (YYYY-MM-DD, defaults to today)",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="Output raw JSON instead of formatted text",
    )

    args = parser.parse_args()

    ref_d = date.fromisoformat(args.date) if args.date else None
    activities = load_activities(args.input) if args.input.is_file() else []
    runs = filter_runs(activities)
    profile = load_athlete_profile(args.athlete)
    history = load_coach_history(args.history) if args.history.is_file() else []

    rec = generate_daily_recommendation(
        runs=runs,
        athlete_profile=profile,
        coach_history=history,
        reference_date=ref_d,
    )

    if args.json:
        print(json.dumps(rec, ensure_ascii=False, indent=2))
        return 0

    print("======================================================================")
    print(f"  Tagesempfehlung ({rec['date_display']})")
    print("======================================================================")
    print(f"Status:   {rec['headline']} [{rec['badge']}]")
    print(f"Details:  {rec['details']}")
    if rec['suggested_duration_min'] > 0:
        print(f"Vorgabe:  {rec['suggested_duration_min']} Min. @ Puls < {rec['suggested_target_hr']} bpm")
    print(f"Erholung: TSB {rec['tsb']:+.1f}")
    print("======================================================================")
    return 0


if __name__ == "__main__":
    sys.exit(main())
