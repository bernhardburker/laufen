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


def filter_activities_up_to_date(
    activities: List[Dict[str, Any]],
    ref_date: Optional[datetime.date],
) -> List[Dict[str, Any]]:
    """Filters activities so only entries with date <= ref_date are returned."""
    if ref_date is None:
        return list(activities)
    ref_iso = ref_date.isoformat()
    filtered: List[Dict[str, Any]] = []
    for a in activities:
        if not isinstance(a, dict):
            continue
        d_str = str(a.get("start_date_local") or a.get("date") or "")[:10]
        if not d_str:
            continue
        try:
            datetime.date.fromisoformat(d_str)
        except ValueError:
            continue
        if d_str <= ref_iso:
            filtered.append(a)
    return filtered


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


def has_muscular_or_health_complaint(run: Dict[str, Any]) -> bool:
    """Detects subjective muscular, cramp, pain, or illness notes attached to an activity."""
    note = str(run.get("description") or run.get("athlete_note") or "").lower()
    if not note:
        return False
    complaint_keywords = (
        "krampf",
        "krämpf",
        "schmerz",
        "zerrung",
        "verletz",
        "abgebrochen",
        "unwohl",
        "krank",
        "schwindel",
        "übel",
        "stechen",
    )
    return any(kw in note for kw in complaint_keywords)


def calculate_consecutive_build_weeks(
    runs: List[Dict[str, Any]],
    ref_date: datetime.date,
    min_load: float = 65.0,
    min_distance_m: float = 12000.0,
) -> int:
    """Calculates how many consecutive completed calendar weeks prior to ref_date were build weeks."""
    current_monday = ref_date - datetime.timedelta(days=ref_date.weekday())
    consecutive_build = 0

    for w in range(1, 5):
        week_start = current_monday - datetime.timedelta(weeks=w)
        week_end = week_start + datetime.timedelta(days=6)

        week_runs = []
        for r in runs:
            d_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
            if d_str:
                try:
                    run_d = datetime.date.fromisoformat(d_str)
                    if week_start <= run_d <= week_end:
                        week_runs.append(r)
                except Exception:
                    continue

        if not week_runs:
            break

        total_load = sum(float(r.get("training_load") or r.get("icu_training_load") or 0.0) for r in week_runs)
        total_dist = sum(float(r.get("distance") or 0.0) for r in week_runs)
        has_quality = any(is_quality_run(r) for r in week_runs)

        if has_quality and (total_load >= min_load or total_dist >= min_distance_m):
            consecutive_build += 1
        else:
            break

    return consecutive_build


def count_base_anchors_14d(
    runs: List[Dict[str, Any]],
    ref_date: datetime.date,
) -> int:
    """Counts completed clean aerobic base runs in the preceding 14 days."""
    count = 0
    start_14d = ref_date - datetime.timedelta(days=14)
    for r in runs:
        d_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
        if not d_str:
            continue
        try:
            d = datetime.date.fromisoformat(d_str)
            if start_14d <= d <= ref_date:
                if not is_quality_run(r) and not has_muscular_or_health_complaint(r):
                    dist_m = float(r.get("distance") or 0.0)
                    dur_s = float(r.get("moving_time") or r.get("elapsed_time") or 0.0)
                    if dist_m >= 4000.0 or dur_s >= 1500.0:
                        count += 1
        except Exception:
            continue
    return count


def get_calendar_context(
    calendar_events: Optional[List[Dict[str, Any]]],
    ref_date: datetime.date,
) -> Dict[str, Any]:
    """Extracts today's planned, completed, or rest notes from calendar events for planning context."""
    if not calendar_events:
        return {
            "has_calendar": False,
            "today_completed_workout": None,
            "today_planned_workout": None,
            "today_rest_note": None,
            "events_today_count": 0,
        }

    today_iso = ref_date.isoformat()
    events_today = [
        e for e in calendar_events
        if isinstance(e, dict) and str(e.get("start_date_local") or e.get("date") or "")[:10] == today_iso
    ]

    completed_workout = next(
        (e for e in events_today if e.get("category") == "WORKOUT" and e.get("paired_activity_id")),
        None,
    )
    planned_workout = next(
        (e for e in events_today if e.get("category") == "WORKOUT" and not e.get("paired_activity_id")),
        None,
    )
    rest_note = next(
        (
            e for e in events_today
            if (e.get("category") == "NOTE" and "ruhetag" in str(e.get("name") or "").lower())
            or e.get("category") == "HOLIDAY"
        ),
        None,
    )

    return {
        "has_calendar": True,
        "today_completed_workout": completed_workout,
        "today_planned_workout": planned_workout,
        "today_rest_note": rest_note,
        "events_today_count": len(events_today),
    }


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
    latest_id = str(latest_w.get("id") or "")[:10]
    is_today = (latest_id == ref_date.isoformat())
    has_sleep = (sleep_secs is not None and float(sleep_secs) > 0)
    sleep_is_today = bool(is_today and has_sleep)
    sleep_date = latest_id if latest_id else None

    if is_today and has_sleep:
        sleep_hours = round(float(sleep_secs) / 3600.0, 2)
        sleep_score = float(latest_w.get("sleepScore")) if latest_w.get("sleepScore") is not None else 80.0
    elif is_today and not has_sleep:
        # Today's record is present (e.g. resting HR synced), but sleep not yet synced.
        prev_w = dated_records[1] if len(dated_records) > 1 else {}
        prev_secs = prev_w.get("sleepSecs")
        sleep_hours = round(float(prev_secs) / 3600.0, 2) if prev_secs is not None else 7.5
        sleep_score = float(prev_w.get("sleepScore")) if prev_w.get("sleepScore") is not None else 80.0
        sleep_date = str(prev_w.get("id") or "")[:10] or None
    else:
        # Latest record is from a previous day
        sleep_hours = round(float(sleep_secs) / 3600.0, 2) if sleep_secs is not None else 7.5
        sleep_score = float(latest_w.get("sleepScore")) if latest_w.get("sleepScore") is not None else 80.0

    resting_hr_today = int(latest_w.get("restingHR")) if latest_w.get("restingHR") is not None else int(rhr_baseline)
    hrv_today = round(float(latest_w["hrv"]), 1) if latest_w.get("hrv") is not None else 70.0
    rhr_delta = round(resting_hr_today - rhr_baseline, 1)
    elevated_rhr = bool(rhr_delta >= 4.0)

    # Subjective wellness, injury & illness checks across latest 3 days
    soreness = int(latest_w["soreness"]) if latest_w.get("soreness") is not None else None
    fatigue = int(latest_w["fatigue"]) if latest_w.get("fatigue") is not None else None
    injury_desc: Optional[str] = None
    illness_comment: Optional[str] = None
    illness_keywords = (
        "krank",
        "infekt",
        "erkält",
        "fieber",
        "husten",
        "hals",
        "corona",
        "covid",
        "unwohl",
        "schlapp",
        "magen",
        "übel",
        "sick",
        "ill",
        "flu",
        "unwell",
    )
    for rec in dated_records[:3]:
        raw_inj = rec.get("injury")
        if raw_inj and str(raw_inj).strip().lower() not in ("none", "null", "0", "false") and not injury_desc:
            injury_desc = str(raw_inj).strip()
        comm = str(rec.get("comments") or "").strip()
        if comm and any(kw in comm.lower() for kw in illness_keywords) and not illness_comment:
            illness_comment = comm

    health_warning = bool(
        injury_desc
        or illness_comment
        or elevated_rhr
        or (soreness is not None and soreness >= 4)
        or (fatigue is not None and fatigue >= 4)
    )

    # TSB / ATL / CTL from latest wellness or activities
    ctl = float(latest_w.get("ctl")) if latest_w.get("ctl") is not None else None
    atl = float(latest_w.get("atl")) if latest_w.get("atl") is not None else None

    if ctl is None or atl is None:
        # Fallback to activities icu_ctl/icu_atl
        runs = [
            a for a in filter_activities_up_to_date(activities, ref_date)
            if a.get("type") == "Run"
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
        "sleep_is_today": sleep_is_today,
        "sleep_date": sleep_date,
        "resting_hr_today": resting_hr_today,
        "resting_hr_baseline": rhr_baseline,
        "rhr_delta": rhr_delta,
        "elevated_rhr": elevated_rhr,
        "hrv_today": hrv_today,
        "soreness": soreness,
        "fatigue": fatigue,
        "injury": injury_desc,
        "illness_comment": illness_comment,
        "health_warning": health_warning,
    }


def get_run_history_markers(
    activities: List[Dict[str, Any]],
    ref_date: datetime.date,
    target_date: Optional[datetime.date] = None,
    athlete_profile: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    """Computes history of recent runs, quality stimulus spacing, base anchor status, and weekly volume."""
    runs = [
        a for a in filter_activities_up_to_date(activities, ref_date)
        if a.get("type") == "Run"
    ]
    runs.sort(key=lambda a: str(a.get("start_date_local") or a.get("date") or ""), reverse=True)

    if not runs:
        return {
            "days_since_last_run": 999,
            "target_days_since_last_run": 999,
            "days_since_last_quality": 999,
            "target_days_since_last_quality": 999,
            "days_since_last_base_run": 999,
            "base_anchor_completed": True,
            "missed_anchor_days": [],
            "last_run": None,
            "last_quality_run": None,
            "weekly_runs_completed": 0,
            "target_runs_per_week": 3,
            "last_week_total_km": 0.0,
            "last_week_z1_z2_pct": 0.0,
            "consecutive_build_weeks": 0,
            "base_anchors_in_last_14d": 0,
        }

    # Latest run
    latest = runs[0]
    latest_date_str = str(latest.get("start_date_local") or latest.get("date") or "")[:10]
    latest_date = datetime.date.fromisoformat(latest_date_str) if latest_date_str else ref_date
    days_since_last_run = max(0, (ref_date - latest_date).days)
    target_days_since_last_run = max(0, (target_date - latest_date).days) if (target_date and latest_date_str) else days_since_last_run

    moving_time_s = float(latest.get("moving_time") or latest.get("elapsed_time") or 0.0)
    distance_m = float(latest.get("distance") or 0.0)
    z_times = latest.get("icu_hr_zone_times") or []
    z1_z2_s = sum(z_times[:2]) if len(z_times) >= 2 else 0
    total_z_s = sum(z_times) if z_times else 0
    z1_z2_pct = round((z1_z2_s / total_z_s) * 100.0, 1) if total_z_s > 0 else 0.0

    latest_note = str(latest.get("description") or latest.get("athlete_note") or "").strip() or None
    latest_has_complaint = has_muscular_or_health_complaint(latest)

    last_run_summary = {
        "date": latest_date_str,
        "type": "easy_run" if not is_quality_run(latest) else "quality_run",
        "name": latest.get("name", "Lauf"),
        "distance_km": round(distance_m / 1000.0, 2),
        "duration_min": round(moving_time_s / 60.0, 1),
        "avg_hr": round(float(latest.get("average_heartrate") or 0.0), 1),
        "load": round(float(latest.get("icu_training_load") or latest.get("training_load") or 0.0), 1),
        "z1_z2_pct": z1_z2_pct,
        "has_muscular_complaint": latest_has_complaint,
        "athlete_note": latest_note,
    }

    # Days since last clean aerobic base run
    days_since_last_base_run = 999
    for r in runs:
        if not is_quality_run(r) and not has_muscular_or_health_complaint(r):
            b_date_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
            if b_date_str:
                try:
                    b_date = datetime.date.fromisoformat(b_date_str)
                    days_since_last_base_run = max(0, (ref_date - b_date).days)
                    break
                except ValueError:
                    continue

    # Check if mandatory base anchor (e.g. Slot 3 weekend run) was completed in preceding cycle
    check_date = target_date or ref_date
    slots = (athlete_profile or {}).get("athlete", {}).get("weekly_slots", [])
    anchor_allowed_days = set()
    for s in slots:
        if s.get("role") == "base_anchor" or s.get("is_mandatory_base") or s.get("is_base_anchor"):
            for d_name in s.get("allowed_days", []):
                anchor_allowed_days.add(str(d_name).lower())

    base_anchor_completed = True
    missed_anchor_days: List[str] = []
    if anchor_allowed_days:
        recent_anchor_dates: List[datetime.date] = []
        for i in range(1, 8):
            past_d = check_date - datetime.timedelta(days=i)
            if past_d.strftime("%A").lower() in anchor_allowed_days:
                recent_anchor_dates.append(past_d)

        if recent_anchor_dates:
            min_anchor_d = min(recent_anchor_dates)
            anchor_done = False
            for r in runs:
                r_d_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
                if not r_d_str:
                    continue
                try:
                    r_d = datetime.date.fromisoformat(r_d_str)
                    if r_d in recent_anchor_dates and not has_muscular_or_health_complaint(r):
                        anchor_done = True
                        break
                    if min_anchor_d <= r_d <= ref_date and not is_quality_run(r) and not has_muscular_or_health_complaint(r):
                        anchor_done = True
                        break
                except ValueError:
                    continue
            base_anchor_completed = anchor_done
            if not anchor_done:
                missed_anchor_days = sorted(d.isoformat() for d in recent_anchor_dates)

    # Last quality run
    last_quality_summary = None
    days_since_last_quality = 999
    target_days_since_last_quality = 999
    for r in runs:
        if is_quality_run(r):
            q_date_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
            if q_date_str:
                q_date = datetime.date.fromisoformat(q_date_str)
                days_since_last_quality = max(0, (ref_date - q_date).days)
                target_days_since_last_quality = max(0, (target_date - q_date).days) if target_date else days_since_last_quality
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

    consecutive_build = calculate_consecutive_build_weeks(runs, ref_date)
    base_anchors_14d = count_base_anchors_14d(runs, ref_date)

    return {
        "days_since_last_run": days_since_last_run,
        "target_days_since_last_run": target_days_since_last_run,
        "days_since_last_quality": days_since_last_quality,
        "target_days_since_last_quality": target_days_since_last_quality,
        "days_since_last_base_run": days_since_last_base_run,
        "base_anchor_completed": base_anchor_completed,
        "missed_anchor_days": missed_anchor_days,
        "last_run": last_run_summary,
        "last_quality_run": last_quality_summary,
        "weekly_runs_completed": runs_this_week,
        "target_runs_per_week": 3,
        "last_week_total_km": round(last_week_dist_m / 1000.0, 2),
        "last_week_z1_z2_pct": last_week_z_pct,
        "consecutive_build_weeks": consecutive_build,
        "base_anchors_in_last_14d": base_anchors_14d,
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
            "role": "flexible",
            "allowed_types": ["easy_run", "tempo_run"],
            "preferred_type": "easy_run",
            "slot_purpose": "Flexible aerobe Basis oder Erhaltung",
            "requires_base_anchor": False,
        }

    return {
        "target_day_name": target_day_name,
        "slot_id": matched_slot.get("slot", 1),
        "slot_name": matched_slot.get("name", "Slot"),
        "role": matched_slot.get("role", "flexible"),
        "allowed_types": matched_slot.get("allowed_types", ["easy_run"]),
        "preferred_type": matched_slot.get("preferred_type", "easy_run"),
        "slot_purpose": matched_slot.get("description", ""),
        "requires_base_anchor": bool(matched_slot.get("requires_base_anchor", False)),
    }


def get_coach_context_markers(
    history_records: Optional[List[Dict[str, Any]]],
    activities: List[Dict[str, Any]],
    ref_date: datetime.date,
) -> Dict[str, Any]:
    """Extracts the active open coaching directive from coach_history.json and checks if it is still pending."""
    records = history_records if isinstance(history_records, list) else []
    open_cycle: Optional[Dict[str, Any]] = None
    evaluated_ids = set()

    for entry in records:
        if not isinstance(entry, dict):
            continue
        for eid in entry.get("evaluated_run_ids") or []:
            evaluated_ids.add(str(eid))
        if entry.get("status") == "offen":
            open_cycle = entry

    if not open_cycle:
        return {
            "has_open_cycle": False,
            "open_cycle_id": None,
            "open_cycle_date": None,
            "target_type": None,
            "target_hr_max": None,
            "recommendation": None,
            "runs_completed_since_cycle": 0,
            "unfulfilled_base_directive": False,
        }

    cycle_date_str = str(open_cycle.get("date") or "")[:10]
    target_type = str(open_cycle.get("target_type") or "easy_run").lower()
    target_hr_max = open_cycle.get("target_hr_max")
    recommendation = str(open_cycle.get("recommendation") or "")

    runs_since = 0
    for a in activities:
        if a.get("type") != "Run":
            continue
        a_id = str(a.get("id") or "")
        a_date = str(a.get("start_date_local") or a.get("date") or "")[:10]
        if not a_date or a_date > ref_date.isoformat():
            continue
        if a_id and a_id in evaluated_ids:
            continue
        if cycle_date_str and a_date >= cycle_date_str:
            runs_since += 1

    unfulfilled_base = bool(
        target_type in ("easy_run", "recovery_run", "rest_day", "long_run")
        and runs_since == 0
    )

    return {
        "has_open_cycle": True,
        "open_cycle_id": open_cycle.get("id"),
        "open_cycle_date": cycle_date_str,
        "target_type": target_type,
        "target_hr_max": target_hr_max,
        "recommendation": recommendation,
        "runs_completed_since_cycle": runs_since,
        "unfulfilled_base_directive": unfulfilled_base,
    }


def calculate_guardrails(
    readiness: Dict[str, Any],
    history: Dict[str, Any],
    periodization: Dict[str, Any],
    coach_context: Optional[Dict[str, Any]] = None,
    calendar_context: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    """Evaluates physiological boundaries to protect athlete against overtraining or excessive fatigue."""
    tsb = readiness.get("tsb", 0.0)
    sleep_h = readiness.get("sleep_hours_last_night", 8.0)
    sleep_score = readiness.get("sleep_score_last_night", 80.0)
    days_since_last_quality = history.get("days_since_last_quality", 999)
    days_since_last_run = history.get("days_since_last_run", 999)
    last_run = history.get("last_run") or {}
    last_duration = last_run.get("duration_min", 0.0)
    consec_build = int(history.get("consecutive_build_weeks", 0))

    # 1. Quality spacing: at least 72h (3 days) between hard quality stimuli
    quality_discouraged = (days_since_last_quality < 3) or (tsb < -8.0)

    # 2. Fatigue / sleep warning: sleep deficit or acute exhaustion
    fatigue_warning = (sleep_h < 6.0) or (sleep_score < 72.0) or (tsb < -10.0)

    # 3. Rest day recommended if athlete ran today (0 days ago) or completed workout today
    cal_ctx = calendar_context or {}
    calendar_completed = bool(cal_ctx.get("today_completed_workout"))
    calendar_rest_day = bool(cal_ctx.get("today_rest_note"))
    rest_day_recommended = (days_since_last_run <= 0) or calendar_completed

    # 4. Volume spike alert: last run exceeded 60 minutes
    volume_spike_alert = (last_duration >= 60.0) and (days_since_last_run <= 1)

    # 5. Health / muscular complaint warning (wellness illness/injury/elevated RHR or muscular complaint on last run)
    wellness_health_warn = bool(readiness.get("health_warning", False))
    last_run_complaint = bool(last_run.get("has_muscular_complaint", False))
    health_warning = wellness_health_warn or last_run_complaint

    # 6. Base anchor missing (target slot requires base anchor, but preceding weekend/anchor slot was missed)
    requires_base_anchor = bool(periodization.get("requires_base_anchor", False))
    base_anchor_completed = bool(history.get("base_anchor_completed", True))
    base_anchor_missing = requires_base_anchor and (not base_anchor_completed)

    # 7. Unfulfilled Coach base directive (e.g. open easy_run / Long Run recommendation in coach_history.json)
    c_ctx = coach_context or {}
    coach_directive_override = bool(c_ctx.get("unfulfilled_base_directive", False))

    # 8. Deload week & Re-entry benchmarks
    is_deload_week = bool(consec_build >= 3)
    reentry_recommended = bool(days_since_last_run >= 7)

    # 9. Slot allows threshold: slot must allow tempo_run AND no blocking guardrails
    allowed_types = periodization.get("allowed_types", [])
    slot_allows_threshold = (
        ("tempo_run" in allowed_types)
        and (not quality_discouraged)
        and (not fatigue_warning)
        and (not health_warning)
        and (not base_anchor_missing)
        and (not coach_directive_override)
        and (not is_deload_week)
        and (not reentry_recommended)
        and (not calendar_rest_day)
    )

    # Compile warnings list
    reasons: List[str] = []
    if quality_discouraged:
        reasons.append(f"Qualitätsreiz abgeraten: Letzte Qualität vor {days_since_last_quality} Tag(en) oder TSB {tsb} < -8.0")
    if fatigue_warning:
        reasons.append(f"Erhöhte Ermüdung: Schlaf {sleep_h:.1f}h (Score {sleep_score:.0f}) oder TSB {tsb}")
    if volume_spike_alert:
        reasons.append(f"Hohe Vorbelastung: Letzter Lauf dauerte {last_duration:.0f} Min.")
    if health_warning:
        if last_run_complaint:
            reasons.append(f"Muskel-/Beschwerdeschutz aktiv: Letzter Lauf ({last_run.get('date')}) verzeichnete Beschwerden/Krämpfe.")
        if readiness.get("injury"):
            reasons.append(f"Verletzungsschutz aktiv: {readiness.get('injury')}")
        if readiness.get("illness_comment"):
            reasons.append(f"Krankheits-/Unwohlsein-Warnung: {readiness.get('illness_comment')}")
        if readiness.get("elevated_rhr"):
            reasons.append(f"Erhöhter Ruhepuls: {readiness.get('resting_hr_today')} bpm (+{readiness.get('rhr_delta')} bpm über Baseline)")
    if base_anchor_missing:
        missed_str = ", ".join(history.get("missed_anchor_days") or [])
        reasons.append(
            f"Basis-Anker fehlt: Wochenend-/Grundlagenlauf ({missed_str or 'Slot 3'}) wurde ausgelassen – Z2-Fundament hat Vorrang vor Intervallen."
        )
    if coach_directive_override:
        cid = c_ctx.get("open_cycle_id") or "offen"
        cdate = c_ctx.get("open_cycle_date") or ""
        ctype = c_ctx.get("target_type") or "easy_run"
        reasons.append(
            f"Offene Coach-Vorgabe ({cid} vom {cdate}): '{ctype}' steht noch unvollendet aus."
        )
    if is_deload_week:
        reasons.append(
            f"Deload-Woche aktiv: {consec_build} aufeinanderfolgende Belastungswochen – aktive Superkompensation."
        )
    if reentry_recommended:
        reasons.append(
            f"Wiedereinstieg empfohlen: Letzter Lauf vor {days_since_last_run} Tag(en) – sanfter Einstieg (25–30 Min.)."
        )
    if calendar_rest_day:
        reasons.append("Kalender-Ruhetag: Im Kalender ist für heute explizit ein Ruhetag eingetragen.")
    if rest_day_recommended:
        reasons.append("Ruhetag empfohlen: Am selben Kalendertag wurde bereits gelaufen.")

    return {
        "quality_discouraged": quality_discouraged,
        "fatigue_warning": fatigue_warning,
        "rest_day_recommended": rest_day_recommended,
        "volume_spike_alert": volume_spike_alert,
        "health_warning": health_warning,
        "base_anchor_missing": base_anchor_missing,
        "coach_directive_override": coach_directive_override,
        "is_deload_week": is_deload_week,
        "reentry_recommended": reentry_recommended,
        "calendar_rest_day": calendar_rest_day,
        "slot_allows_threshold": slot_allows_threshold,
        "active_warnings": reasons,
    }


def find_heart_rate_zone(zones: Dict[str, Any], zone_num: int) -> Optional[Dict[str, Any]]:
    """Finds a heart rate zone definition by zone number or prefix matching."""
    if not isinstance(zones, dict):
        return None
    for key, z_data in zones.items():
        if isinstance(z_data, dict):
            if z_data.get("zone") == zone_num or key.startswith(f"z{zone_num}_") or key == f"z{zone_num}":
                return z_data
    return None


def extract_planning_markers(
    activities_file: Optional[Path] = None,
    wellness_file: Optional[Path] = None,
    profile_file: Optional[Path] = None,
    history_file: Optional[Path] = None,
    events_file: Optional[Path] = None,
    activities_data: Optional[List[Dict[str, Any]]] = None,
    wellness_data: Optional[List[Dict[str, Any]]] = None,
    profile_data: Optional[Dict[str, Any]] = None,
    history_data: Optional[List[Dict[str, Any]]] = None,
    events_data: Optional[List[Dict[str, Any]]] = None,
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
    sibling_dir = act_path.parent if activities_file else None
    well_path = wellness_file or (
        (sibling_dir / "intervals_wellness.json") if (sibling_dir and (sibling_dir / "intervals_wellness.json").is_file()) else (root / "data" / "intervals_wellness.json")
    )
    prof_path = profile_file or (
        (sibling_dir / "athlete.json") if (sibling_dir and (sibling_dir / "athlete.json").is_file()) else (root / "config" / "athlete.json")
    )
    hist_path = history_file or (
        (sibling_dir / "coach_history.json") if (sibling_dir and (sibling_dir / "coach_history.json").is_file()) else None
    )
    ev_path = events_file or (
        (sibling_dir / "calendar_events.json") if (sibling_dir and (sibling_dir / "calendar_events.json").is_file()) else None
    )

    raw_activities = activities_data if activities_data is not None else (load_json_file(act_path) or [])
    activities = filter_activities_up_to_date(raw_activities, today)
    wellness = wellness_data if wellness_data is not None else (load_json_file(well_path) or [])
    profile = profile_data if profile_data is not None else (load_json_file(prof_path) or {})
    coach_history = history_data if history_data is not None else (load_json_file(hist_path) if hist_path else [])
    calendar_events = events_data if events_data is not None else (load_json_file(ev_path) if ev_path else [])

    zones = profile.get("athlete", {}).get("heart_rate", {}).get("zones", {})
    z2 = find_heart_rate_zone(zones, 2) or {}
    z4 = find_heart_rate_zone(zones, 4) or {}

    athlete_info = {
        "classification": profile.get("athlete", {}).get("classification", "Hobbyläufer"),
        "training_mode": profile.get("athlete", {}).get("training_mode", "flexi_hybrid"),
        "z2_max_hr": int(z2.get("max", 158)),
        "z4_range": [
            int(z4.get("min", 169)),
            int(z4.get("max", 177)),
        ],
        "lthr": int(profile.get("athlete", {}).get("heart_rate", {}).get("lthr", 178)),
    }

    readiness = get_readiness_markers(wellness, activities, today)
    history = get_run_history_markers(activities, today, planned_target, athlete_profile=profile)
    periodization = get_periodization_markers(planned_target, profile)
    coach_context = get_coach_context_markers(coach_history, activities, today)
    calendar_context = get_calendar_context(calendar_events, today)
    guardrails = calculate_guardrails(
        readiness,
        history,
        periodization,
        coach_context=coach_context,
        calendar_context=calendar_context,
    )

    return {
        "reference_date": today.isoformat(),
        "target_date": planned_target.isoformat(),
        "athlete": athlete_info,
        "readiness": readiness,
        "history": history,
        "periodization": periodization,
        "coach_context": coach_context,
        "guardrails": guardrails,
        "calendar_context": calendar_context,
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
