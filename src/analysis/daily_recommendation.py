#!/usr/bin/env python3
"""Daily adaptive running recommendation engine ("Workout of the Day").

Evaluates recovery balance (TSB / ATL / CTL), athlete heart rate thresholds,
and days elapsed since the last running activity to generate a dynamic, daily
training recommendation (rest day, scheduled workout, or gentle re-entry).
"""

from __future__ import annotations

import argparse
from datetime import date, datetime, timedelta, timezone
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


def determine_next_quality_type(
    runs: List[Dict[str, Any]],
    allowed_types: Optional[List[str]] = None,
    preferred_type: Optional[str] = None,
) -> str:
    """Selects next quality workout type (alternating intervals vs tempo) from recent activity history."""
    choices = allowed_types or ["intervals", "tempo_run"]
    if len(choices) == 1:
        return choices[0]

    # Inspect runs reverse chronologically to find the most recent quality run
    last_quality = None
    for r in runs:
        r_type = str(r.get("workout_type") or "").lower()
        name = str(r.get("name") or "").lower()
        if "interval" in r_type or "interval" in name or "speed" in name or "wiederholung" in name:
            last_quality = "intervals"
            break
        if "tempo" in r_type or "tempo" in name or "schwelle" in name or "threshold" in name:
            last_quality = "tempo_run"
            break
        # Heuristic zone inspection: high Z5 time -> intervals, high Z4 time -> tempo
        z_times = r.get("icu_hr_zone_times") or []
        if len(z_times) >= 5:
            z4_time = z_times[3] if len(z_times) > 3 else 0
            z5_plus = sum(z_times[4:]) if len(z_times) > 4 else 0
            if z5_plus > 240:
                last_quality = "intervals"
                break
            elif z4_time > 480:
                last_quality = "tempo_run"
                break

    # Alternate based on last quality workout
    if last_quality == "intervals" and "tempo_run" in choices:
        return "tempo_run"
    elif last_quality == "tempo_run" and "intervals" in choices:
        return "intervals"

    return preferred_type or choices[0]


def check_base_anchor_completed(
    runs: List[Dict[str, Any]],
    ref_date: date,
    slots: List[Dict[str, Any]],
) -> bool:
    """Checks whether a base anchor run was completed in the preceding cycle."""
    anchor_days = set()
    for s in slots:
        if s.get("role") == "base_anchor" or s.get("is_base_anchor") or s.get("is_mandatory_base"):
            for d in s.get("allowed_days", []):
                anchor_days.add(d.lower())

    if not anchor_days:
        anchor_days = {"saturday", "sunday"}

    # Target dates: the most recent occurrences of anchor_days prior to ref_date (up to 7 days lookback)
    target_dates = set()
    for i in range(1, 8):
        past_d = ref_date - timedelta(days=i)
        if past_d.strftime("%A").lower() in anchor_days:
            target_dates.add(past_d)

    for r in runs:
        d_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
        if d_str:
            try:
                run_d = date.fromisoformat(d_str)
                if run_d in target_dates:
                    return True
            except Exception:
                continue
    return False


def resolve_slot_recommendation(
    slots: List[Dict[str, Any]],
    runs: List[Dict[str, Any]],
    ref_date: date,
    tsb: float,
    z2_max: int,
    days_since: int,
) -> Optional[Dict[str, Any]]:
    """Resolves today's structured workout from weekly_slots dynamically (supports N slots)."""
    if not slots:
        return None

    day_name = ref_date.strftime("%A").lower()
    matching_slot = None
    for s in slots:
        allowed = [d.lower() for d in s.get("allowed_days", [])]
        if day_name in allowed:
            matching_slot = s
            break

    if not matching_slot:
        return None

    ref_date_str = ref_date.strftime("%d.%m.%Y")
    slot_id = matching_slot.get("slot") or matching_slot.get("id") or 1
    role = matching_slot.get("role")
    if not role:
        if matching_slot.get("is_mandatory_base") or matching_slot.get("is_base_anchor"):
            role = "base_anchor"
        elif matching_slot.get("workout_type") in ("intervals", "tempo_run") or matching_slot.get("allowed_types"):
            role = "quality"
        else:
            role = "base_anchor"

    # 1. Base Anchor Slot (e.g. Long Run / Zone 2 Base)
    if role == "base_anchor":
        w_type = matching_slot.get("workout_type") or "easy_run"
        target_max = matching_slot.get("target_hr_max", z2_max)
        duration = matching_slot.get("duration_min", 50)
        slot_name = matching_slot.get("name_display") or matching_slot.get("name") or f"Aerober Anker & Long Run (Slot {slot_id})"
        return {
            "date": ref_date.isoformat(),
            "date_display": ref_date_str,
            "days_since_last_run": days_since,
            "action_type": "workout_day",
            "workout_type": w_type,
            "slot": slot_id,
            "role": "base_anchor",
            "is_catch_up": False,
            "headline": f"Heute: {slot_name}",
            "badge": f"Slot {slot_id}: Z2 Long Run",
            "badge_class": "tag-green",
            "icon": "🏃",
            "details": (
                f"Wochenend-Grundlagenlauf: {duration} Minuten entspanntes Traben strikt unter {target_max} bpm. "
                "Dieser Lauf baut deine Kapillarendichte (+13 %) und Fettstoffwechselkapazität auf. "
                "Pace ist zweitrangig – an Steigungen konsequent gehen."
            ),
            "suggested_duration_min": duration,
            "suggested_target_hr": target_max,
            "tsb": tsb,
        }

    # 2. Quality Slot (Dynamically alternates intervals vs. tempo_run; checks base anchor)
    if role == "quality":
        requires_base = matching_slot.get("requires_base_anchor", True) or matching_slot.get("fallback_if_slot3_missed")
        base_done = check_base_anchor_completed(runs, ref_date, slots)
        if requires_base and not base_done:
            # Catch-Up Base Run: Base anchor was missing in preceding cycle!
            return {
                "date": ref_date.isoformat(),
                "date_display": ref_date_str,
                "days_since_last_run": days_since,
                "action_type": "workout_day",
                "workout_type": "easy_run",
                "slot": slot_id,
                "role": "base_anchor",
                "is_catch_up": True,
                "headline": "Heute: Nachhol-Grundlagenlauf (Aerober Anker)",
                "badge": f"Slot {slot_id}: Z2-Nachholbedarf",
                "badge_class": "tag-amber",
                "icon": "🏃",
                "details": (
                    f"Da in der Vorwoche / am Wochenende kein Grundlagenlauf stattfand, greift die Schutzregel: "
                    f"Wir sichern heute zuerst das aerobe Fundament (45–50 min strictly < {z2_max} bpm), "
                    "bevor schnelle Tempo- oder Intervalleinheiten freigegeben werden. Gehpausen an Anstiegen sind erwünscht."
                ),
                "suggested_duration_min": 45,
                "suggested_target_hr": z2_max,
                "tsb": tsb,
            }

        # Resolve quality workout dynamically (not hardcoded!)
        allowed_types = matching_slot.get("allowed_types") or []
        if not allowed_types:
            single = matching_slot.get("workout_type")
            allowed_types = [single] if single else ["intervals", "tempo_run"]
        if "intervals" in allowed_types and "tempo_run" not in allowed_types:
            allowed_types.append("tempo_run")
        elif "tempo_run" in allowed_types and "intervals" not in allowed_types:
            allowed_types.append("intervals")

        preferred = matching_slot.get("preferred_type") or matching_slot.get("workout_type")
        chosen_type = determine_next_quality_type(runs, allowed_types=allowed_types, preferred_type=preferred)

        if chosen_type == "tempo_run":
            hr_range = matching_slot.get("target_hr_range", [168, 174])
            target_hr = hr_range[1] if isinstance(hr_range, list) and len(hr_range) > 1 else 174
            duration = matching_slot.get("duration_min", 40)
            return {
                "date": ref_date.isoformat(),
                "date_display": ref_date_str,
                "days_since_last_run": days_since,
                "action_type": "workout_day",
                "workout_type": "tempo_run",
                "slot": slot_id,
                "role": "quality",
                "is_catch_up": False,
                "headline": f"Heute: Kontrollierter Schwellenlauf (Slot {slot_id})",
                "badge": f"Slot {slot_id}: Schwellenreiz",
                "badge_class": "tag-green",
                "icon": "🔥",
                "details": (
                    f"Zweiter Wochenreiz: Kontrollierte Cruise-Intervalle an der Laktatschwelle ({hr_range[0]}–{hr_range[1]} bpm). "
                    "Ziel ist die Ökonomisierung der Laktatverarbeitung und Stärkung des Herzschlagvolumens ohne Übersäuerung."
                ),
                "suggested_duration_min": duration,
                "suggested_target_hr": target_hr,
                "tsb": tsb,
            }
        else:
            duration = matching_slot.get("duration_min", 35)
            target_desc = matching_slot.get("target", "Intervalle / Speed")
            return {
                "date": ref_date.isoformat(),
                "date_display": ref_date_str,
                "days_since_last_run": days_since,
                "action_type": "workout_day",
                "workout_type": "intervals",
                "slot": slot_id,
                "role": "quality",
                "is_catch_up": False,
                "headline": f"Heute: Tempo- & Intervalltraining (Slot {slot_id})",
                "badge": f"Slot {slot_id}: Tempo / Speed",
                "badge_class": "tag-green",
                "icon": "⚡",
                "details": (
                    f"Das Wochenende ist verarbeitet und deine Form ist frisch (TSB: {tsb:+.1f}). "
                    f"Heute steht die erste Qualitätseinheit der Woche an: {matching_slot.get('description', target_desc)}. "
                    "Achte auf eine saubere, hohe Schrittfrequenz (> 165 spm)."
                ),
                "suggested_duration_min": duration,
                "suggested_target_hr": 0,
                "tsb": tsb,
            }

    # 3. Recovery Slot
    if role == "recovery":
        duration = matching_slot.get("duration_min", 30)
        return {
            "date": ref_date.isoformat(),
            "date_display": ref_date_str,
            "days_since_last_run": days_since,
            "action_type": "workout_day",
            "workout_type": "recovery_run",
            "slot": slot_id,
            "role": "recovery",
            "is_catch_up": False,
            "headline": f"Heute: Aktive Regeneration (Slot {slot_id})",
            "badge": f"Slot {slot_id}: Regeneration",
            "badge_class": "tag-cyan",
            "icon": "🚶",
            "details": f"Sehr lockerer Erholungslauf über {duration} Minuten strictly in Zone 1/2 zum Durchbluten der Muskulatur.",
            "suggested_duration_min": duration,
            "suggested_target_hr": z2_max,
            "tsb": tsb,
        }

    return None


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
            slots = profile.get("athlete", {}).get("weekly_slots", [])
            slot_rec = resolve_slot_recommendation(
                slots=slots,
                runs=runs,
                ref_date=ref_date,
                tsb=tsb,
                z2_max=z2_max,
                days_since=days_since,
            )
            if slot_rec:
                return slot_rec

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

    slots = profile.get("athlete", {}).get("weekly_slots", [])

    # Case 2: 2 to 3 days since last run (optimal workout window)
    if 2 <= days_since <= 3:
        slot_rec = resolve_slot_recommendation(
            slots=slots,
            runs=runs,
            ref_date=ref_date,
            tsb=tsb,
            z2_max=z2_max,
            days_since=days_since,
        )
        if slot_rec:
            return slot_rec

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
        slot_rec = resolve_slot_recommendation(
            slots=slots,
            runs=runs,
            ref_date=ref_date,
            tsb=tsb,
            z2_max=z2_max,
            days_since=days_since,
        )
        if slot_rec:
            return slot_rec

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
