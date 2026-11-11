#!/usr/bin/env python3
"""AI Running Coach context preparation and commentary integration.

Extracts recent running activity metrics, training load, heart rate zone
discipline, and athlete profile targets to prepare a targeted coaching prompt
for Antigravity CLI (agy), and parses/integrates generated AI coaching notes
and structured training summaries.
"""

from __future__ import annotations

import argparse
from datetime import date, datetime, timedelta, timezone
import json
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

# Ensure project root is in sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.analysis.daily_recommendation import calculate_stimulus_context
from src.analysis.decision_trace import append_decision_trace
from src.analysis.coach_history import (
    find_runs_since,
    get_evaluated_run_ids,
    get_open_recommendation,
    initialize_cold_start_history,
    load_coach_history,
    update_history_with_coach_review,
)
from src.analysis.forecast import calculate_fitness_form_trend, predict_race_times
from src.analysis.run_reviewer import load_run_reviews
from src.analysis.trends import (
    aggregate_weekly_trends,
    calculate_run_metrics,
    filter_runs,
    format_duration,
    format_pace,
    load_activities,
)
from src.core.config import load_athlete_profile
from src.llm import LLMClient, get_llm_client


def extract_athlete_display_name(athlete_info: Dict[str, Any]) -> str:
    """Extract a friendly first name or display name for coaching dialogue."""
    if "display_name" in athlete_info and athlete_info["display_name"]:
        return str(athlete_info["display_name"]).strip()
    name = str(athlete_info.get("name") or "").strip()
    if not name:
        return "Athlet"
    # Match CamelCase like "BerniBurker" -> "Berni"
    camel_match = re.match(r"^([A-Z][a-z]{2,})[A-Z]", name)
    if camel_match:
        return camel_match.group(1)
    # Default to first whitespace-delimited word (e.g. "Max Mustermann" -> "Max")
    return name.split()[0]


def determine_athlete_classification(
    runs: List[Dict[str, Any]],
    athlete_profile: Optional[Dict[str, Any]] = None,
    trends: Optional[List[Dict[str, Any]]] = None,
    form_data: Optional[Dict[str, Any]] = None,
) -> Dict[str, str]:
    """Classify athlete level (Hobby, Erfahren, Profi) from profile or metrics."""
    athlete_info = (athlete_profile or {}).get("athlete", {})
    explicit_level = athlete_info.get("classification") or athlete_info.get("level")
    if explicit_level:
        return {
            "category": str(explicit_level),
            "description": "Im Athleten-Profil hinterlegt",
            "source": "config",
        }

    # Fallback when no activities available
    if not runs:
        return {
            "category": "Einsteiger / Wiedereinsteiger",
            "description": "Keine aktuellen Läufe verzeichnet",
            "source": "dynamic",
        }

    num_weeks = max(len(trends or []), 1)
    total_km = sum(
        float(r.get("distance_km") or (r.get("distance", 0) / 1000.0)) for r in runs
    )
    avg_weekly_km = total_km / num_weeks
    avg_runs_per_week = len(runs) / num_weeks
    ctl = float((form_data or {}).get("latest_ctl") or 0.0)

    if avg_weekly_km < 20.0 or avg_runs_per_week < 2.5 or ctl < 18.0:
        category = "Hobbyläufer (Basisaufbau)"
        desc = f"Überschaubares Pensum ({avg_weekly_km:.1f} km/Wo., {avg_runs_per_week:.1f} Läufe/Wo., CTL {ctl:.1f})"
    elif avg_weekly_km < 45.0 or avg_runs_per_week < 3.5 or ctl < 38.0:
        category = "Regelmäßiger Hobbyläufer"
        desc = f"Solides Breitensportpensum ({avg_weekly_km:.1f} km/Wo., {avg_runs_per_week:.1f} Läufe/Wo., CTL {ctl:.1f})"
    elif avg_weekly_km < 70.0 or avg_runs_per_week < 4.5 or ctl < 60.0:
        category = "Ambitionierter Läufer"
        desc = f"Ambitioniertes Pensum ({avg_weekly_km:.1f} km/Wo., {avg_runs_per_week:.1f} Läufe/Wo., CTL {ctl:.1f})"
    else:
        category = "Leistungssportler / Profi"
        desc = f"Hohes Wettkampfpensum ({avg_weekly_km:.1f} km/Wo., {avg_runs_per_week:.1f} Läufe/Wo., CTL {ctl:.1f})"

    return {
        "category": category,
        "description": desc,
        "source": "dynamic",
    }


def save_athlete_classification(
    athlete_file: Optional[Path], classification_str: str
) -> None:
    """Persist athlete classification into athlete configuration JSON."""
    if not athlete_file or not athlete_file.is_file():
        return
    try:
        with open(athlete_file, "r", encoding="utf-8") as f:
            data = json.load(f)
        if "athlete" in data and isinstance(data["athlete"], dict):
            if data["athlete"].get("classification") != classification_str:
                data["athlete"]["classification"] = classification_str
                with open(athlete_file, "w", encoding="utf-8") as f:
                    json.dump(data, f, ensure_ascii=False, indent=2)
    except Exception:
        pass


def load_wellness(wellness_file: Optional[Path]) -> List[Dict[str, Any]]:
    """Load wellness records from JSON file safely."""
    if not wellness_file or not wellness_file.is_file():
        return []
    try:
        with open(wellness_file, "r", encoding="utf-8") as f:
            data = json.load(f)
            return data if isinstance(data, list) else []
    except Exception as e:
        print(f"Warning: Failed to load wellness records from {wellness_file}: {e}", file=sys.stderr)
        return []


def extract_coach_wellness_context(
    wellness_records: Optional[List[Dict[str, Any]]],
    ref_date: Optional[date] = None,
) -> Optional[Dict[str, Any]]:
    """Extract lean, high-signal physiological wellness indicators for AI Coach.

    Filters out noisy raw vendor keys (e.g. bloodGlucose, abdomen, etc.)
    and returns a compact summary of:
    - Sleep duration & score
    - Resting HR and 30-day baseline delta
    - HRV and baseline status
    - Subjective soreness, fatigue, and stress (1-5 scale)
    - Injury and illness/symptoms indicators
    - Recent 3-day recovery trend
    """
    records = wellness_records or []
    if not records:
        return None

    ref_d = ref_date or datetime.now().date()
    ref_iso = ref_d.isoformat()

    dated_records: List[Dict[str, Any]] = []
    for w in records:
        w_id = str(w.get("id") or w.get("date") or "")[:10]
        if w_id and w_id <= ref_iso:
            dated_records.append(w)

    if not dated_records:
        return None

    dated_records.sort(
        key=lambda x: str(x.get("id") or x.get("date") or "")[:10],
        reverse=True,
    )
    latest = dated_records[0]
    latest_date = str(latest.get("id") or latest.get("date") or "")[:10]

    # Sleep metrics
    sleep_secs = latest.get("sleepSecs")
    sleep_hours = (
        round(float(sleep_secs) / 3600.0, 1) if sleep_secs is not None else None
    )
    sleep_score = latest.get("sleepScore")
    sleep_quality = latest.get("sleepQuality")

    # Resting HR and 30-day baseline
    latest_rhr = (
        int(latest["restingHR"]) if latest.get("restingHR") is not None else None
    )
    recent_rhrs = [
        float(w["restingHR"])
        for w in dated_records[:30]
        if w.get("restingHR") is not None
    ]
    rhr_baseline = (
        round(sum(recent_rhrs) / len(recent_rhrs), 1) if recent_rhrs else None
    )
    rhr_delta = (
        round(latest_rhr - rhr_baseline, 1)
        if (latest_rhr is not None and rhr_baseline is not None)
        else None
    )

    # HRV and 14-day average
    latest_hrv = (
        round(float(latest["hrv"]), 1) if latest.get("hrv") is not None else None
    )
    recent_hrvs = [
        float(w["hrv"]) for w in dated_records[:14] if w.get("hrv") is not None
    ]
    hrv_baseline = (
        round(sum(recent_hrvs) / len(recent_hrvs), 1) if recent_hrvs else None
    )

    # Subjective metrics (1-5 scale)
    soreness = latest.get("soreness")
    fatigue = latest.get("fatigue")
    stress = latest.get("stress")
    mood = latest.get("mood")

    # Injury & sickness alerts (inspect latest + last 3 days)
    injury_desc = None
    illness_desc = None
    comments = latest.get("comments")

    illness_keywords = (
        "krank",
        "infekt",
        "erkält",
        "fieber",
        "husten",
        "hals",
        "corona",
        "covid",
        "sick",
        "ill",
        "flu",
    )

    for rec in dated_records[:3]:
        raw_injury = rec.get("injury")
        if (
            raw_injury
            and str(raw_injury).strip().lower() not in ("none", "null", "0", "false")
            and not injury_desc
        ):
            injury_desc = str(raw_injury).strip()
        comm = str(rec.get("comments") or "").strip()
        if comm and any(kw in comm.lower() for kw in illness_keywords) and not illness_desc:
            illness_desc = comm

    has_injury = bool(injury_desc)
    has_illness = bool(illness_desc)
    elevated_rhr = bool(rhr_delta is not None and rhr_delta >= 4.0)
    high_soreness = bool(soreness is not None and int(soreness) >= 4)
    suppressed_hrv = bool(
        latest_hrv is not None
        and hrv_baseline is not None
        and latest_hrv <= (hrv_baseline * 0.85)
    )

    # 3-day history summary
    trend_history: List[Dict[str, Any]] = []
    for rec in dated_records[:3]:
        r_id = str(rec.get("id") or rec.get("date") or "")[:10]
        r_secs = rec.get("sleepSecs")
        trend_history.append(
            {
                "date": r_id,
                "sleep_hours": round(float(r_secs) / 3600.0, 1)
                if r_secs is not None
                else None,
                "resting_hr": rec.get("restingHR"),
                "hrv": rec.get("hrv"),
                "soreness": rec.get("soreness"),
                "injury": rec.get("injury"),
            }
        )

    return {
        "latest_date": latest_date,
        "sleep_hours": sleep_hours,
        "sleep_score": sleep_score,
        "sleep_quality": sleep_quality,
        "latest_rhr": latest_rhr,
        "rhr_baseline": rhr_baseline,
        "rhr_delta": rhr_delta,
        "latest_hrv": latest_hrv,
        "hrv_baseline": hrv_baseline,
        "soreness": soreness,
        "fatigue": fatigue,
        "stress": stress,
        "mood": mood,
        "injury": injury_desc,
        "comments": comments,
        "has_injury": has_injury,
        "has_illness": has_illness,
        "elevated_rhr": elevated_rhr,
        "high_soreness": high_soreness,
        "suppressed_hrv": suppressed_hrv,
        "trend_history": trend_history,
    }


def format_coach_wellness_section(context: Dict[str, Any]) -> str:
    """Formats structured wellness indicators into a concise markdown section."""
    lines = [f"### Erholungs- & Wellness-Status (Stand: {context['latest_date']}):"]

    # Sleep
    sleep_parts = []
    if context.get("sleep_hours") is not None:
        sleep_parts.append(f"{context['sleep_hours']} h")
    if context.get("sleep_score") is not None:
        sleep_parts.append(f"Score {context['sleep_score']}/100")
    elif context.get("sleep_quality") is not None:
        sleep_parts.append(f"Qualität {context['sleep_quality']}/5")
    sleep_str = ", ".join(sleep_parts) if sleep_parts else "Keine Schlafdaten"
    lines.append(f"- Schlaf: {sleep_str}")

    # Resting HR
    if context.get("latest_rhr") is not None:
        base_str = (
            f" (Baseline: {context['rhr_baseline']} bpm, Delta: {context['rhr_delta']:+.1f} bpm)"
            if context.get("rhr_baseline") is not None
            else ""
        )
        lines.append(f"- Ruhepuls: {context['latest_rhr']} bpm{base_str}")

    # HRV
    if context.get("latest_hrv") is not None:
        hrv_base_str = (
            f" (14-Tage-Schnitt: {context['hrv_baseline']} ms)"
            if context.get("hrv_baseline") is not None
            else ""
        )
        lines.append(f"- HRV: {context['latest_hrv']} ms{hrv_base_str}")

    # Subjective
    subj = []
    if context.get("soreness") is not None:
        subj.append(f"Muskelkater/Soreness: {context['soreness']}/5")
    if context.get("fatigue") is not None:
        subj.append(f"Ermüdung/Fatigue: {context['fatigue']}/5")
    if context.get("stress") is not None:
        subj.append(f"Stress: {context['stress']}/5")
    if subj:
        lines.append(f"- Subjektives Befinden: {', '.join(subj)}")

    # Injury & Illness
    if context.get("has_injury"):
        lines.append(f"- ⚠️ VERLETZUNG GEMELDET: {context['injury']}")
    else:
        lines.append("- Verletzungen: Keine gemeldet")

    if context.get("comments"):
        lines.append(f"- Athleten-Notizen: \"{context['comments']}\"")

    # Alerts & Guardrails
    alerts = []
    if context.get("has_injury"):
        alerts.append(f"Akute Verletzung ({context['injury']})")
    if context.get("has_illness"):
        alerts.append("Krankheitssymptome in Notizen")
    if context.get("elevated_rhr"):
        alerts.append(f"Erhöhter Ruhepuls ({context['rhr_delta']:+.1f} bpm über Baseline)")
    if context.get("high_soreness"):
        alerts.append(f"Hohe Muskelerschöpfung (Soreness {context['soreness']}/5)")
    if context.get("suppressed_hrv"):
        alerts.append("Deutlich unterdrückte HRV")

    if alerts:
        lines.append(
            f"- 🚨 VITAL-WARNUNG: {', '.join(alerts)} -> Unbedingt Erholung / Ruhetag ('rest_day') priorisieren!"
        )

    # 3-day trend table / compact bullets
    if len(context.get("trend_history", [])) > 1:
        trend_items = []
        for t in context["trend_history"]:
            t_date = t["date"]
            t_rhr = f"RHR {t['resting_hr']}" if t.get("resting_hr") is not None else ""
            t_hrv = f"HRV {t['hrv']}" if t.get("hrv") is not None else ""
            t_slp = f"{t['sleep_hours']}h Schlaf" if t.get("sleep_hours") is not None else ""
            t_str = " | ".join(filter(None, [t_date, t_slp, t_rhr, t_hrv]))
            trend_items.append(t_str)
        lines.append(f"- Verlauf (letzte 3 Tage): {'; '.join(trend_items)}")

    return "\n".join(lines)


def load_calendar_events(events_file: Optional[Path]) -> List[Dict[str, Any]]:
    """Load calendar events from JSON file safely."""
    if not events_file or not events_file.is_file():
        return []
    try:
        with open(events_file, "r", encoding="utf-8") as f:
            data = json.load(f)
            return data if isinstance(data, list) else []
    except Exception as e:
        print(f"Warning: Failed to load calendar events from {events_file}: {e}", file=sys.stderr)
        return []


def extract_coach_calendar_context(
    calendar_records: Optional[List[Dict[str, Any]]],
    ref_date: Optional[date] = None,
) -> Optional[Dict[str, Any]]:
    """Extract clean, high-signal calendar and planned workout context for AI Coach."""
    records = calendar_records or []
    if not records:
        return None

    ref_d = ref_date or datetime.now().date()
    ref_iso = ref_d.isoformat()

    dated_events: List[Dict[str, Any]] = []
    for ev in records:
        if not isinstance(ev, dict):
            continue
        d_str = str(ev.get("start_date_local") or ev.get("date") or "")[:10]
        if d_str:
            dated_events.append(ev)

    if not dated_events:
        return None

    dated_events.sort(key=lambda x: str(x.get("start_date_local") or x.get("date") or ""))

    today_events = [
        e for e in dated_events
        if str(e.get("start_date_local") or e.get("date") or "")[:10] == ref_iso
    ]

    max_upcoming = ref_d + timedelta(days=7)
    upcoming_events = [
        e for e in dated_events
        if ref_iso < str(e.get("start_date_local") or e.get("date") or "")[:10] <= max_upcoming.isoformat()
    ]

    today_completed = [
        e for e in today_events
        if e.get("category") == "WORKOUT" and e.get("paired_activity_id")
    ]
    today_planned = [
        e for e in today_events
        if e.get("category") == "WORKOUT" and not e.get("paired_activity_id")
    ]
    today_rest = [
        e for e in today_events
        if (e.get("category") == "NOTE" and "ruhetag" in str(e.get("name") or "").lower())
        or e.get("category") == "HOLIDAY"
    ]

    return {
        "today_date": ref_iso,
        "today_completed": today_completed,
        "today_planned": today_planned,
        "today_rest": today_rest,
        "upcoming_events": upcoming_events,
        "all_today_events": today_events,
    }


def format_coach_calendar_section(context: Dict[str, Any]) -> str:
    """Formats structured calendar indicators into a concise markdown section."""
    lines = [f"### Kalender & Geplante Einheiten (Intervals.icu / Garmin, Stand: {context['today_date']}):"]

    today_parts = []
    if context.get("today_completed"):
        for ev in context["today_completed"]:
            today_parts.append(
                f"Absolviertes Workout: \"{ev.get('name')}\" (Paired Activity: {ev.get('paired_activity_id')})"
            )
    if context.get("today_planned"):
        for ev in context["today_planned"]:
            desc = str(ev.get("description") or "").strip().replace("\n", " | ")
            if len(desc) > 80:
                desc = desc[:77] + "..."
            desc_str = f" [{desc}]" if desc else ""
            today_parts.append(f"Geplantes Workout: \"{ev.get('name')}\"{desc_str}")
    if context.get("today_rest"):
        for ev in context["today_rest"]:
            today_parts.append(f"Ruhetag-Notiz: \"{ev.get('name')}\"")

    if today_parts:
        lines.append(f"- Status Heute ({context['today_date']}): {'; '.join(today_parts)}")
    else:
        lines.append(f"- Status Heute ({context['today_date']}): Kein Workout oder Ruhetag im Kalender eingetragen")

    upcoming = context.get("upcoming_events", [])
    if upcoming:
        lines.append("- Nächste geplante Einheiten im Kalender (kommende 7 Tage):")
        for ev in upcoming:
            ev_date = str(ev.get("start_date_local") or ev.get("date") or "")[:10]
            ev_name = ev.get("name", "Workout")
            ev_cat = ev.get("category", "WORKOUT")
            if ev_cat == "NOTE":
                lines.append(f"  * {ev_date} | Notiz: {ev_name}")
            else:
                desc = str(ev.get("description") or "").strip().replace("\n", " | ")
                if len(desc) > 70:
                    desc = desc[:67] + "..."
                desc_str = f" ({desc})" if desc else ""
                lines.append(f"  * {ev_date} | Workout: {ev_name}{desc_str}")
    else:
        lines.append("- Nächste geplante Einheiten: Keine weiteren Einträge in den nächsten 7 Tagen")

    lines.append(
        "- 💡 Kalender-Abstimmung & Trainer-Leitplanke:\n"
        "  Beziehe die Einträge im Kalender aktiv in deine Bewertung ein. Wenn der Athlet ein bestimmtes Workout "
        "geplant hat oder eine Einheit manuell modifiziert hat (z. B. Intervalle statt Schwelle), erkenne diese Absicht an. "
        "Stimme deine Wochenaufgabe und die nächste Empfehlung ('next_recommendation') auf den realen Kalenderstand ab, "
        "anstatt im luftleeren Raum zu planen oder Kalendereinträgen zu widersprechen."
    )

    return "\n".join(lines)


def build_coach_prompt(
    activities_file: Path,
    athlete_file: Optional[Path] = None,
    history_file: Optional[Path] = None,
    reviews_file: Optional[Path] = None,
    wellness_file: Optional[Path] = None,
    wellness_records: Optional[List[Dict[str, Any]]] = None,
    events_file: Optional[Path] = None,
    calendar_records: Optional[List[Dict[str, Any]]] = None,
    save_classification: bool = True,
    ref_date: Optional[date] = None,
) -> str:
    """Prepare a detailed, context-rich prompt for Antigravity AI Coach."""
    activities = load_activities(activities_file)
    runs = filter_runs(activities)
    athlete_profile = load_athlete_profile(athlete_file)
    athlete_info = athlete_profile.get("athlete", {})
    athlete_name = extract_athlete_display_name(athlete_info)
    full_name = athlete_info.get("name") or athlete_name

    # Load persistent run reviews cache if available
    rev_path = reviews_file or (PROJECT_ROOT / "data" / "run_reviews.json")
    reviews_cache = load_run_reviews(rev_path) if rev_path and rev_path.is_file() else {}

    # Load wellness records if available
    w_records = wellness_records
    if w_records is None:
        target_w = wellness_file
        if not target_w and athlete_file and athlete_file.is_file():
            cand = athlete_file.parent / "intervals_wellness.json"
            if cand.is_file():
                target_w = cand
        if not target_w and activities_file and activities_file.is_file():
            cand = activities_file.parent / "intervals_wellness.json"
            if cand.is_file():
                target_w = cand
        if not target_w:
            cand = PROJECT_ROOT / "data" / "intervals_wellness.json"
            if cand.is_file():
                target_w = cand
        w_records = load_wellness(target_w) if target_w else []

    # Load calendar events if available
    cal_records = calendar_records
    if cal_records is None:
        target_ev = events_file
        if not target_ev and athlete_file and athlete_file.is_file():
            cand = athlete_file.parent / "calendar_events.json"
            if cand.is_file():
                target_ev = cand
        if not target_ev and activities_file and activities_file.is_file():
            cand = activities_file.parent / "calendar_events.json"
            if cand.is_file():
                target_ev = cand
        if not target_ev:
            cand = PROJECT_ROOT / "data" / "calendar_events.json"
            if cand.is_file():
                target_ev = cand
        cal_records = load_calendar_events(target_ev) if target_ev else []

    ref_d = ref_date or datetime.now().date()
    wellness_ctx = extract_coach_wellness_context(w_records, ref_date=ref_d)
    wellness_section = format_coach_wellness_section(wellness_ctx) if wellness_ctx else ""
    calendar_ctx = extract_coach_calendar_context(cal_records, ref_date=ref_d)
    calendar_section = format_coach_calendar_section(calendar_ctx) if calendar_ctx else ""

    if not runs:
        return (
            "Du bist ein professioneller, bodenständiger Lauf- und Ausdauertrainer. "
            f"Für {athlete_name} liegen aktuell noch keine Laufaktivitäten vor. "
            f"Gib {athlete_name} eine kurze, sachliche und realistische Empfehlung für den Einstieg in das strukturierte Lauftraining als JSON. "
            "Vermeide jegliche Übertreibung oder Schmeichelei."
        )

    processed_runs = []
    for r in runs:
        m = calculate_run_metrics(r)
        m["moving_time_str"] = format_duration(m["moving_time_s"])
        m["pace_str"] = format_pace(m["pace_s_per_km"])
        processed_runs.append(m)

    # Sort reverse chronological
    processed_runs.sort(key=lambda r: r.get("date") or "", reverse=True)
    recent_runs = processed_runs[:6]

    trends = aggregate_weekly_trends(runs, max_weeks=8)
    form_data = calculate_fitness_form_trend(runs)
    predictions = predict_race_times(processed_runs, athlete_profile)

    # Weekly stats from recent trends
    num_trend_weeks = max(len(trends), 1)
    avg_weekly_km = sum(t["total_distance_km"] for t in trends) / num_trend_weeks
    avg_runs_per_week = sum(t["runs_count"] for t in trends) / num_trend_weeks

    # Athlete classification
    classification = determine_athlete_classification(
        processed_runs, athlete_profile, trends, form_data
    )
    if save_classification and athlete_file:
        save_athlete_classification(athlete_file, classification["category"])

    # Athlete specs
    hr_info = athlete_info.get("heart_rate", {})
    lthr = hr_info.get("lthr", 166)
    max_hr = hr_info.get("max_hr", 183)
    resting_hr = hr_info.get("resting_hr", 51)
    z2_max = (
        hr_info.get("zones", {}).get("z2_aerobic", {}).get("max", 148)
    )

    # Training mode & slot preferences
    training_mode = athlete_info.get("training_mode", "polarized_base")
    weekly_slots = athlete_info.get("weekly_slots", [])

    # Overall base discipline
    total_z1_z2_s = sum(r["z1_z2_seconds"] for r in processed_runs)
    total_zone_s = sum(r["total_zone_seconds"] for r in processed_runs)
    overall_z1_z2_pct = (
        round((total_z1_z2_s / total_zone_s) * 100.0, 1)
        if total_zone_s > 0
        else 0.0
    )

    # Format runs table for prompt
    runs_lines = []
    for r in recent_runs:
        date_str = str(r.get("date", ""))[:10]
        name = r.get("name", "Lauf")
        dist = r.get("distance_km", 0.0)
        pace = r.get("pace_str", "-")
        hr = int(r.get("avg_hr", 0))
        z_sec = r.get("z1_z2_seconds", 0)
        tot_sec = r.get("total_zone_seconds", 0)
        z_pct = round((z_sec / tot_sec) * 100.0, 1) if tot_sec > 0 else 0.0
        load = int(r.get("training_load", 0))

        run_id = str(r.get("id"))
        cached_rev = reviews_cache.get(run_id)
        if cached_rev:
            max_val = cached_rev.get("max_hr") or int(r.get("max_heartrate") or 0)
            cadence = cached_rev.get("cadence_spm") or 0
            cadence_str = f", Kadenz: {int(cadence)} spm" if cadence > 0 else ""
            max_hr_str = f", Max Puls: {max_val} bpm" if max_val > 0 else ""
            rev_details = cached_rev.get("review", {})
            rating_lbl = rev_details.get("rating_label", "")
            summary_txt = rev_details.get("summary", "")
            z_dist_txt = rev_details.get("zone_distribution_text", "")

            runs_lines.append(
                f"- {date_str} | {name}: {dist:.2f} km in {r['moving_time_str']} (Pace: {pace}, Ø Puls: {hr} bpm{max_hr_str}{cadence_str}, Z1/Z2-Anteil: {z_pct}%, Load: {load})\n"
                f"  * Detail-Zonen: {z_dist_txt}\n"
                f"  * Einzel-Review: [{rating_lbl}] {summary_txt}"
            )
        else:
            runs_lines.append(
                f"- {date_str} | {name}: {dist:.2f} km in {r['moving_time_str']} (Pace: {pace}, Puls: {hr} bpm, Z1/Z2-Anteil: {z_pct}%, Load: {load})"
            )

    # Format trends for prompt
    trends_lines = []
    for t in trends[-4:]:
        trends_lines.append(
            f"- {t['iso_week']}: {t['total_distance_km']:.1f} km, {t['runs_count']} Läufe, Ø Pace: {t['avg_pace']}, Ø Puls: {t['avg_hr']:.0f} bpm, Z1/Z2: {t['z1_z2_pct']}%, Load: {t['total_load']:.0f}"
        )

    # Format predictions
    pred_lines = []
    for p in predictions:
        pred_lines.append(
            f"- {p['name']}: {p['predicted_time_str']} (Pace: {p['target_pace_str']}) - {p['readiness']}"
        )

    # Incorporate Coach History & Feedback Loop Context
    history_section = ""
    history_list = load_coach_history(history_file) if history_file and history_file.is_file() else []
    open_rec = get_open_recommendation(history_list)
    if open_rec:
        rec_date = open_rec.get("date", "Unbekannt")
        rec_text = open_rec.get("recommendation", "")
        evaluated_ids = get_evaluated_run_ids(history_list)
        runs_since = find_runs_since(processed_runs, rec_date, exclude_run_ids=evaluated_ids)
        runs_since_lines = []
        for r in runs_since:
            d_s = str(r.get("date", ""))[:10]
            z_pct = round((r.get("z1_z2_seconds", 0) / r.get("total_zone_seconds", 1)) * 100.0, 1) if r.get("total_zone_seconds", 0) > 0 else 0.0
            r_id = str(r.get("id"))
            cached_rev = reviews_cache.get(r_id)
            if cached_rev:
                rev_details = cached_rev.get("review", {})
                r_lbl = rev_details.get("rating_label", "")
                r_sum = rev_details.get("summary", "")
                z_dist = rev_details.get("zone_distribution_text", "")
                cad = int(cached_rev.get("cadence_spm") or 0)
                cad_s = f", Kadenz: {cad} spm" if cad > 0 else ""
                runs_since_lines.append(
                    f"  * {d_s} | {r.get('name', 'Lauf')}: {r.get('distance_km', 0.0):.2f} km, Pace: {r.get('pace_str', '-')}, Ø Puls: {int(round(r.get('avg_hr', 0)))} bpm (Max: {cached_rev.get('max_hr')} bpm{cad_s}), Z1/Z2: {z_pct}%, Load: {int(round(r.get('training_load', 0)))}\n"
                    f"    - Zonen: {z_dist}\n"
                    f"    - Review: [{r_lbl}] {r_sum}"
                )
            else:
                runs_since_lines.append(
                    f"  * {d_s} | {r.get('name', 'Lauf')}: {r.get('distance_km', 0.0):.2f} km, Pace: {r.get('pace_str', '-')}, Ø Puls: {int(round(r.get('avg_hr', 0)))} bpm, Z1/Z2: {z_pct}%, Load: {int(round(r.get('training_load', 0)))}"
                )

        runs_since_str = "\n".join(runs_since_lines) if runs_since_lines else "  * Noch keine neuen Läufe seit dieser Empfehlung eingetragen."

        history_section = f"""
### Bisheriges Coaching-Gedächtnis & Vorgaben (Feedback-Schleife):
- Bisherige offene Empfehlung (vom {rec_date}):
  "{rec_text}"
- Seitdem absolvierte Läufe ({len(runs_since)} Einheit(en)):
{runs_since_str}

### Feedback-Schleife & Adaptive Trainingssteuerung:
Analysiere ehrlich und nüchtern den Abgleich zwischen der letzten Vorgabe und den tatsächlich absolvierten Läufen:
1. Compliance: Hat der Athlet die Empfehlung befolgt, oder ist er z.B. zu schnell gelaufen, hat einen Tempolauf / Intervalle eingeschoben oder Einheiten ausgelassen?
2. Kalender- & Plan-Konsistenz: Wenn der Athlet eine Einheit absolviert hat, die dem real im Kalender / Telegram geplanten Workout entsprach (z. B. Grundlagen- oder Erholungslauf), werte dies als "erfüllt". Rüge den Athleten nicht für eine angebliche Planabweichung, wenn er diszipliniert dem Kalenderplan gefolgt ist.
3. Adaptive Steuerung: Wenn der Athlet ungeplant einen intensiven Tempolauf oder Schwellenlauf absolviert hat, schimpfe nicht, sondern reagiere adaptiv – plane als Nächstes gezielt eine lockere Einheit / aktive Regeneration (Zone 1/2) ein, um Überlastung zu verhindern.
4. Physiologischer Effekt: Was lässt sich aus den Daten über die aerobe Entwicklung, Pace/Puls-Verhältnis und Ermüdung (TSB) ablesen?
"""

    day_name_de = {
        "Monday": "Montag",
        "Tuesday": "Dienstag",
        "Wednesday": "Mittwoch",
        "Thursday": "Donnerstag",
        "Friday": "Freitag",
        "Saturday": "Samstag",
        "Sunday": "Sonntag",
    }.get(ref_d.strftime("%A"), ref_d.strftime("%A"))
    stimulus = calculate_stimulus_context(runs, ref_d, z2_max=z2_max)
    days_since_base = stimulus["days_since_last_base_run"]
    consec_quality = stimulus["consecutive_quality_runs"]

    z4_profile = (
        hr_info.get("zones", {}).get("z4_subthreshold")
        or hr_info.get("zones", {}).get("z4_threshold")
        or {}
    )
    z4_min = int(z4_profile.get("min") or (lthr - 8 if lthr else 168))
    z4_max = int(z4_profile.get("max") or (lthr - 1 if lthr else 174))
    threshold_str = f"{z4_min}–{z4_max} bpm"

    if weekly_slots:
        slot_parts = []
        for s in weekly_slots:
            s_num = s.get("slot") or s.get("id") or (len(slot_parts) + 1)
            days = "/".join(d[:2].capitalize() for d in s.get("allowed_days", []))
            days_str = f" ({days})" if days else ""
            role = s.get("role", "")
            if role == "flexible":
                slot_parts.append(
                    f"Slot {s_num}{days_str}: flexibel (Z2 Basis oder Schwelle {threshold_str} bei Frische)"
                )
            elif role == "quality":
                slot_parts.append(f"Slot {s_num}{days_str}: Tempo/Intervalle")
            elif role == "base_anchor":
                slot_parts.append(f"Slot {s_num}{days_str}: Z2 Long Run < {z2_max} bpm")
            else:
                desc = s.get("description") or s.get("name") or f"Slot {s_num}"
                slot_parts.append(f"Slot {s_num}{days_str}: {desc}")
        slots_line = f"- Wochen-Slots ({len(weekly_slots)} Einheiten): " + ", ".join(slot_parts)
    else:
        slots_line = (
            f"- Wochen-Slots (3 Einheiten): Slot 1 (Mo/Di: Tempo/Intervalle), "
            f"Slot 2 (Mi-Fr: flexibel: Z2 Basis oder Schwelle {threshold_str} bei Frische), "
            f"Slot 3 (Sa/So: Z2 Long Run < {z2_max} bpm)"
        )

    if training_mode in ("flexi_hybrid", "pyramidal_hybrid"):
        mode_desc = (
            f"- Trainingsmodus: Flexi-Modell (3 flexible Säulen: Basis Z2 ~65–75%, Tempo/Schwelle Z3/Z4 ~15–20%, Spitzen Z5+ ~5–10%)\n"
            f"{slots_line}\n"
            f"- Reiz-Steuerung: Schau nüchtern auf die Daten. Weder rügen noch loben. Ein Schwellen- oder Tempolauf ist gewollt (15–20% Z3/Z4). Wenn an einem Qualitätstag locker gelaufen wurde, beschreibe sachlich: Der Temporeiz entfiel, die Beine blieben frisch. Wenn ein Lauf flotter war als gedacht, beschreibe einfach die Auswirkung auf die Ermüdung und was als Nächstes sinnvoll ist."
        )
    else:
        mode_desc = f"- Trainingsmodus: Klassischer polarisierter Basisaufbau (80/20-Methode, Soll Z1/Z2: ~80%)"

    prompt = f"""Du bist ein erfahrener Lauf- und Ausdauertrainer mit Spezialisierung auf sportwissenschaftliche Trainingssteuerung, Herzfrequenz-Zonen und evidenzbasierte Trainingsmodelle (Flexi-Modell und polarisierte Periodisierung).

Analysiere die aktuellen Trainingsdaten des Athleten ({athlete_name}) und erstelle eine vollständige, dynamische Trainings-Zusammenfassung und Handlungsempfehlungen für das Dashboard.

### Tonalität & Coaching-Haltung (STRIKTE VORGABE):
- **Pragmatischer, unaufgeregter Laufpartner auf Augenhöhe**:
  Schreibe wie ein erfahrener Laufkollege, der mit dem Athleten nüchtern auf die Uhr und den Puls schaut.
  Keine Belehrungen, keine Rügen, kein künstliches Schulterklopfen und kein Fachchinesisch.
- **Fakten und praktische Auswirkung statt Urteil (Weder Rüge noch Lob)**:
  Beschreibe einfach, was war (Dauer, Puls, Tempo) und was das praktisch für die Beine und die nächsten Tage bedeutet.
  - Wenn an einem Qualitätstag locker in Zone 2 gelaufen wurde: Nicht belehren oder rügen. Sachlich feststellen: Der Temporeiz entfiel, die Beine blieben frisch, das aerobe Fundament wurde gepflegt.
  - Wenn ein Lauf flotter war als gedacht (z. B. Zone 3/4 statt Zone 2): Nicht belehren oder rügen. Sachlich feststellen: Das war ein zügiger Dauerlauf / Schwellenreiz, der aber Körner gekostet hat, weshalb als Nächstes Erholung sinnvoll ist.
  - Schwellen- und Tempoläufe in Z3/Z4 sind im Flexi-Modell vollkommen gewollt (15–20%). Niemals als "Grauzone" oder Fehler abstempeln.
- **Keine Übertreibungen, kein Hype, keine Schmeicheleien**: Verzichte komplett auf Schmeicheleien, künstlichen Cheerleader-Ton und Floskeln. Bleibe bodenständig, realistisch und unaufgeregt.
- **Herzfrequenz-Steuerung bei Grundlageneinheiten**: Für Zone-2-Läufe NIEMALS eine feste Ziel-Pace vorgeben! Grundlagenläufe werden ausschließlich über die Herzfrequenz (< {z2_max} bpm) gesteuert. Die Pace ist ein reines Resultat und zweitrangig (Gehpausen an Anstiegen sind normal und erwünscht).
- **Wellness-, Verletzungs- & Krankheitssteuerung (Absolute Priorität)**:
  Liegen akute Krankheitssymptome, eine gemeldete Verletzung, ein deutlich erhöhter Ruhepuls (>= +4 bpm über Baseline) oder extremer Muskelkater (Soreness >= 4) vor, hat die Gesundheit und Regeneration absoluten Vorrang.
  Verordne in diesem Fall zwingend Pause / Ruhetag ("rest_day") oder allenfalls eine kurze, sehr lockere regenerative Einheit ("recovery_run"). Fordere unter keinen Umständen Tempo-, Schwellen- oder Umfangssteigerungen!
- **Kalender-Abstimmung & Geplante Workouts (Reale Praxis)**:
  Beziehe die geplanten Einheiten und Notizen im Kalender aktiv in deine Bewertung ein. Wenn der Athlet ein bestimmtes Training geplant hat oder eine Einheit manuell modifiziert hat (z. B. Intervalle statt Schwelle), erkenne diese Absicht an und richte deine Wochenaufgabe und nächste Empfehlung darauf aus. Vermeide es, im luftleeren Raum Trainingsformen zu fordern, die den Kalendereinträgen frontal widersprechen.

### Athleten-Profil & Reiz-Benchmark:
- Aktuelles Datum (Heute): {ref_d.isoformat()} ({day_name_de})
- Name: {full_name} ({athlete_name})
- Einstufung / Level: {classification['category']} ({classification['description']})
{mode_desc}
- Basis-Benchmark: Letzter reiner Z2-Grundlagenlauf liegt {days_since_base} Tag(e) zurück (Qualitätsläufe in Folge ohne Z2: {consec_quality})
- Ruhepuls: {resting_hr} bpm | Maximalpuls: {max_hr} bpm
- Laktatschwelle (LTHR): {lthr} bpm
- Grundlagen-Obergrenze (Zone 2 max): {z2_max} bpm
- Bisherige Zone 1 & 2 Disziplin im gesamten Zeitraum: {overall_z1_z2_pct}%
- Aktuelle Form (TSB): {form_data['current_tsb']:+.1f} (Fitness CTL: {form_data['latest_ctl']}, Ermüdung ATL: {form_data['latest_atl']})
- Aktuelles Pensum: Ø {avg_weekly_km:.1f} km / Woche bei Ø {avg_runs_per_week:.1f} Läufen
{history_section}
{wellness_section}
{calendar_section}

### Letzte Aktivitäten:
{chr(10).join(runs_lines)}

### Wöchentliche Entwicklung (letzte 4 Wochen):
{chr(10).join(trends_lines)}

### Aktuelle Wettkampf-Prognosen (Riegel-Modell):
{chr(10).join(pred_lines)}

---

### Deine Coaching-Aufgabe:
Erstelle eine vollständige, dynamische und ehrliche Trainingsbewertung für {athlete_name}.
Antworte AUSSCHLIESSLICH mit einem validen JSON-Objekt (kein Markdown-Backticks ```json davor oder danach, kein sonstiger Text außenherum) in folgender Struktur:

{{
  "status_title": "Prägnanter Status-Titel basierend auf den Daten (z.B. Solider Grundlagenaufbau / Gute Schwellendisziplin / Reiz-Ausgleich)",
  "status_badge": "Kurzer Badge-Text (z.B. Basis stabil / Tempo dosieren / Pensum stabilisieren)",
  "status_level": "warning" oder "info" oder "success",
  "coach_commentary": "Dein ehrlicher, bodenständiger Coaching-Brief an {athlete_name} als Markdown formatiert (ca. 160-220 Wörter) mit Absätzen, Überschriften (###) und einer konkreten Wochenaufgabe für nächste Woche. Bleibe nüchtern, sachlich und realistisch – absolut keine Übertreibungen oder Schmeicheleien.",
  "history_evaluation": {{
    "compliance_status": "erfüllt" oder "angepasst_nach_tempo" oder "teilweise" oder "verfehlt",
    "actual_summary": "Nüchterne Zusammenfassung der seit der letzten Vorgabe absolvierten Läufe...",
    "effect_analysis": "Messbarer physiologischer Effekt (Pace/Puls-Verhältnis, Ermüdung, Zonen)...",
    "next_recommendation": "Konkrete nächste Aufgabe / Trainingsanweisung...",
    "next_workout_type": "easy_run" oder "intervals" oder "tempo_run" oder "recovery_run" oder "rest_day",
    "next_target_hr": {z2_max},
    "next_duration_min": 45
  }},
  "insights": [
    {{
      "title": "Titel der Erkenntnis (z.B. Aerobe Basis & Disziplin / Schwellenreiz & Tempohärte)",
      "type": "warning" oder "info" oder "success",
      "badge": "Kurzer Tag (z.B. {overall_z1_z2_pct}% Z1/Z2)",
      "text": "Nüchterne, faktenbasierte Erklärung mit den echten gemessenen Werten..."
    }},
    {{
      "title": "Zweite Erkenntnis (z.B. Trainingsvolumen & Häufigkeit)",
      "type": "info",
      "badge": "Ø {avg_weekly_km:.1f} km / Wo.",
      "text": "Realistische Einschätzung zu aktuellem Umfang, Häufigkeit und Long Run..."
    }}
  ],
  "recommendations": [
    {{
      "title": "Titel Empfehlung 1 (z.B. Grundlagenläufe strikt nach Herzfrequenz steuern)",
      "tag": "Priorität 1: Aerobe Basis",
      "tag_class": "tag-amber",
      "text": "Konkrete Praxisanweisung (Puls strikt unter {z2_max} bpm, Pace ignorieren, bei Bedarf Gehpausen)..."
    }},
    {{
      "title": "Titel Empfehlung 2 (z.B. Klare Reiztrennung nach 80/20)",
      "tag": "Trainingsstruktur",
      "tag_class": "tag-cyan",
      "text": "Trennung zwischen 80% lockerer Z2-Basis und gezielten Schwellenreizen an LTHR {lthr} bpm..."
    }},
    {{
      "title": "Titel Empfehlung 3 (z.B. Behutsamer Umfangsausbau)",
      "tag": "Umfangsaufbau",
      "tag_class": "tag-green",
      "text": "Empfehlung für das nächste Wochenziel, angepasst an das Niveau ({classification['category']})..."
    }}
  ]
}}
"""
    return prompt.strip()


def parse_coach_response(raw_text: str) -> Optional[Dict[str, Any]]:
    """Parse and validate JSON response from Antigravity AI Coach."""
    if not raw_text or not raw_text.strip():
        return None

    cleaned = raw_text.strip()
    # Strip markdown code block if present
    match = re.search(r"```(?:json)?\s*([\s\S]*?)\s*```", cleaned)
    if match:
        cleaned = match.group(1).strip()
    else:
        # Find first { and last }
        start = cleaned.find("{")
        end = cleaned.rfind("}")
        if start != -1 and end != -1 and end > start:
            cleaned = cleaned[start : end + 1]

    try:
        data = json.loads(cleaned)
        if not isinstance(data, dict):
            return None

        # Validate minimum expected fields
        if "status_title" in data and "insights" in data and "recommendations" in data:
            return data
    except Exception:
        pass

    return None


def run_ai_coach(
    activities_file: Path,
    athlete_file: Optional[Path] = None,
    history_file: Optional[Path] = None,
    reviews_file: Optional[Path] = None,
    wellness_file: Optional[Path] = None,
    wellness_records: Optional[List[Dict[str, Any]]] = None,
    events_file: Optional[Path] = None,
    calendar_records: Optional[List[Dict[str, Any]]] = None,
    output_json: Optional[Path] = None,
    output_md: Optional[Path] = None,
    llm_client: Optional[LLMClient] = None,
    fallback_on_error: bool = True,
    update_reviews_first: bool = True,
    dry_run: bool = False,
    force: bool = False,
    ref_date: Optional[date] = None,
) -> Dict[str, Any]:
    """Execute end-to-end AI coach pipeline using an abstracted LLM client.

    1. Optionally updates deep per-run review cache heuristically first.
    2. Evaluates all training and rest days (adapts plans on rest/recovery days).
    3. Constructs context-rich coaching prompt.
    4. Invokes LLM via abstracted LLMClient.
    5. Parses structured JSON response and saves summary / commentary.
    6. Updates adaptive coaching memory history.
    7. Falls back gracefully to heuristic analysis on failure.
    """
    athlete_path = athlete_file or (PROJECT_ROOT / "config" / "athlete.json")
    hist_path = history_file or (PROJECT_ROOT / "data" / "coach_history.json")
    rev_path = reviews_file or (PROJECT_ROOT / "data" / "run_reviews.json")
    out_json_path = output_json or (PROJECT_ROOT / "data" / "ai_coach_summary.json")
    out_md_path = output_md or (PROJECT_ROOT / "data" / "ai_coach_commentary.md")

    # Step 1: Pre-populate or refresh heuristic run reviews
    if update_reviews_first and activities_file.is_file() and not dry_run:
        try:
            from src.analysis.run_reviewer import review_runs

            review_runs(
                activities_file=activities_file,
                reviews_file=rev_path,
                athlete_file=athlete_path,
            )
        except Exception as e:
            print(f"Warning: Pre-review of activities failed: {e}", file=sys.stderr)

    # Step 2: Build coach prompt
    prompt = build_coach_prompt(
        activities_file=activities_file,
        athlete_file=athlete_path,
        history_file=hist_path,
        reviews_file=rev_path,
        wellness_file=wellness_file,
        wellness_records=wellness_records,
        events_file=events_file,
        calendar_records=calendar_records,
        save_classification=not dry_run,
        ref_date=ref_date,
    )

    # Step 3: Invoke LLM client
    client = llm_client or get_llm_client()
    llm_res = client.generate(prompt)

    # Step 4: Handle response or fallback
    if not llm_res.success:
        err_msg = llm_res.error or "LLM generation failed"
        print(f"Warning: LLM invocation unsuccessful: {err_msg}", file=sys.stderr)
        if fallback_on_error:
            # Fallback: ensure heuristic reviews exist
            if activities_file.is_file() and not dry_run:
                try:
                    from src.analysis.run_reviewer import review_runs

                    review_runs(
                        activities_file=activities_file,
                        reviews_file=rev_path,
                        athlete_file=athlete_path,
                    )
                except Exception:
                    pass
            return {
                "status": "fallback",
                "error": err_msg,
                "model": llm_res.model,
                "prompt": prompt,
            }
        return {"status": "error", "error": err_msg, "model": llm_res.model}

    parsed = parse_coach_response(llm_res.text)
    if not parsed:
        err_msg = "Could not parse valid structured JSON from LLM response"
        print(f"Warning: {err_msg}", file=sys.stderr)
        if fallback_on_error:
            return {
                "status": "fallback",
                "error": err_msg,
                "raw_output": llm_res.text,
                "model": llm_res.model,
            }
        return {"status": "error", "error": err_msg, "raw_output": llm_res.text}

    # Enrich profile metadata
    profile = {}
    if athlete_path.is_file():
        profile = load_athlete_profile(athlete_path)
        athlete_info = profile.get("athlete", {})
        if "athlete" not in parsed or not isinstance(parsed.get("athlete"), dict):
            parsed["athlete"] = {}
        parsed["athlete"]["name"] = extract_athlete_display_name(athlete_info)
        if athlete_info.get("classification"):
            parsed["athlete"]["classification"] = athlete_info["classification"]

    if dry_run:
        print("[DRY-RUN] Execution completed successfully. Files were NOT modified.")
        print(f"[DRY-RUN] Status: {parsed.get('status_title')} [{parsed.get('status_badge')}]")
        print(f"[DRY-RUN] Target output JSON: {out_json_path}")
        print(f"[DRY-RUN] Target output MD: {out_md_path}")
        if "history_evaluation" in parsed:
            print(f"[DRY-RUN] Next Recommendation: {parsed['history_evaluation'].get('next_recommendation')}")
        return {
            "status": "dry_run",
            "data": parsed,
            "model": llm_res.model,
            "history_updated": False,
            "output_json": str(out_json_path),
            "output_md": str(out_md_path),
        }

    # Write output JSON and Markdown
    out_json_path.parent.mkdir(parents=True, exist_ok=True)
    out_json_path.write_text(
        json.dumps(parsed, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    if "coach_commentary" in parsed and parsed["coach_commentary"]:
        out_md_path.parent.mkdir(parents=True, exist_ok=True)
        out_md_path.write_text(parsed["coach_commentary"], encoding="utf-8")

    # Step 5: Update coach history
    history_updated = False
    if "history_evaluation" in parsed and isinstance(parsed["history_evaluation"], dict):
        activities = load_activities(activities_file) if activities_file.is_file() else []
        runs = filter_runs(activities)
        history = load_coach_history(hist_path)
        if not history and runs:
            history = initialize_cold_start_history(runs, profile, hist_path)
        open_rec = get_open_recommendation(history)
        rec_date = open_rec.get("date", "2000-01-01") if open_rec else "2000-01-01"
        evaluated_ids = get_evaluated_run_ids(history)
        runs_since = find_runs_since(runs, rec_date, exclude_run_ids=evaluated_ids)
        if runs_since:
            update_history_with_coach_review(
                history=history,
                review_data=parsed["history_evaluation"],
                runs_since=runs_since,
                athlete_profile=profile,
                history_file=hist_path,
            )
            history_updated = True

        # Append AI Coach decision trace to decision_trace.jsonl
        hist_eval = parsed["history_evaluation"]
        now_iso = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        today_iso = date.today().isoformat()
        trace_entry = {
            "timestamp": now_iso,
            "trigger": "ai_coach",
            "reference_date": today_iso,
            "target_date": today_iso,
            "is_rest_day": hist_eval.get("next_workout_type") == "rest_day",
            "decision_source": f"llm:{llm_res.model or 'coach'}",
            "selected_workout_name": str(hist_eval.get("next_recommendation") or parsed.get("status_title") or "Coach-Direktive")[:90],
            "selected_workout_type": str(hist_eval.get("next_workout_type") or "easy_run"),
            "rejected_alternatives": [],
            "inputs": {
                "compliance_status": hist_eval.get("compliance_status"),
                "runs_since_count": len(runs_since),
                "next_target_hr": hist_eval.get("next_target_hr"),
                "next_duration_min": hist_eval.get("next_duration_min"),
            },
            "criteria_evaluated": [
                {
                    "criterion": "Makro-Coach Zyklus-Auswertung (IST vs. SOLL)",
                    "passed": hist_eval.get("compliance_status") in ("erfüllt", "angepasst_nach_tempo"),
                    "status": "ok" if hist_eval.get("compliance_status") == "erfüllt" else "override",
                    "value": hist_eval.get("compliance_status"),
                    "detail": str(hist_eval.get("actual_summary") or ""),
                },
                {
                    "criterion": "Physiologische Wirkungsanalyse",
                    "passed": True,
                    "status": "ok",
                    "value": hist_eval.get("next_workout_type"),
                    "detail": str(hist_eval.get("effect_analysis") or ""),
                },
            ],
            "active_warnings": [],
            "reasoning_summary": str(hist_eval.get("next_recommendation") or ""),
        }
        append_decision_trace(trace_entry, out_json_path.parent / "decision_trace.jsonl")

    return {
        "status": "success",
        "data": parsed,
        "model": llm_res.model,
        "history_updated": history_updated,
        "output_json": str(out_json_path),
        "output_md": str(out_md_path),
    }


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Prepare AI coach prompt, parse response, or execute full AI coach pipeline."
    )
    parser.add_argument(
        "--execute",
        action="store_true",
        help="Execute the full AI coach generation and analysis pipeline using LLM abstraction",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Simulate execution without modifying files or coach history",
    )
    parser.add_argument(
        "--provider",
        type=str,
        default=None,
        help="LLM provider (agy, mock, gemini). Defaults to LLM_PROVIDER env var or agy.",
    )
    parser.add_argument(
        "--model",
        type=str,
        default=None,
        help="Override LLM model name",
    )
    parser.add_argument(
        "--timeout",
        type=str,
        default=None,
        help="Override LLM timeout (e.g. 3m or 180s)",
    )
    parser.add_argument(
        "--no-fallback",
        action="store_true",
        help="Disable automatic heuristic fallback on LLM failure",
    )
    parser.add_argument(
        "--skip-if-no-pending",
        action="store_true",
        help="Deprecated: AI coach now always evaluates training and rest days",
    )
    parser.add_argument(
        "--force",
        "-f",
        action="store_true",
        help="Force execution even if no pending runs exist",
    )
    parser.add_argument(
        "--prepare-prompt",
        action="store_true",
        help="Generate AI Coach prompt",
    )
    parser.add_argument(
        "--parse-response",
        type=Path,
        help="Parse raw agy response file and save structured JSON and commentary",
    )
    parser.add_argument(
        "--input",
        "-i",
        type=Path,
        default=PROJECT_ROOT / "data" / "intervals_activities.json",
        help="Path to activity JSON",
    )
    parser.add_argument(
        "--athlete",
        "-a",
        type=Path,
        default=PROJECT_ROOT / "config" / "athlete.json",
        help="Path to athlete profile JSON",
    )
    parser.add_argument(
        "--history",
        type=Path,
        default=PROJECT_ROOT / "data" / "coach_history.json",
        help="Path to coach history JSON",
    )
    parser.add_argument(
        "--reviews",
        type=Path,
        default=PROJECT_ROOT / "data" / "run_reviews.json",
        help="Path to per-run reviews JSON cache",
    )
    parser.add_argument(
        "--wellness",
        "-w",
        type=Path,
        default=None,
        help="Path to intervals_wellness.json (default: auto-detected from athlete or data/)",
    )
    parser.add_argument(
        "--events",
        "--calendar",
        type=Path,
        default=None,
        help="Path to calendar_events.json (default: auto-detected from athlete or data/)",
    )
    parser.add_argument(
        "--output-prompt",
        type=Path,
        help="Write generated prompt directly to file",
    )
    parser.add_argument(
        "--output-json",
        type=Path,
        default=PROJECT_ROOT / "data" / "ai_coach_summary.json",
        help="Path to write parsed structured JSON summary",
    )
    parser.add_argument(
        "--output-md",
        type=Path,
        default=PROJECT_ROOT / "data" / "ai_coach_commentary.md",
        help="Path to write extracted Markdown commentary",
    )

    args = parser.parse_args()

    if args.prepare_prompt:
        try:
            prompt = build_coach_prompt(
                args.input,
                args.athlete,
                history_file=args.history,
                reviews_file=args.reviews,
                wellness_file=args.wellness,
                events_file=args.events,
            )
            if args.output_prompt:
                args.output_prompt.parent.mkdir(parents=True, exist_ok=True)
                args.output_prompt.write_text(prompt, encoding="utf-8")
            else:
                print(prompt)
            return 0
        except Exception as e:
            print(f"Error generating AI coach prompt: {e}", file=sys.stderr)
            return 1

    if args.parse_response:
        if not args.parse_response.exists():
            print(f"Error: Response file not found: {args.parse_response}", file=sys.stderr)
            return 1
        raw_text = args.parse_response.read_text(encoding="utf-8")
        parsed = parse_coach_response(raw_text)
        if not parsed:
            print("Warning: Could not parse valid structured JSON from agy response.", file=sys.stderr)
            return 1

        profile = {}
        if args.athlete and args.athlete.is_file():
            profile = load_athlete_profile(args.athlete)
            athlete_info = profile.get("athlete", {})
            if "athlete" not in parsed or not isinstance(parsed.get("athlete"), dict):
                parsed["athlete"] = {}
            parsed["athlete"]["name"] = extract_athlete_display_name(athlete_info)
            if athlete_info.get("classification"):
                parsed["athlete"]["classification"] = athlete_info["classification"]

        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(parsed, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"Structured AI summary saved: {args.output_json}")

        if "coach_commentary" in parsed and parsed["coach_commentary"]:
            args.output_md.write_text(parsed["coach_commentary"], encoding="utf-8")
            print(f"Extracted AI commentary saved: {args.output_md}")

        # Update coach history if evaluation present
        if "history_evaluation" in parsed and isinstance(parsed["history_evaluation"], dict):
            activities = load_activities(args.input) if args.input.is_file() else []
            runs = filter_runs(activities)
            history = load_coach_history(args.history)
            if not history and runs:
                history = initialize_cold_start_history(runs, profile, args.history)
            open_rec = get_open_recommendation(history)
            rec_date = open_rec.get("date", "2000-01-01") if open_rec else "2000-01-01"
            evaluated_ids = get_evaluated_run_ids(history)
            runs_since = find_runs_since(runs, rec_date, exclude_run_ids=evaluated_ids)
            if runs_since:
                update_history_with_coach_review(
                    history=history,
                    review_data=parsed["history_evaluation"],
                    runs_since=runs_since,
                    athlete_profile=profile,
                    history_file=args.history,
                )
                print(f"Coach history successfully updated: {args.history}")
            else:
                print("No new runs since current open recommendation. History remains unchanged.")

        return 0

    if args.execute or args.dry_run:
        client_kwargs: Dict[str, Any] = {}
        if args.model:
            client_kwargs["model"] = args.model
        if args.timeout:
            client_kwargs["timeout"] = args.timeout

        try:
            client = get_llm_client(provider=args.provider, **client_kwargs)
        except Exception as e:
            print(f"Error initializing LLM client: {e}", file=sys.stderr)
            return 1

        result = run_ai_coach(
            activities_file=args.input,
            athlete_file=args.athlete,
            history_file=args.history,
            reviews_file=args.reviews,
            wellness_file=args.wellness,
            events_file=args.events,
            output_json=args.output_json,
            output_md=args.output_md,
            llm_client=client,
            fallback_on_error=not args.no_fallback,
            dry_run=args.dry_run,
            force=args.force,
        )

        if result.get("status") == "dry_run":
            return 0
        elif result.get("status") == "success":
            print(f"AI Coach execution successful. Summary: {args.output_json}")
            if result.get("history_updated"):
                print(f"Coach history updated: {args.history}")
            return 0
        elif result.get("status") == "fallback":
            print(f"AI Coach completed with fallback: {result.get('error')}", file=sys.stderr)
            return 0
        else:
            print(f"Error in AI Coach execution: {result.get('error')}", file=sys.stderr)
            return 1

    try:
        prompt = build_coach_prompt(
            args.input,
            args.athlete,
            history_file=args.history,
            reviews_file=args.reviews,
            wellness_file=args.wellness,
            events_file=args.events,
        )
        if args.output_prompt:
            args.output_prompt.parent.mkdir(parents=True, exist_ok=True)
            args.output_prompt.write_text(prompt, encoding="utf-8")
        else:
            print(prompt)
        return 0
    except Exception as e:
        print(f"Error generating AI coach prompt: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
