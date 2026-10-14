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


def is_quality_workout(run: Dict[str, Any]) -> bool:
    """Returns True if the run is classified as a quality session (intervals, tempo, threshold, race)."""
    r_type = str(run.get("workout_type") or "").lower()
    name = str(run.get("name") or "").lower()
    if any(k in r_type or k in name for k in ("interval", "tempo", "schwelle", "threshold", "speed", "race", "wettkampf")):
        return True
    z_times = run.get("icu_hr_zone_times") or []
    if len(z_times) >= 5:
        z4_time = z_times[3] if len(z_times) > 3 else 0
        z5_plus = sum(z_times[4:]) if len(z_times) > 4 else 0
        if z5_plus > 240 or z4_time > 480:
            return True
    return False


def is_base_workout(run: Dict[str, Any], z2_max: int = 158) -> bool:
    """Returns True if the run is classified as an aerobic base / easy run."""
    if is_quality_workout(run):
        return False
    r_type = str(run.get("workout_type") or "").lower()
    name = str(run.get("name") or "").lower()
    if any(k in r_type or k in name for k in ("easy", "long_run", "base", "grundlage", "rekom", "recovery")):
        return True
    avg_hr = float(run.get("average_heartrate") or run.get("avg_hr") or 0.0)
    if 0 < avg_hr <= z2_max + 3:
        return True
    z_times = run.get("icu_hr_zone_times") or []
    if z_times and sum(z_times) > 0:
        easy_time = sum(z_times[:2])
        if easy_time / sum(z_times) >= 0.65:
            return True
    return False


def calculate_consecutive_build_weeks(
    runs: List[Dict[str, Any]],
    ref_date: date,
    min_load: float = 65.0,
    min_distance_m: float = 12000.0,
) -> int:
    """Calculates how many consecutive completed calendar weeks prior to ref_date were build weeks."""
    current_monday = ref_date - timedelta(days=ref_date.weekday())
    consecutive_build = 0

    for w in range(1, 5):
        week_start = current_monday - timedelta(weeks=w)
        week_end = week_start + timedelta(days=6)

        week_runs = []
        for r in runs:
            d_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
            if d_str:
                try:
                    run_d = date.fromisoformat(d_str)
                    if week_start <= run_d <= week_end:
                        week_runs.append(r)
                except Exception:
                    continue

        if not week_runs:
            break

        total_load = sum(float(r.get("training_load") or r.get("icu_training_load") or 0.0) for r in week_runs)
        total_dist = sum(float(r.get("distance") or 0.0) for r in week_runs)
        has_quality = any(is_quality_workout(r) for r in week_runs)

        if has_quality and (total_load >= min_load or total_dist >= min_distance_m):
            consecutive_build += 1
        else:
            break

    return consecutive_build


def calculate_stimulus_context(
    runs: List[Dict[str, Any]],
    ref_date: date,
    z2_max: int = 158,
) -> Dict[str, Any]:
    """Calculates stimulus history: consecutive quality runs, days since last base run, etc."""
    sorted_past_runs = []
    for r in runs:
        d_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
        if d_str:
            try:
                d = date.fromisoformat(d_str)
                if d < ref_date:
                    sorted_past_runs.append((d, r))
            except Exception:
                continue
    sorted_past_runs.sort(key=lambda item: item[0], reverse=True)

    consecutive_quality = 0
    for _, r in sorted_past_runs:
        if is_quality_workout(r):
            consecutive_quality += 1
        elif is_base_workout(r, z2_max=z2_max):
            break

    days_since_base = 999
    for d, r in sorted_past_runs:
        if is_base_workout(r, z2_max=z2_max):
            days_since_base = (ref_date - d).days
            break

    quality_last_7d = sum(
        1 for d, r in sorted_past_runs
        if (ref_date - d).days <= 7 and is_quality_workout(r)
    )

    consecutive_build = calculate_consecutive_build_weeks(runs, ref_date)

    return {
        "consecutive_quality_runs": consecutive_quality,
        "days_since_last_base_run": days_since_base,
        "quality_runs_last_7d": quality_last_7d,
        "consecutive_build_weeks": consecutive_build,
        "is_deload_week": (consecutive_build >= 3),
    }


def check_base_anchor_completed(
    runs: List[Dict[str, Any]],
    ref_date: date,
    slots: List[Dict[str, Any]],
    z2_max: int = 158,
) -> bool:
    """Checks whether a base anchor run was completed in the preceding cycle or recently."""
    if not runs:
        return False

    stimulus = calculate_stimulus_context(runs, ref_date, z2_max=z2_max)
    if stimulus["days_since_last_base_run"] <= 7:
        return True

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

    # 1. Did the runner complete a run on the anchor days?
    for r in runs:
        d_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
        if d_str:
            try:
                run_d = date.fromisoformat(d_str)
                if run_d in target_dates:
                    return True
            except Exception:
                continue

    # 2. Hybrid Catch-Up: Did the runner complete a base run on or after the anchor period started?
    min_anchor_date = min(target_dates) if target_dates else (ref_date - timedelta(days=7))
    for r in runs:
        d_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
        if d_str:
            try:
                run_d = date.fromisoformat(d_str)
                if min_anchor_date <= run_d < ref_date:
                    if is_base_workout(r, z2_max=z2_max):
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
    coach_history_rec: Optional[Dict[str, Any]] = None,
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

    # Check for Deload Week (automatically triggered after 3 consecutive build weeks)
    consecutive_build_weeks = calculate_consecutive_build_weeks(runs, ref_date)
    is_deload = (consecutive_build_weeks >= 3)
    if is_deload:
        deload_duration = 40 if role == "base_anchor" else 35
        return {
            "date": ref_date.isoformat(),
            "date_display": ref_date_str,
            "days_since_last_run": days_since,
            "action_type": "workout_day",
            "workout_type": "easy_run",
            "slot": slot_id,
            "role": "deload",
            "is_catch_up": False,
            "headline": f"Heute: Aktive Regeneration (Deload Slot {slot_id})",
            "badge": f"Slot {slot_id}: Deload-Woche",
            "badge_class": "tag-cyan",
            "icon": "🌱",
            "details": (
                f"Nach {consecutive_build_weeks} aufeinanderfolgenden Belastungswochen steht diese Woche Superkompensation an. "
                f"Wir nehmen das Tempo komplett heraus: {deload_duration} Minuten ganz entspanntes Traben strictly unter {z2_max} bpm, "
                "damit sich Sehnen und Nervensystem regenerieren können."
            ),
            "suggested_duration_min": deload_duration,
            "suggested_target_hr": z2_max,
            "tsb": tsb,
        }

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

    # 2. Quality Slot (Dynamically alternates intervals vs. tempo_run; checks base anchor & stimulus balance)
    if role == "quality":
        requires_base = matching_slot.get("requires_base_anchor", True) or matching_slot.get("fallback_if_slot3_missed")
        base_done = check_base_anchor_completed(runs, ref_date, slots, z2_max=z2_max)
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

        # Check stimulus context for recovery guardrails & coaching guidance
        stimulus = calculate_stimulus_context(runs, ref_date, z2_max=z2_max)
        consec_quality = stimulus["consecutive_quality_runs"]
        days_since_base = stimulus["days_since_last_base_run"]

        # Hard guardrail 1: Back-to-back quality is not allowed (days_since < 2 after quality)
        if consec_quality >= 1 and days_since < 2:
            return {
                "date": ref_date.isoformat(),
                "date_display": ref_date_str,
                "days_since_last_run": days_since,
                "action_type": "workout_day",
                "workout_type": "easy_run",
                "slot": slot_id,
                "role": "base_anchor",
                "is_catch_up": False,
                "headline": "Heute: Aktive Erholung / Z2 statt Doppelbelastung",
                "badge": f"Slot {slot_id}: Schutz vor Überreizung",
                "badge_class": "tag-cyan",
                "icon": "🚶",
                "details": (
                    f"Du hast gestern bereits einen intensiven Reiz gesetzt. "
                    f"Um das vegetative Nervensystem zu schonen, steht heute aktive Erholung oder lockeres Traben in Z1/Z2 (< {z2_max} bpm) an."
                ),
                "suggested_duration_min": 35,
                "suggested_target_hr": z2_max,
                "tsb": tsb,
            }

        # Hard guardrail 2: 2 or more consecutive quality runs without intervening base run
        if consec_quality >= 2:
            return {
                "date": ref_date.isoformat(),
                "date_display": ref_date_str,
                "days_since_last_run": days_since,
                "action_type": "workout_day",
                "workout_type": "easy_run",
                "slot": slot_id,
                "role": "base_anchor",
                "is_catch_up": True,
                "headline": "Heute: Aerobe Fundamentsicherung (Z2)",
                "badge": f"Slot {slot_id}: Reizausgleich",
                "badge_class": "tag-amber",
                "icon": "🏃",
                "details": (
                    f"Die letzten beiden Einheiten waren intensiv. Zur Vermeidung von Überlastung "
                    f"und zur Unterstützung der Mitochondriendichte schieben wir heute einen Z2-Lauf (< {z2_max} bpm) ein."
                ),
                "suggested_duration_min": 45,
                "suggested_target_hr": z2_max,
                "tsb": tsb,
            }

        # Flexible guidance: If base run is overdue (>= 9 days)
        if days_since_base >= 9 and days_since_base != 999:
            return {
                "date": ref_date.isoformat(),
                "date_display": ref_date_str,
                "days_since_last_run": days_since,
                "action_type": "workout_day",
                "workout_type": "easy_run",
                "slot": slot_id,
                "role": "base_anchor",
                "is_catch_up": True,
                "headline": "Heute: Auffrischung der Grundlagenausdauer",
                "badge": f"Slot {slot_id}: Basis auffrischen",
                "badge_class": "tag-amber",
                "icon": "🏃",
                "details": (
                    f"Dein letzter reiner Grundlagenlauf liegt bereits {days_since_base} Tage zurück. "
                    f"Bevor wir wieder harte Spitzen setzen, frischen wir dein aerobes Fundament mit 45 Minuten strictly < {z2_max} bpm auf."
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

    # 3. Flexible Slot (Defaults to Zone 2 base run; upgrades to Threshold / Tempo when fresh)
    if role in ("flexible", "adaptive_quality"):
        requires_base = matching_slot.get("requires_base_anchor", True)
        base_done = check_base_anchor_completed(runs, ref_date, slots, z2_max=z2_max)
        stimulus = calculate_stimulus_context(runs, ref_date, z2_max=z2_max)
        consec_quality = stimulus["consecutive_quality_runs"]
        days_since_base = stimulus["days_since_last_base_run"]

        hr_range = matching_slot.get("target_hr_range", [168, 174])
        target_quality_hr = hr_range[1] if isinstance(hr_range, list) and len(hr_range) > 1 else 174
        quality_duration = matching_slot.get("duration_min", 40)
        base_duration = 45

        # Freshness condition: positive form, at least 2 days rest after a quality run (consec_quality <= 1), and base anchor intact
        is_fresh = (tsb >= 0.0 and consec_quality <= 1 and days_since >= 2 and base_done and days_since_base <= 8)

        # Hybrid AI Coach fine-tuning
        coach_pref = (coach_history_rec or {}).get("next_workout_type")
        coach_note = (coach_history_rec or {}).get("recommendation", "")

        # If AI Coach explicitly recommended easy_run or recovery, respect it even if technically fresh
        if coach_pref in ("easy_run", "recovery_run"):
            is_fresh = False

        if is_fresh:
            details_text = (
                f"Deine Form ist frisch (TSB: {tsb:+.1f}) und der aerobe Anker ist gelegt. "
                f"Heute ist der kontrollierte Schwellenlauf freigegeben ({hr_range[0]}–{hr_range[1]} bpm). "
                "Alternativ kannst du 40–45 Minuten ganz locker in Zone 2 laufen, falls du ein reines Grundlagentraining bevorzugst."
            )
            headline_text = f"Heute: Kontrollierter Schwellenlauf (Slot {slot_id}) [Optional: Z2]"
            if coach_pref == "tempo_run":
                headline_text = f"Heute: Kontrollierter Schwellenlauf (Slot {slot_id}) [Coach-Empfehlung]"
                if coach_note:
                    details_text = f"Coach-Hinweis: {coach_note} | " + details_text

            return {
                "date": ref_date.isoformat(),
                "date_display": ref_date_str,
                "days_since_last_run": days_since,
                "action_type": "workout_day",
                "workout_type": "tempo_run",
                "slot": slot_id,
                "role": "flexible",
                "is_catch_up": False,
                "headline": headline_text,
                "badge": f"Slot {slot_id}: Schwellenreiz",
                "badge_class": "tag-green",
                "icon": "🔥",
                "details": details_text,
                "suggested_duration_min": quality_duration,
                "suggested_target_hr": target_quality_hr,
                "tsb": tsb,
            }
        else:
            details_text = (
                f"Heute steht das aerobe Fundament im Vordergrund (40–45 min strictly < {z2_max} bpm). "
                f"Da dein Körper noch Reize verarbeitet oder Erholung benötigt (TSB: {tsb:+.1f}), ist ein ruhiger Dauerlauf optimal."
            )
            headline_text = f"Heute: Aerobe Basis & Regeneration (Slot {slot_id})"
            if coach_pref in ("easy_run", "recovery_run"):
                headline_text = f"Heute: Aerobe Basis & Regeneration (Slot {slot_id}) [Coach-Empfehlung]"
                if coach_note:
                    details_text = f"Coach-Empfehlung: {coach_note} | " + details_text

            return {
                "date": ref_date.isoformat(),
                "date_display": ref_date_str,
                "days_since_last_run": days_since,
                "action_type": "workout_day",
                "workout_type": "easy_run",
                "slot": slot_id,
                "role": "flexible",
                "is_catch_up": False,
                "headline": headline_text,
                "badge": f"Slot {slot_id}: Z2 Grundlagenlauf",
                "badge_class": "tag-green",
                "icon": "🏃",
                "details": details_text,
                "suggested_duration_min": base_duration,
                "suggested_target_hr": z2_max,
                "tsb": tsb,
            }

    # 4. Recovery Slot
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

    stimulus = calculate_stimulus_context(runs, ref_date, z2_max=z2_max)

    def _finalize(rec: Dict[str, Any]) -> Dict[str, Any]:
        rec.setdefault("stimulus_context", stimulus)
        return rec

    if not runs:
        return _finalize({
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
        })

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
        return _finalize({
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
        })

    # Case 1: Exactly 1 day since last run
    if days_since == 1:
        if tsb < -5.0 or last_load >= 50.0:
            return _finalize({
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
            })
        else:
            slots = profile.get("athlete", {}).get("weekly_slots", [])
            slot_rec = resolve_slot_recommendation(
                slots=slots,
                runs=runs,
                ref_date=ref_date,
                tsb=tsb,
                z2_max=z2_max,
                days_since=days_since,
                coach_history_rec=open_rec,
            )
            if slot_rec:
                return _finalize(slot_rec)

            return _finalize({
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
            })

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
            coach_history_rec=open_rec,
        )
        if slot_rec:
            return _finalize(slot_rec)

        return _finalize({
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
        })

    # Case 3: 4 to 6 days since last run (pause extending)
    if 4 <= days_since <= 6:
        slot_rec = resolve_slot_recommendation(
            slots=slots,
            runs=runs,
            ref_date=ref_date,
            tsb=tsb,
            z2_max=z2_max,
            days_since=days_since,
            coach_history_rec=open_rec,
        )
        if slot_rec:
            return _finalize(slot_rec)

        return _finalize({
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
        })

    # Case 4: 7+ days (extended hiatus / illness / re-entry)
    reentry_duration = 25
    return _finalize({
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
    })


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
