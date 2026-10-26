#!/usr/bin/env python3
"""extract_markers.py

Extracts objective physiological, readiness, and periodization telemetry markers
into a structured JSON payload for cognitive workout planning and guardrail evaluation.
Follows the 4-stage adaptive planning specification:
docs/plans/marker_basierte_trainingsplanung_spezifikation.md
"""

from __future__ import annotations

import argparse
import datetime
import json
from pathlib import Path
from typing import Any, Dict, List, Optional


def load_json_file(file_path: Path) -> Any:
    """Safely loads a JSON file or returns an empty container on missing/corrupt file."""
    if not file_path.is_file():
        return None
    try:
        with open(file_path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def is_quality_run(run: Dict[str, Any]) -> bool:
    """Classifies if a run was a quality session (intervals, tempo, threshold, speed, race)."""
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


def get_readiness_markers(
    wellness_records: Optional[List[Dict[str, Any]]],
    activities: List[Dict[str, Any]],
    ref_date: datetime.date,
) -> Dict[str, Any]:
    """Computes readiness, fatigue (TSB/ATL/CTL), HRV, resting HR and sleep telemetry."""
    records = wellness_records or []
    # Filter records on or before ref_date
    dated_records: List[Dict[str, Any]] = []
    for w in records:
        w_id = str(w.get("id") or "")[:10]
        if w_id:
            try:
                rec_date = datetime.date.fromisoformat(w_id)
                if rec_date <= ref_date:
                    dated_records.append(w)
            except ValueError:
                continue

    dated_records.sort(key=lambda x: str(x.get("id") or "")[:10], reverse=True)
    latest_w = dated_records[0] if dated_records else {}

    # Calculate 30-day baseline resting HR
    recent_rhr = [
        float(w["restingHR"])
        for w in dated_records[:30]
        if w.get("restingHR") is not None
    ]
    rhr_baseline = round(sum(recent_rhr) / len(recent_rhr), 1) if recent_rhr else 48.0

    sleep_secs = latest_w.get("sleepSecs")
    sleep_hours = round(float(sleep_secs) / 3600.0, 2) if sleep_secs is not None else 7.5
    sleep_score = float(latest_w.get("sleepScore")) if latest_w.get("sleepScore") is not None else 80.0
    resting_hr_today = int(latest_w.get("restingHR")) if latest_w.get("restingHR") is not None else int(rhr_baseline)
    hrv_today = round(float(latest_w["hrv"]), 1) if latest_w.get("hrv") is not None else 70.0

    # TSB / ATL / CTL from latest wellness or activities
    ctl = float(latest_w.get("ctl")) if latest_w.get("ctl") is not None else None
    atl = float(latest_w.get("atl")) if latest_w.get("atl") is not None else None

    if ctl is None or atl is None:
        # Fallback to activities icu_ctl/icu_atl
        runs = [
            a for a in activities
            if a.get("type") == "Run" and str(a.get("start_date_local") or a.get("date") or "")[:10] <= ref_date.isoformat()
        ]
        runs.sort(key=lambda a: str(a.get("start_date_local") or a.get("date") or ""), reverse=True)
        if runs and runs[0].get("icu_ctl") is not None and runs[0].get("icu_atl") is not None:
            ctl = float(runs[0]["icu_ctl"])
            atl = float(runs[0]["icu_atl"])
        else:
            ctl = 13.0
            atl = 15.0

    tsb = round(ctl - atl, 1)

    return {
        "tsb": tsb,
        "atl": round(atl, 1),
        "ctl": round(ctl, 1),
        "sleep_hours_last_night": sleep_hours,
        "sleep_score_last_night": sleep_score,
        "resting_hr_today": resting_hr_today,
        "resting_hr_baseline": rhr_baseline,
        "hrv_today": hrv_today,
    }


def get_run_history_markers(
    activities: List[Dict[str, Any]],
    ref_date: datetime.date,
) -> Dict[str, Any]:
    """Computes history of recent runs, quality stimulus spacing, and weekly volume."""
    runs = [
        a for a in activities
        if a.get("type") == "Run" and str(a.get("start_date_local") or a.get("date") or "")[:10] <= ref_date.isoformat()
    ]
    runs.sort(key=lambda a: str(a.get("start_date_local") or a.get("date") or ""), reverse=True)

    if not runs:
        return {
            "days_since_last_run": 999,
            "days_since_last_quality": 999,
            "last_run": None,
            "last_quality_run": None,
            "weekly_runs_completed": 0,
            "target_runs_per_week": 3,
            "last_week_total_km": 0.0,
            "last_week_z1_z2_pct": 0.0,
        }

    # Latest run
    latest = runs[0]
    latest_date_str = str(latest.get("start_date_local") or latest.get("date") or "")[:10]
    latest_date = datetime.date.fromisoformat(latest_date_str) if latest_date_str else ref_date
    days_since_last_run = max(0, (ref_date - latest_date).days)

    moving_time_s = float(latest.get("moving_time") or latest.get("elapsed_time") or 0.0)
    distance_m = float(latest.get("distance") or 0.0)
    z_times = latest.get("icu_hr_zone_times") or []
    z1_z2_s = sum(z_times[:2]) if len(z_times) >= 2 else 0
    total_z_s = sum(z_times) if z_times else 0
    z1_z2_pct = round((z1_z2_s / total_z_s) * 100.0, 1) if total_z_s > 0 else 0.0

    last_run_summary = {
        "date": latest_date_str,
        "type": "easy_run" if not is_quality_run(latest) else "quality_run",
        "name": latest.get("name", "Lauf"),
        "distance_km": round(distance_m / 1000.0, 2),
        "duration_min": round(moving_time_s / 60.0, 1),
        "avg_hr": round(float(latest.get("average_heartrate") or 0.0), 1),
        "load": round(float(latest.get("icu_training_load") or latest.get("training_load") or 0.0), 1),
        "z1_z2_pct": z1_z2_pct,
    }

    # Last quality run
    last_quality_summary = None
    days_since_last_quality = 999
    for r in runs:
        if is_quality_run(r):
            q_date_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
            if q_date_str:
                q_date = datetime.date.fromisoformat(q_date_str)
                days_since_last_quality = max(0, (ref_date - q_date).days)
                q_z_times = r.get("icu_hr_zone_times") or []
                z4_min = round((q_z_times[3] if len(q_z_times) > 3 else 0) / 60.0, 1)
                z5_min = round(sum(q_z_times[4:]) / 60.0, 1) if len(q_z_times) > 4 else 0.0
                q_type = "intervals" if "interval" in str(r.get("name", "")).lower() else "tempo_run"
                last_quality_summary = {
                    "date": q_date_str,
                    "type": q_type,
                    "name": r.get("name", "Quality Run"),
                    "distance_km": round(float(r.get("distance") or 0.0) / 1000.0, 2),
                    "z4_minutes": z4_min,
                    "z5_minutes": z5_min,
                    "load": round(float(r.get("icu_training_load") or 0.0), 1),
                }
                break

    # Calculate current ISO week runs and previous week volume
    ref_cal = ref_date.isocalendar()
    current_year, current_week = ref_cal.year, ref_cal.week
    runs_this_week = 0
    last_week_dist_m = 0.0
    last_week_z1_z2_s = 0.0
    last_week_total_z_s = 0.0

    prev_week = current_week - 1 if current_week > 1 else 52
    prev_year = current_year if current_week > 1 else current_year - 1

    for r in runs:
        d_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
        if not d_str:
            continue
        try:
            d = datetime.date.fromisoformat(d_str)
            d_cal = d.isocalendar()
            if d_cal.year == current_year and d_cal.week == current_week:
                runs_this_week += 1
            elif d_cal.year == prev_year and d_cal.week == prev_week:
                last_week_dist_m += float(r.get("distance") or 0.0)
                rz = r.get("icu_hr_zone_times") or []
                if rz:
                    last_week_z1_z2_s += sum(rz[:2]) if len(rz) >= 2 else 0
                    last_week_total_z_s += sum(rz)
        except ValueError:
            continue

    last_week_z_pct = (
        round((last_week_z1_z2_s / last_week_total_z_s) * 100.0, 1)
        if last_week_total_z_s > 0
        else 0.0
    )

    return {
        "days_since_last_run": days_since_last_run,
        "days_since_last_quality": days_since_last_quality,
        "last_run": last_run_summary,
        "last_quality_run": last_quality_summary,
        "weekly_runs_completed": runs_this_week,
        "target_runs_per_week": 3,
        "last_week_total_km": round(last_week_dist_m / 1000.0, 2),
        "last_week_z1_z2_pct": last_week_z_pct,
    }


def get_periodization_markers(
    target_date: datetime.date,
    athlete_profile: Dict[str, Any],
) -> Dict[str, Any]:
    """Matches target calendar day against athlete periodization slots (Slot 1, 2, or 3)."""
    target_day_name = target_date.strftime("%A").lower()
    slots = athlete_profile.get("athlete", {}).get("weekly_slots", [])

    matched_slot = None
    for s in slots:
        if target_day_name in [d.lower() for d in s.get("allowed_days", [])]:
            matched_slot = s
            break

    if not matched_slot:
        # Default fallback
        return {
            "target_day_name": target_day_name,
            "slot_id": 2,
            "slot_name": "Flexible Slot",
            "allowed_types": ["easy_run", "tempo_run"],
            "preferred_type": "easy_run",
            "slot_purpose": "Flexible aerobe Basis oder Erhaltung",
        }

    return {
        "target_day_name": target_day_name,
        "slot_id": matched_slot.get("slot", 1),
        "slot_name": matched_slot.get("name", "Slot"),
        "role": matched_slot.get("role", "flexible"),
        "allowed_types": matched_slot.get("allowed_types", ["easy_run"]),
        "preferred_type": matched_slot.get("preferred_type", "easy_run"),
        "slot_purpose": matched_slot.get("description", ""),
    }


def calculate_guardrails(
    readiness: Dict[str, Any],
    history: Dict[str, Any],
    periodization: Dict[str, Any],
) -> Dict[str, Any]:
    """Evaluates physiological boundaries to protect athlete against overtraining or excessive fatigue."""
    tsb = readiness.get("tsb", 0.0)
    sleep_h = readiness.get("sleep_hours_last_night", 8.0)
    sleep_score = readiness.get("sleep_score_last_night", 80.0)
    days_since_last_quality = history.get("days_since_last_quality", 999)
    days_since_last_run = history.get("days_since_last_run", 999)
    last_run = history.get("last_run") or {}
    last_duration = last_run.get("duration_min", 0.0)

    # 1. Quality spacing: at least 72h (3 days) between hard quality stimuli
    quality_discouraged = (days_since_last_quality < 3) or (tsb < -8.0)

    # 2. Fatigue / sleep warning: sleep deficit or acute exhaustion
    fatigue_warning = (sleep_h < 6.0) or (sleep_score < 72.0) or (tsb < -10.0)

    # 3. Rest day recommended if athlete ran today (0 days ago)
    rest_day_recommended = (days_since_last_run <= 0)

    # 4. Volume spike alert: last run exceeded 60 minutes
    volume_spike_alert = (last_duration >= 60.0) and (days_since_last_run <= 1)

    # 5. Slot allows threshold: slot must allow tempo_run AND quality is not discouraged
    allowed_types = periodization.get("allowed_types", [])
    slot_allows_threshold = ("tempo_run" in allowed_types) and (not quality_discouraged) and (not fatigue_warning)

    # Compile warnings list
    reasons: List[str] = []
    if quality_discouraged:
        reasons.append(f"Qualitätsreiz abgeraten: Letzte Qualität vor {days_since_last_quality} Tag(en) oder TSB {tsb} < -8.0")
    if fatigue_warning:
        reasons.append(f"Erhöhte Ermüdung: Schlaf {sleep_h:.1f}h (Score {sleep_score:.0f}) oder TSB {tsb}")
    if volume_spike_alert:
        reasons.append(f"Hohe Vorbelastung: Letzter Lauf dauerte {last_duration:.0f} Min.")
    if rest_day_recommended:
        reasons.append("Ruhetag empfohlen: Am selben Kalendertag wurde bereits gelaufen.")

    return {
        "quality_discouraged": quality_discouraged,
        "fatigue_warning": fatigue_warning,
        "rest_day_recommended": rest_day_recommended,
        "volume_spike_alert": volume_spike_alert,
        "slot_allows_threshold": slot_allows_threshold,
        "active_warnings": reasons,
    }


def extract_planning_markers(
    activities_file: Optional[Path] = None,
    wellness_file: Optional[Path] = None,
    profile_file: Optional[Path] = None,
    activities_data: Optional[List[Dict[str, Any]]] = None,
    wellness_data: Optional[List[Dict[str, Any]]] = None,
    profile_data: Optional[Dict[str, Any]] = None,
    ref_date: Optional[datetime.date] = None,
    target_date: Optional[datetime.date] = None,
    project_root: Optional[Path] = None,
) -> Dict[str, Any]:
    """Generates the full planning marker dictionary following the 4-stage specification."""
    root = project_root or Path(__file__).resolve().parent.parent.parent
    today = ref_date or datetime.date.today()
    planned_target = target_date or (today + datetime.timedelta(days=1))

    # Load data files or use provided in-memory datasets
    act_path = activities_file or (root / "data" / "intervals_activities.json")
    well_path = wellness_file or (root / "data" / "intervals_wellness.json")
    prof_path = profile_file or (root / "config" / "athlete.json")

    activities = activities_data if activities_data is not None else (load_json_file(act_path) or [])
    wellness = wellness_data if wellness_data is not None else (load_json_file(well_path) or [])
    profile = profile_data if profile_data is not None else (load_json_file(prof_path) or {})

    athlete_info = {
        "classification": profile.get("athlete", {}).get("classification", "Hobbyläufer"),
        "training_mode": profile.get("athlete", {}).get("training_mode", "flexi_hybrid"),
        "z2_max_hr": profile.get("athlete", {}).get("heart_rate", {}).get("zones", {}).get("z2_aerobic", {}).get("max", 158),
        "z4_range": [
            profile.get("athlete", {}).get("heart_rate", {}).get("zones", {}).get("z4_subthreshold", {}).get("min", 169),
            profile.get("athlete", {}).get("heart_rate", {}).get("zones", {}).get("z4_subthreshold", {}).get("max", 177),
        ],
        "lthr": profile.get("athlete", {}).get("heart_rate", {}).get("lthr", 178),
    }

    readiness = get_readiness_markers(wellness, activities, today)
    history = get_run_history_markers(activities, today)
    periodization = get_periodization_markers(planned_target, profile)
    guardrails = calculate_guardrails(readiness, history, periodization)

    return {
        "reference_date": today.isoformat(),
        "target_date": planned_target.isoformat(),
        "athlete": athlete_info,
        "readiness": readiness,
        "history": history,
        "periodization": periodization,
        "guardrails": guardrails,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Extract objective planning markers and guardrails.")
    parser.add_argument("--ref-date", help="Reference date (YYYY-MM-DD, defaults to today)")
    parser.add_argument("--target-date", help="Target planning date (YYYY-MM-DD, defaults to tomorrow)")
    parser.add_argument("--json", action="store_true", help="Print markers as raw JSON")
    args = parser.parse_args()

    ref = datetime.date.fromisoformat(args.ref_date) if args.ref_date else None
    target = datetime.date.fromisoformat(args.target_date) if args.target_date else None

    markers = extract_planning_markers(ref_date=ref, target_date=target)

    if args.json:
        print(json.dumps(markers, indent=2, ensure_ascii=False))
        return

    print("=" * 65)
    print("  Marker- & Telemetrie-Extraktor für Trainingsplanung")
    print("=" * 65)
    print(f"Stichtag (Heute) : {markers['reference_date']}")
    print(f"Planungsziel     : {markers['target_date']} ({markers['periodization']['target_day_name'].capitalize()})")
    print(f"Zugeordneter Slot: {markers['periodization']['slot_name']} (ID {markers['periodization']['slot_id']})")
    print(f"Slot-Fokus       : {markers['periodization']['slot_purpose']}")
    print("-" * 65)
    print("Erholungs- & Readiness-Werte:")
    r = markers["readiness"]
    print(f"  TSB (Form)     : {r['tsb']} | ATL: {r['atl']} | CTL: {r['ctl']}")
    print(f"  Schlaf         : {r['sleep_hours_last_night']}h (Score: {r['sleep_score_last_night']})")
    print(f"  Ruhepuls / HRV : {r['resting_hr_today']} bpm (Schnitt: {r['resting_hr_baseline']}) | HRV: {r['hrv_today']} ms")
    print("-" * 65)
    h = markers["history"]
    print("Trainingshistorie:")
    print(f"  Tage seit letztem Lauf   : {h['days_since_last_run']} Tag(e)")
    print(f"  Tage seit letzter Qualität: {h['days_since_last_quality']} Tag(e)")
    if h["last_quality_run"]:
        lq = h["last_quality_run"]
        print(f"  Letzter Qualitätslauf    : {lq['date']} | {lq['name']} ({lq['type']}, Load {lq['load']})")
    print("-" * 65)
    g = markers["guardrails"]
    print("Guardrails & Leitplanken:")
    print(f"  Qualität abgeraten (quality_discouraged): {g['quality_discouraged']}")
    print(f"  Ermüdungswarnung (fatigue_warning)     : {g['fatigue_warning']}")
    print(f"  Ruhetag empfohlen (rest_day_rec)       : {g['rest_day_recommended']}")
    print(f"  Volumen-Spike (volume_spike_alert)     : {g['volume_spike_alert']}")
    print(f"  Schwelle erlaubt (slot_allows_threshold): {g['slot_allows_threshold']}")
    if g["active_warnings"]:
        print("  Warnhinweise:")
        for w in g["active_warnings"]:
            print(f"    ⚠️  {w}")
    print("=" * 65)


if __name__ == "__main__":
    main()
