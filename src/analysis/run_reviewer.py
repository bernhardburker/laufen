#!/usr/bin/env python3
"""Incremental per-run deep physiological analysis and 4-stage review cache.

Extracts second-level HR zone distributions, cadence, peak HR, elevation,
aerobic decoupling/efficiency, and interval breakdowns for each running activity.
Evaluates runs according to the 4-stage review specification:
  Stage 1: Physiological Run Review (Lauf-Bewertung)
  Stage 2: Target vs. Actual Compliance (Plan-Erfüllung)
  Stage 3: Comparison & Actionable Optimizations (Vergleich & Optimierung)
  Stage 4: Post-Run Recovery & Next Slot Guidance (Nächster Lauf & Erholung)

Persists per-run coach reviews into a local cache (data/run_reviews.json) with schema_version: 2.
"""

from __future__ import annotations

import argparse
import datetime
import json
import math
import os
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.analysis.coach_history import load_coach_history
from src.analysis.trends import filter_runs, format_duration, format_pace, load_activities


def load_athlete_profile(file_path: Optional[Path] = None) -> Dict[str, Any]:
    athlete_path = file_path or (PROJECT_ROOT / "config" / "athlete.json")
    if athlete_path.is_file():
        try:
            with open(athlete_path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return {}
    return {}


def format_seconds_to_min_sec(seconds: int | float) -> str:
    """Format seconds into 'Xm Ys' or 'Xs'."""
    if seconds <= 0:
        return "0s"
    m, s = divmod(int(round(seconds)), 60)
    if m > 0:
        return f"{m}m {s:02d}s" if s > 0 else f"{m}m"
    return f"{s}s"


def parse_interval_summary(summary_list: List[str]) -> List[Dict[str, Any]]:
    """Parse interval summary strings (e.g. '3x 5m59s 168bpm') into structured interval objects."""
    parsed: List[Dict[str, Any]] = []
    pattern = re.compile(r"(\d+)x\s*(?:(\d+)m)?\s*(\d+)s?\s*(\d+)bpm", re.IGNORECASE)

    for item in summary_list:
        raw = str(item).strip()
        match = pattern.search(raw)
        if match:
            reps = int(match.group(1))
            m_part = int(match.group(2) or 0)
            s_part = int(match.group(3) or 0)
            avg_hr = int(match.group(4))
            duration_s = m_part * 60 + s_part
            duration_str = format_seconds_to_min_sec(duration_s)

            if avg_hr >= 165:
                role = "work"
            elif avg_hr <= 152:
                role = "recovery"
            else:
                role = "steady"

            parsed.append(
                {
                    "raw": raw,
                    "reps": reps,
                    "duration_s": duration_s,
                    "duration_str": duration_str,
                    "avg_hr": avg_hr,
                    "role": role,
                }
            )
        else:
            parsed.append({"raw": raw, "reps": 1, "duration_s": 0, "duration_str": raw, "avg_hr": 0, "role": "info"})

    return parsed


def extract_deep_run_metrics(
    run: Dict[str, Any], athlete_profile: Optional[Dict[str, Any]] = None
) -> Dict[str, Any]:
    """Extract exhaustive physiological and mechanical metrics for a single run."""
    distance_m = float(run.get("distance") or 0.0)
    moving_time_s = int(run.get("moving_time") or 0)
    distance_km = round(distance_m / 1000.0, 2)

    pace_s_per_km = moving_time_s / distance_km if distance_km > 0 else 0.0
    pace_str = format_pace(pace_s_per_km)

    avg_hr = round(float(run.get("average_heartrate") or 0.0), 1)
    max_hr = int(round(float(run.get("max_heartrate") or 0.0)))

    # Cadence: Garmin/Intervals.icu reports RPM (one leg); double if <= 100 for SPM
    raw_cadence = float(run.get("average_cadence") or 0.0)
    if raw_cadence > 0:
        cadence_spm = round(raw_cadence * 2 if raw_cadence <= 100 else raw_cadence, 1)
    else:
        cadence_spm = 0.0

    elevation_gain = round(float(run.get("total_elevation_gain") or 0.0), 1)
    elevation_loss = round(float(run.get("total_elevation_loss") or 0.0), 1)
    training_load = round(float(run.get("icu_training_load") or run.get("training_load") or 0.0), 1)

    # Point-in-time load and form metrics from activity
    icu_ctl = float(run.get("icu_ctl")) if run.get("icu_ctl") is not None else None
    icu_atl = float(run.get("icu_atl")) if run.get("icu_atl") is not None else None
    tsb_at_run = round(icu_ctl - icu_atl, 1) if (icu_ctl is not None and icu_atl is not None) else None

    # Zone breakdown
    zone_times = run.get("icu_hr_zone_times") or []
    total_zone_s = sum(zone_times) if isinstance(zone_times, list) else 0
    zone_breakdown: Dict[str, Dict[str, Any]] = {}

    zone_names = [
        "Zone 1 (Recovery)",
        "Zone 2 (Aerobic)",
        "Zone 3 (Tempo)",
        "Zone 4 (Threshold/Schwelle)",
        "Zone 5 (VO2max)",
        "Zone 6 (Anaerobic)",
        "Zone 7 (Neuromuscular)",
    ]

    z1_z2_seconds = 0
    if isinstance(zone_times, list):
        for idx, sec in enumerate(zone_times):
            name = zone_names[idx] if idx < len(zone_names) else f"Zone {idx + 1}"
            sec_val = int(sec or 0)
            pct = round((sec_val / total_zone_s) * 100.0, 1) if total_zone_s > 0 else 0.0
            zone_breakdown[f"z{idx + 1}"] = {
                "name": name,
                "seconds": sec_val,
                "time_str": format_seconds_to_min_sec(sec_val),
                "pct": pct,
            }
            if idx in (0, 1):
                z1_z2_seconds += sec_val

    z1_z2_pct = (
        round((z1_z2_seconds / total_zone_s) * 100.0, 1) if total_zone_s > 0 else 0.0
    )

    # Aerobic efficiency factor: speed (m/min) / HR (bpm)
    ef = None
    if avg_hr > 0 and moving_time_s > 0:
        speed_m_per_min = distance_m / (moving_time_s / 60.0)
        ef = round(speed_m_per_min / avg_hr, 3)

    # Intervals summary
    raw_intervals = run.get("interval_summary") or []
    if isinstance(raw_intervals, list):
        interval_strings = [str(x) for x in raw_intervals]
    else:
        interval_strings = []
    parsed_intervals = parse_interval_summary(interval_strings)

    # Athlete thresholds
    hr_profile = (athlete_profile or {}).get("athlete", {}).get("heart_rate", {})
    lthr = int(hr_profile.get("lthr") or 178)
    z2_max = int(
        hr_profile.get("zones", {}).get("z2_aerobic", {}).get("max") or 158
    )

    # Intensity classification
    intensity_class = classify_run_intensity(
        z1_z2_pct=z1_z2_pct,
        zone_breakdown=zone_breakdown,
        avg_hr=avg_hr,
        z2_max=z2_max,
        lthr=lthr,
    )

    return {
        "id": str(run.get("id")),
        "date": str(run.get("start_date_local") or "")[:19],
        "name": str(run.get("name") or "Lauf"),
        "description": run.get("description") or run.get("notes") or "",
        "athlete_note": run.get("athlete_note") or run.get("description") or run.get("notes") or "",
        "distance_km": distance_km,
        "moving_time_s": moving_time_s,
        "moving_time_str": format_duration(moving_time_s),
        "pace_str": pace_str,
        "pace_s_per_km": pace_s_per_km,
        "avg_hr": avg_hr,
        "max_hr": max_hr,
        "cadence_spm": cadence_spm,
        "elevation_gain_m": elevation_gain,
        "elevation_loss_m": elevation_loss,
        "training_load": training_load,
        "icu_ctl": icu_ctl,
        "icu_atl": icu_atl,
        "tsb_at_run": tsb_at_run,
        "aerobic_ef": ef,
        "z1_z2_seconds": z1_z2_seconds,
        "total_zone_seconds": total_zone_s,
        "z1_z2_pct": z1_z2_pct,
        "zone_breakdown": zone_breakdown,
        "intervals": interval_strings,
        "parsed_intervals": parsed_intervals,
        "intensity_class": intensity_class,
    }


def classify_run_intensity(
    z1_z2_pct: float,
    zone_breakdown: Dict[str, Dict[str, Any]],
    avg_hr: float,
    z2_max: int,
    lthr: int,
) -> str:
    """Classify the primary physiological category of the run following 4-stage review specification."""
    z3_pct = zone_breakdown.get("z3", {}).get("pct", 0.0)
    z4_pct = zone_breakdown.get("z4", {}).get("pct", 0.0)
    z5_pct = zone_breakdown.get("z5", {}).get("pct", 0.0)

    z3_seconds = zone_breakdown.get("z3", {}).get("seconds", 0)
    z4_seconds = zone_breakdown.get("z4", {}).get("seconds", 0)
    z5_seconds = zone_breakdown.get("z5", {}).get("seconds", 0)

    # 1. High-intensity / intervals: high Z5 time or combined Z4+Z5 >= 15m @ near LTHR
    if z5_pct >= 10.0 or (z4_seconds + z5_seconds >= 900 and avg_hr >= lthr - 5):
        return "schwellen_intervall"

    # 2. Threshold run: >= 12m (720s) in Z4 OR (>= 25% in Z4 and Z3+Z4 >= 20m)
    if z4_seconds >= 720 or (z4_pct >= 25.0 and z3_seconds + z4_seconds >= 1200):
        return "schwellenlauf"

    # 3. Pure Aerobic Base: >= 80% in Zone 1/2 and avg HR <= z2_max
    if z1_z2_pct >= 80.0 and avg_hr <= z2_max:
        return "aerobe_basis"

    # 4. Predominantly Aerobic: >= 60% in Zone 1/2
    if z1_z2_pct >= 60.0:
        return "ueberwiegend_aerob"

    # 5. Zone 3 Tempo Run
    if z3_pct >= 35.0 and z4_seconds < 720:
        return "tempo_z3"

    return "gemischte_intensitaet"


def resolve_run_intent(
    run: Dict[str, Any],
    metrics: Dict[str, Any],
    athlete_profile: Optional[Dict[str, Any]] = None,
    coach_history: Optional[List[Dict[str, Any]]] = None,
) -> Dict[str, Any]:
    """Resolves target intent vs actual execution across name, slots, and coach history."""
    run_name = str(run.get("name") or "").lower()
    run_date_str = str(metrics.get("date") or "")[:10]
    weekday_name = "unknown"
    if run_date_str:
        try:
            d = datetime.date.fromisoformat(run_date_str)
            weekday_name = d.strftime("%A").lower()
        except Exception:
            pass

    slots = (athlete_profile or {}).get("athlete", {}).get("weekly_slots", [])
    if not slots:
        slots = [
            {"slot": 1, "role": "quality", "allowed_days": ["monday", "tuesday"]},
            {"slot": 2, "role": "flexible", "allowed_days": ["wednesday", "thursday", "friday"]},
            {"slot": 3, "role": "base_anchor", "allowed_days": ["saturday", "sunday"]},
        ]
    hr_profile = (athlete_profile or {}).get("athlete", {}).get("heart_rate", {})
    z2_max = int(hr_profile.get("zones", {}).get("z2_aerobic", {}).get("max") or 158)
    lthr = int(hr_profile.get("lthr") or 178)

    # Keywords in name
    quality_keywords = ("schwelle", "threshold", "tempo", "intervall", "interval", "speed", "fartlek", "fahrtspiel")
    base_keywords = ("easy", "grundlage", "base", "long run", "anker", "rekom", "recovery")

    is_quality_name = any(k in run_name for k in quality_keywords)
    is_base_name = any(k in run_name for k in base_keywords)

    # Match weekday slot
    matched_slot = None
    for s in slots:
        allowed = [day.lower() for day in s.get("allowed_days", [])]
        if weekday_name in allowed:
            matched_slot = s
            break

    slot_role = matched_slot.get("role", "flexible") if matched_slot else "flexible"
    slot_id = (matched_slot.get("slot") or matched_slot.get("id") or 2) if matched_slot else 2

    # Intent disambiguation
    if is_quality_name:
        intended_type = "threshold" if any(k in run_name for k in ("schwelle", "threshold", "tempo")) else "intervals"
        planned_slot = f"Slot {slot_id} (Qualitätsreiz)"
        intent_source = "name"
    elif is_base_name:
        intended_type = "easy_run"
        planned_slot = f"Slot {slot_id} (Aerobe Basis)"
        intent_source = "name"
    elif slot_role == "base_anchor":
        intended_type = "easy_run"
        planned_slot = f"Slot {slot_id} (Wochenende – Aerober Anker)"
        intent_source = "slot"
    elif slot_role == "quality":
        intended_type = "intervals" if "interval" in run_name else "tempo_run"
        planned_slot = f"Slot {slot_id} (Wochenanfang – Tempo/Intervalle)"
        intent_source = "slot"
    else:  # flexible slot
        # If athlete accumulated >= 10m in Z4 or classified as threshold, resolve to threshold option
        z4_sec = metrics.get("zone_breakdown", {}).get("z4", {}).get("seconds", 0)
        if z4_sec >= 600 or metrics.get("intensity_class") in ("schwellenlauf", "schwellen_intervall"):
            intended_type = "threshold"
            planned_slot = f"Slot {slot_id} (Wochenmitte – Schwellen-Option)"
            intent_source = "slot+physiology"
        else:
            intended_type = "easy_run"
            planned_slot = f"Slot {slot_id} (Wochenmitte – Aerobe Basis)"
            intent_source = "slot"

    # Evaluate target vs actual compliance
    z4_sec = metrics.get("zone_breakdown", {}).get("z4", {}).get("seconds", 0)
    z3_sec = metrics.get("zone_breakdown", {}).get("z3", {}).get("seconds", 0)
    z1_z2_pct = metrics.get("z1_z2_pct", 0.0)
    avg_hr = metrics.get("avg_hr", 0.0)

    intervals_icu_compliance = run.get("compliance")

    if intended_type in ("threshold", "tempo_run", "intervals"):
        if z4_sec >= 720 or metrics["intensity_class"] in ("schwellenlauf", "schwellen_intervall"):
            status = "erfüllt"
            status_label = "Erfüllt"
            target_comparison = (
                f"{format_seconds_to_min_sec(z4_sec)} in Zone 4 (Ziel: >= 12m @ 168–174 bpm). "
                f"Die {format_seconds_to_min_sec(z3_sec)} in Zone 3 resultieren aus Anlauf-Lag und Nachbrenneffekt in den Pausen."
            )
        elif z4_sec >= 360:
            status = "teilweise"
            status_label = "Teilweise erfüllt"
            target_comparison = (
                f"{format_seconds_to_min_sec(z4_sec)} in Zone 4 (Ziel: >= 12m). "
                "Der Schwellenreiz wurde vorzeitig abgebrochen oder gedrosselt."
            )
        else:
            status = "abweichend"
            status_label = "Abweichend"
            target_comparison = "Temporeiz entfiel zugunsten einer lockeren Einheit; Beine blieben frisch."
    else:  # easy_run / base
        if z1_z2_pct >= 80.0 and avg_hr <= z2_max:
            status = "erfüllt"
            status_label = "Erfüllt"
            target_comparison = (
                f"{z1_z2_pct}% in Zone 1/2 bei Ø {int(round(avg_hr))} bpm (Ziel: >= 80% < {z2_max} bpm). "
                "Aerobes Fundament optimal gepflegt."
            )
        elif z1_z2_pct >= 60.0 or avg_hr <= z2_max + 3:
            status = "teilweise"
            status_label = "Teilweise erfüllt"
            target_comparison = (
                f"{z1_z2_pct}% in Zone 1/2 mit {format_seconds_to_min_sec(z3_sec)} in Zone 3 (Drift an Hügeln/im Schlussteil)."
            )
        else:
            status = "abweichend"
            status_label = "Abweichend"
            target_comparison = (
                f"Lauf war mit {z1_z2_pct}% Z1/Z2 und Ø {int(round(avg_hr))} bpm flotter als geplant. "
                "Erfordert zusätzliche Regeneration."
            )

    return {
        "planned_slot": planned_slot,
        "intent_source": intent_source,
        "intended_type": intended_type,
        "status": status,
        "status_label": status_label,
        "intervals_icu_compliance": intervals_icu_compliance,
        "target_comparison": target_comparison,
    }


def generate_actionable_optimizations(
    metrics: Dict[str, Any], intent: Dict[str, Any]
) -> List[Dict[str, Any]]:
    """Generates deterministic Stage 3 optimizations based on physiological telemetry."""
    optimizations = []
    z3_sec = metrics.get("zone_breakdown", {}).get("z3", {}).get("seconds", 0)
    z4_sec = metrics.get("zone_breakdown", {}).get("z4", {}).get("seconds", 0)
    cadence = metrics.get("cadence_spm", 0.0)
    max_hr = metrics.get("max_hr", 0)
    intended_type = intent.get("intended_type", "easy_run")

    # 1. Pacing optimization for threshold/intervals
    if intended_type in ("threshold", "tempo_run", "intervals"):
        if max_hr >= 173 and z3_sec >= 480:
            optimizations.append(
                {
                    "category": "pacing",
                    "title": "Intervall-Einstieg über Ziel-Pace steuern",
                    "text": (
                        "Nicht auf den trägen Puls warten: Die ersten 2 Minuten jedes Intervalls starr nach Ziel-Pace "
                        "(05:45–06:00 /km) anlaufen. Der Puls zieht nach 2–3 Minuten von selbst in Zone 4 nach."
                    ),
                }
            )

        # 2. Recovery walk optimization
        if z3_sec >= 600 or any(p.get("avg_hr", 0) >= 155 for p in metrics.get("parsed_intervals", []) if p.get("role") == "recovery"):
            optimizations.append(
                {
                    "category": "recovery",
                    "title": "Gehpausen zur Pulsabsenkung nutzen",
                    "text": (
                        "Bleibt der Puls in den Trabpausen über 155–160 bpm hängen: 60–90 Sekunden zügiges Gehen (Power Walk) "
                        "einlegen, um den Parasympathikus zu aktivieren, bevor leicht weitergetrabt wird."
                    ),
                }
            )

        # 3. Alternative micro-threshold proposal
        if z3_sec >= 0.75 * max(z4_sec, 1) and z4_sec >= 600:
            optimizations.append(
                {
                    "category": "alternative_workout",
                    "title": "Alternative: 5x 4m Micro-Threshold",
                    "text": (
                        "Falls 3x 8m am Stück zu viel thermische Drift erzeugt: 5x 4m mit 90s Gehpause "
                        "ermöglicht die gleiche Netto-Reizzeit (20m Z4) bei stabilerem Laktatabbau."
                    ),
                    "cli_command": "./run plan --type micro_threshold --date tomorrow",
                }
            )
    else:  # easy base run
        if z3_sec > 300 or cadence < 155.0:
            optimizations.append(
                {
                    "category": "cadence",
                    "title": "Kadenzökonomie & Gehpausen an Anstiegen",
                    "text": (
                        "Schrittfrequenz durch kürzere Schritte behutsam Richtung 158–165 spm steigern und an Anstiegen "
                        "konsequent ins zügige Gehen wechseln, bevor der Puls über 155 bpm klettert."
                    ),
                }
            )

    if not optimizations:
        optimizations.append(
            {
                "category": "maintenance",
                "title": "Saubere Reizausführung",
                "text": "Keine akuten physiologischen Fehlbelastungen festgestellt. Die Reizdosierung war harmonisch.",
            }
        )

    return optimizations


def resolve_next_slot(
    metrics: Dict[str, Any],
    athlete_profile: Optional[Dict[str, Any]] = None,
    intent: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    """Resolves the next chronological weekly slot based on run date or planned slot."""
    run_date_str = str(metrics.get("date") or "")[:10]
    weekday_name = ""
    if run_date_str:
        try:
            d = datetime.date.fromisoformat(run_date_str)
            weekday_name = d.strftime("%A").lower()
        except Exception:
            weekday_name = ""

    slots = (athlete_profile or {}).get("athlete", {}).get("weekly_slots", [])

    if weekday_name in ("monday", "tuesday"):
        curr_slot_num = 1
    elif weekday_name in ("wednesday", "thursday", "friday"):
        curr_slot_num = 2
    elif weekday_name in ("saturday", "sunday"):
        curr_slot_num = 3
    else:
        planned = str((intent or {}).get("planned_slot", "")).lower()
        if "slot 1" in planned:
            curr_slot_num = 1
        elif "slot 2" in planned:
            curr_slot_num = 2
        elif "slot 3" in planned:
            curr_slot_num = 3
        else:
            curr_slot_num = 2

    next_slot_num = 1 if curr_slot_num >= 3 else (curr_slot_num + 1)

    matched_next = None
    for s in slots:
        if (s.get("slot") or s.get("id")) == next_slot_num:
            matched_next = s
            break

    if matched_next:
        slot_name = matched_next.get("name", f"Slot {next_slot_num}")
        role = matched_next.get("role", "flexible")
    else:
        if next_slot_num == 1:
            slot_name = "Slot 1 (Wochenanfang – Tempo/Intervalle)"
            role = "quality"
        elif next_slot_num == 2:
            slot_name = "Slot 2 (Wochenmitte – Flexibel)"
            role = "flexible"
        else:
            slot_name = "Slot 3 (Wochenende – Aerober Anker)"
            role = "base_anchor"

    default_workout_type = "easy_run"
    if role == "quality":
        default_workout_type = "intervals"
    elif role == "base_anchor":
        default_workout_type = "easy_run"
    else:
        default_workout_type = "easy_run"

    duration = 50 if role == "base_anchor" else (35 if role == "quality" else 45)

    return {
        "slot_id": next_slot_num,
        "next_slot": slot_name,
        "next_workout_type": default_workout_type,
        "duration_min": duration,
        "role": role,
    }


def determine_post_run_recovery(
    metrics: Dict[str, Any],
    athlete_profile: Optional[Dict[str, Any]] = None,
    intent: Optional[Dict[str, Any]] = None,
    optimizations: Optional[List[Dict[str, Any]]] = None,
) -> Dict[str, Any]:
    """Generates Stage 4 recovery window and directive integrating Stage 3 optimizations."""
    intensity_class = metrics.get("intensity_class", "aerobe_basis")
    training_load = metrics.get("training_load", 0.0)
    tsb_at_run = metrics.get("tsb_at_run")

    hr_profile = (athlete_profile or {}).get("athlete", {}).get("heart_rate", {})
    z2_max = int(hr_profile.get("zones", {}).get("z2_aerobic", {}).get("max") or 158)

    is_quality = (
        intensity_class in ("schwellenlauf", "schwellen_intervall", "tempo_z3")
        or training_load >= 55.0
    )

    recovery_hours = 48 if is_quality else 24

    tsb_str = f"TSB {tsb_at_run:+.1f}" if tsb_at_run is not None else "TSB neutral"
    ctl = metrics.get("icu_ctl")
    atl = metrics.get("icu_atl")
    if ctl is not None and atl is not None:
        tsb_context = f"{tsb_str} (CTL {ctl:.1f} / ATL {atl:.1f})"
    else:
        tsb_context = tsb_str

    next_slot_info = resolve_next_slot(metrics, athlete_profile=athlete_profile, intent=intent)
    next_slot = next_slot_info["next_slot"]
    next_workout_type = next_slot_info["next_workout_type"]
    duration_min = next_slot_info["duration_min"]

    # Synthesize insights from Stage 3 optimizations
    opt_list = optimizations or []
    alt_opt = next((o for o in opt_list if o.get("category") == "alternative_workout"), None)
    cadence_opt = next((o for o in opt_list if o.get("category") == "cadence"), None)
    recovery_opt = next((o for o in opt_list if o.get("category") == "recovery"), None)
    pacing_opt = next((o for o in opt_list if o.get("category") == "pacing"), None)

    focus_title = ""
    directive_parts = []

    if is_quality:
        directive_parts.append(f"Mindestens 48h Erholung ohne intensive Spitzen ({tsb_context}).")
    else:
        directive_parts.append(f"Erholung verläuft planmäßig ({recovery_hours}h Erholungsfenster).")

    if alt_opt:
        focus_title = alt_opt.get("title", "")
        directive_parts.append(
            f"Nächste Einheit: {next_slot}. Für die nächste Qualitätseinheit empfohlen: "
            f"3x 8m durch 5x 4m Micro-Threshold ersetzen, um thermische Drift zu reduzieren."
        )
    elif cadence_opt:
        focus_title = cadence_opt.get("title", "")
        directive_parts.append(
            f"Nächste Einheit: {next_slot}. Fokus auf Kadenzökonomie "
            f"(Schrittfrequenz Richtung 158–165 spm) und Gehpausen an Anstiegen konsequent nutzen."
        )
    elif recovery_opt:
        focus_title = recovery_opt.get("title", "")
        directive_parts.append(
            f"Nächste Einheit: {next_slot}. Bei Temporeizen in den Trabpausen "
            f"konsequente 60–90s Gehpausen einlegen, um den Parasympathikus zu aktivieren."
        )
    elif pacing_opt:
        focus_title = pacing_opt.get("title", "")
        directive_parts.append(
            f"Nächste Einheit: {next_slot}. Bei Intervallen die ersten 2 Minuten "
            f"starr nach Pace anlaufen und nicht auf den trägen Puls warten."
        )
    else:
        if is_quality:
            directive_parts.append(
                f"Nächste Einheit: {next_slot} strictly < {z2_max} bpm, Pace ignorieren."
            )
        else:
            directive_parts.append(
                f"Nächste Einheit ({next_slot}) im Wochenrhythmus fortführen."
            )

    return {
        "recovery_hours": recovery_hours,
        "tsb_context": tsb_context,
        "next_slot": next_slot,
        "next_workout_type": next_workout_type,
        "target_hr_max": z2_max,
        "duration_min": duration_min,
        "directive": " ".join(directive_parts),
        "optimization_focus": focus_title,
    }


def evaluate_run_deep(
    metrics: Dict[str, Any],
    athlete_profile: Optional[Dict[str, Any]] = None,
    run_raw: Optional[Dict[str, Any]] = None,
    coach_history: Optional[List[Dict[str, Any]]] = None,
    run_intent: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    """Generate exhaustive 4-stage review while maintaining backward-compatible top-level fields."""
    hr_profile = (athlete_profile or {}).get("athlete", {}).get("heart_rate", {})
    z2_max = int(hr_profile.get("zones", {}).get("z2_aerobic", {}).get("max") or 158)
    lthr = int(hr_profile.get("lthr") or 178)

    z1_z2_pct = metrics["z1_z2_pct"]
    avg_hr = metrics["avg_hr"]
    max_hr = metrics["max_hr"]
    cadence = metrics["cadence_spm"]
    z_breakdown = metrics["zone_breakdown"]
    z1_str = z_breakdown.get("z1", {}).get("time_str", "0s")
    z2_str = z_breakdown.get("z2", {}).get("time_str", "0s")
    z3_str = z_breakdown.get("z3", {}).get("time_str", "0s")
    z4_str = z_breakdown.get("z4", {}).get("time_str", "0s")

    # Intent and compliance
    intent = run_intent or resolve_run_intent(
        run=run_raw or {"name": metrics.get("name")},
        metrics=metrics,
        athlete_profile=athlete_profile,
        coach_history=coach_history,
    )

    # Stage 1: Physiology
    stage1 = {
        "headline": f"{metrics['intensity_class'].replace('_', ' ').title()}: {metrics['distance_km']:.2f} km in {metrics['moving_time_str']}",
        "summary": (
            f"Herzfrequenz lag bei Ø {int(round(avg_hr))} bpm (Max: {max_hr} bpm). "
            f"Zonenverteilung: Z1/Z2 {z1_z2_pct}%, Z3 {z3_str}, Z4 {z4_str}. Training Load: {int(round(metrics['training_load']))}."
        ),
        "ef": metrics.get("aerobic_ef"),
        "tsb_at_run": metrics.get("tsb_at_run"),
        "parsed_intervals": metrics.get("parsed_intervals", []),
    }

    # Stage 2: Compliance
    stage2 = dict(intent)

    # Check for subjective athlete notes (cramps, muscle tightness, early termination)
    athlete_note = str(
        metrics.get("athlete_note")
        or (run_raw.get("athlete_note") if run_raw else "")
        or (run_raw.get("description") if run_raw else "")
        or ""
    ).strip()

    cramp_keywords = (
        "krampf", "krämpfe", "krampfen", "gekrampft", "wade", "waden",
        "zerrung", "schmerz", "beschwerde", "verhärtung", "verspannt",
        "cramp", "calf", "spasm", "tightness"
    )
    has_cramps = any(k in athlete_note.lower() for k in cramp_keywords)

    if has_cramps:
        stage2["compliance_notes"] = (
            f"Einheit kontrolliert angepasst/abgemildert wegen muskulärer Beschwerden ('{athlete_note}'). "
            "Vernünftige Schutzreaktion zum Verletzungsschutz."
        )
        stage2["target_comparison"] = (
            f"Wegen Wadenkrämpfen angepasst ('{athlete_note}'). "
            "Verletzungsschutz hatte Vorrang vor Intervallen."
        )
        if stage2.get("status") in ("verfehlt", "teilweise"):
            stage2["status_label"] = "Angepasst (Wadenbeschwerden)"

    # Stage 3: Optimizations
    stage3 = generate_actionable_optimizations(metrics, intent)
    if has_cramps:
        stage3.insert(0, {
            "category": "recovery",
            "title": "Wadenschonung, Dehnen & Elektrolyte",
            "text": (
                f"Aufgetretene Wadenkrämpfe beachten ('{athlete_note}'): "
                "Gezielte Wadendehnung, Faszientraining, Magnesium-/Elektrolytausgleich. "
                "Mindestens 48 Stunden keine harten Schwellen- oder Intervallläufe."
            ),
            "actionable_advice": "Waden gezielt dehnen, Magnesium zuführen und 48h auf harte Intervalle verzichten.",
        })

    # Stage 4: Next run
    stage4 = determine_post_run_recovery(
        metrics,
        athlete_profile=athlete_profile,
        intent=intent,
        optimizations=stage3,
    )
    if has_cramps:
        stage4["recovery_hours"] = max(int(stage4.get("recovery_hours") or 24), 48)
        stage4["next_workout_type"] = "recovery_run"
        stage4["optimization_focus"] = "Wadenschonung & passive/aktive Erholung"
        stage4["directive"] = (
            f"Aufgrund aufgetretener Wadenkrämpfe ('{athlete_note}') stehen die nächsten 48h im Zeichen der Erholung. "
            "Kein Schwellentempo, Fokus auf Dehnen, Faszien und maximal lockeren Z1/Z2-Lauf."
        )

    # Derive top-level legacy fields
    intensity_class = metrics["intensity_class"]
    if intensity_class in ("schwellenlauf", "schwellen_intervall"):
        rating = "intensiv"
        rating_label = "Gezielter Schwellenlauf (Plan erfüllt)" if intent["status"] == "erfüllt" else "Schwellen- / Tempolauf"
        summary = (
            f"Intensive Belastung nahe der Laktatschwelle ({lthr} bpm). "
            f"{z4_str} in Zone 4 bei Ø {int(round(avg_hr))} bpm (Max: {max_hr} bpm). Training Load: {int(round(metrics['training_load']))}."
        )
        tip = "Wichtiger Reiz für Tempohärte – erfordert nun mindestens 48 Stunden lockere Regeneration."
    elif z1_z2_pct >= 85.0:
        rating = "optimal"
        rating_label = "Optimaler Grundlagenlauf"
        summary = (
            f"Hervorragende aerobe Zonendisziplin ({z1_z2_pct}% in Z1/Z2). "
            f"Die Herzfrequenz blieb mit Ø {int(round(avg_hr))} bpm verlässlich unter der Z2-Grenze ({z2_max} bpm). "
            f"Z1: {z1_str}, Z2: {z2_str}."
        )
        tip = "Genau dieser Intensitätsbereich stärkt die Kapillarisierung und den Fettstoffwechsel."
    elif z1_z2_pct >= 60.0 or intensity_class == "ueberwiegend_aerob":
        rating = "solide"
        rating_label = "Überwiegend aerob mit Z3-Drift"
        if avg_hr <= z2_max:
            hr_comment = f"Ø Puls lag mit {int(round(avg_hr))} bpm im Soll (< {z2_max} bpm), jedoch kletterte der Maximalpuls auf {max_hr} bpm."
        else:
            hr_comment = f"Ø Puls ({int(round(avg_hr))} bpm) und Maximalpuls ({max_hr} bpm) lagen leicht über der Z2-Obergrenze ({z2_max} bpm)."
        summary = (
            f"Solider Grundlagenschwerpunkt ({z1_z2_pct}% in Z1/Z2), jedoch {z3_str} in Zone 3 abgedriftet. "
            f"{hr_comment}"
        )
        tip = "An Steigungen oder in der zweiten Hälfte früher Tempo herausnehmen, um nicht in Zone 3 abzudriften."
    else:
        rating = "zuegig"
        rating_label = "Zügiger Dauerlauf (Zone 3)"
        if avg_hr > z2_max:
            hr_part = f"Ø Puls lag mit {int(round(avg_hr))} bpm über der Z2-Obergrenze ({z2_max} bpm)."
        else:
            hr_part = f"Spitzenpuls lag bei {max_hr} bpm mit {z3_str} in Zone 3."
        summary = (
            f"Zügiger Ausdauerlauf mit Schwerpunkt im Übergangsbereich ({z1_z2_pct}% in Z1/Z2, {z3_str} in Zone 3, {z4_str} in Zone 4). "
            f"{hr_part} Ein solider Reiz für das Herzschlagvolumen."
        )
        tip = "Dieser Reiz beansprucht die Glykogenspeicher spürbar – plane vor der nächsten tempo- oder schwellenbetonten Einheit 48 Stunden lockere Erholung ein."

    # Cadence feedback
    if cadence > 0:
        if cadence < 155.0:
            cadence_note = (
                f"Ø {int(round(cadence))} spm ist etwas niedrig. Schrittfrequenz mittelfristig "
                "durch kürzere Schritte behutsam Richtung 158–165 spm steigern."
            )
        elif cadence <= 172.0:
            cadence_note = f"Ø {int(round(cadence))} spm liegt im soliden, ökonomischen Freizeit-Bereich."
        else:
            cadence_note = f"Ø {int(round(cadence))} spm zeugt von hoher Schrittfrequenz und guter Fußarbeit."
    else:
        cadence_note = "Keine Schrittfrequenz-Sensordaten vorhanden."

    return {
        "schema_version": 2,
        "rating": rating,
        "rating_label": rating_label,
        "summary": summary,
        "coach_tip": tip,
        "cadence_feedback": cadence_note,
        "athlete_note": athlete_note if athlete_note else None,
        "zone_distribution_text": f"Z1: {z1_str} | Z2: {z2_str} | Z3: {z3_str} | Z4: {z4_str}",
        "stages": {
            "stage1_physiology": stage1,
            "stage2_compliance": stage2,
            "stage3_optimizations": stage3,
            "stage4_next_run": stage4,
        },
    }


def load_run_reviews(file_path: Optional[Path] = None) -> Dict[str, Any]:
    """Load persistent per-run review cache from JSON file."""
    path = file_path or (PROJECT_ROOT / "data" / "run_reviews.json")
    if not path.is_file():
        return {}
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
            return data if isinstance(data, dict) else {}
    except Exception:
        return {}


def save_run_reviews(reviews: Dict[str, Any], file_path: Optional[Path] = None) -> None:
    """Save updated per-run reviews to persistent cache."""
    path = file_path or (PROJECT_ROOT / "data" / "run_reviews.json")
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(reviews, f, ensure_ascii=False, indent=2)


def find_unreviewed_runs(
    runs: List[Dict[str, Any]],
    reviews: Dict[str, Any],
    limit: Optional[int] = None,
    recent: Optional[int] = None,
    force: bool = False,
) -> List[Dict[str, Any]]:
    """Return runs that do not yet have a cached review or require upgrade to schema_version 2."""
    sorted_runs = sorted(runs, key=lambda r: str(r.get("start_date_local") or ""), reverse=True)
    if recent is not None and recent > 0:
        sorted_runs = sorted_runs[:recent]

    unreviewed = []
    for r in sorted_runs:
        run_id = str(r.get("id"))
        cached_entry = reviews.get(run_id)
        needs_upgrade = (
            cached_entry is None
            or cached_entry.get("review", {}).get("schema_version") != 2
        )
        if force or needs_upgrade:
            unreviewed.append(r)

    if limit is not None and limit > 0:
        return unreviewed[:limit]
    return unreviewed


def review_runs(
    activities_file: Path,
    reviews_file: Optional[Path] = None,
    athlete_file: Optional[Path] = None,
    history_file: Optional[Path] = None,
    limit: Optional[int] = None,
    recent: Optional[int] = None,
    force: bool = False,
) -> Dict[str, Any]:
    """Incrementally review unreviewed runs and persist into review cache."""
    activities = load_activities(activities_file)
    runs = filter_runs(activities)
    profile = load_athlete_profile(athlete_file)
    hist_path = history_file or (PROJECT_ROOT / "data" / "coach_history.json")
    history = load_coach_history(hist_path) if hist_path.is_file() else []

    reviews_path = reviews_file or (PROJECT_ROOT / "data" / "run_reviews.json")
    reviews = load_run_reviews(reviews_path)

    unreviewed = find_unreviewed_runs(runs, reviews, limit=limit, recent=recent, force=force)
    now_iso = datetime.datetime.now().isoformat()

    added_count = 0
    for run in unreviewed:
        run_id = str(run.get("id"))
        metrics = extract_deep_run_metrics(run, profile)
        intent = resolve_run_intent(run, metrics, profile, history)
        evaluation = evaluate_run_deep(metrics, profile, run_raw=run, coach_history=history, run_intent=intent)

        reviews[run_id] = {
            "id": run_id,
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
            "athlete_note": evaluation.get("athlete_note") or metrics.get("athlete_note") or None,
            "review": evaluation,
            "analyzed_at": now_iso,
        }
        added_count += 1

    if added_count > 0:
        save_run_reviews(reviews, reviews_path)

    return reviews


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Extract deep physiological run metrics and maintain per-run 4-stage review cache."
    )
    parser.add_argument(
        "--input",
        "-i",
        type=Path,
        default=PROJECT_ROOT / "data" / "intervals_activities.json",
        help="Path to activity JSON file",
    )
    parser.add_argument(
        "--reviews",
        "-r",
        type=Path,
        default=PROJECT_ROOT / "data" / "run_reviews.json",
        help="Path to per-run reviews JSON cache",
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
        help="Path to coach history JSON file",
    )
    parser.add_argument(
        "--limit",
        "-l",
        type=int,
        default=None,
        help="Limit number of unanalyzed runs to review",
    )
    parser.add_argument(
        "--recent",
        type=int,
        default=None,
        help="Only consider the N most recent runs from activity history (e.g. 10)",
    )
    parser.add_argument(
        "--force",
        "-f",
        action="store_true",
        help="Force re-evaluation of runs even if already cached",
    )

    args = parser.parse_args()

    if not args.input.is_file():
        print(f"Error: Activities file not found: {args.input}", file=sys.stderr)
        return 1

    initial_reviews = load_run_reviews(args.reviews)
    activities = load_activities(args.input)
    runs = filter_runs(activities)
    unreviewed = find_unreviewed_runs(
        runs, initial_reviews, limit=args.limit, recent=args.recent, force=args.force
    )

    print(f"Total runs: {len(runs)} | Cached reviews: {len(initial_reviews)} | Unreviewed to process: {len(unreviewed)}")

    if not unreviewed:
        print("All matching runs already reviewed with schema_version 2. Cache is up to date.")
        return 0

    updated_reviews = review_runs(
        activities_file=args.input,
        reviews_file=args.reviews,
        athlete_file=args.athlete,
        history_file=args.history,
        limit=args.limit,
        recent=args.recent,
        force=args.force,
    )

    print(f"Successfully processed {len(unreviewed)} runs into cache: {args.reviews}")
    for run in unreviewed[:5]:
        rid = str(run.get("id"))
        rev = updated_reviews.get(rid, {})
        r_info = rev.get("review", {})
        st = r_info.get("stages", {})
        st2 = st.get("stage2_compliance", {})
        print(f"- {rev.get('date')} | {rev.get('name')}: [{st2.get('status_label', 'Review')}] {r_info.get('rating_label')} ({rev.get('z1_z2_pct')}% Z1/Z2)")

    return 0


if __name__ == "__main__":
    sys.exit(main())
