#!/usr/bin/env python3
"""Incremental per-run deep physiological analysis and review cache.

Extracts second-level HR zone distributions, cadence, peak HR, elevation,
aerobic decoupling/efficiency, and interval breakdowns for each running activity.
Persists per-run coach reviews into a local cache (data/run_reviews.json) so each run
is analyzed in depth exactly once without redundant re-processing.
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


def format_seconds_to_min_sec(seconds: int) -> str:
    """Format seconds into 'Xm Ys' or 'Xs'."""
    if seconds <= 0:
        return "0s"
    m, s = divmod(int(round(seconds)), 60)
    if m > 0:
        return f"{m}m {s:02d}s" if s > 0 else f"{m}m"
    return f"{s}s"


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
    training_load = round(float(run.get("icu_training_load") or 0.0), 1)

    # Zone breakdown
    zone_times = run.get("icu_hr_zone_times") or []
    total_zone_s = sum(zone_times) if isinstance(zone_times, list) else 0
    zone_breakdown: Dict[str, Dict[str, Any]] = {}

    zone_names = [
        "Zone 1 (Recovery)",
        "Zone 2 (Aerobic)",
        "Zone 3 (Tempo/Grauzone)",
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
    interval_summary = run.get("interval_summary") or []
    if isinstance(interval_summary, list):
        interval_summary = [str(x) for x in interval_summary]
    else:
        interval_summary = []

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
        "aerobic_ef": ef,
        "z1_z2_seconds": z1_z2_seconds,
        "total_zone_seconds": total_zone_s,
        "z1_z2_pct": z1_z2_pct,
        "zone_breakdown": zone_breakdown,
        "intervals": interval_summary,
        "intensity_class": intensity_class,
    }


def classify_run_intensity(
    z1_z2_pct: float,
    zone_breakdown: Dict[str, Dict[str, Any]],
    avg_hr: float,
    z2_max: int,
    lthr: int,
) -> str:
    """Classify the primary physiological category of the run."""
    z3_pct = zone_breakdown.get("z3", {}).get("pct", 0.0)
    z4_pct = zone_breakdown.get("z4", {}).get("pct", 0.0)
    z5_pct = zone_breakdown.get("z5", {}).get("pct", 0.0)

    if z1_z2_pct >= 80.0:
        return "aerobe_basis"
    if z5_pct >= 10.0 or (z4_pct + z5_pct >= 35.0 and avg_hr >= lthr - 5):
        return "schwellen_intervall"
    if z3_pct >= 35.0 and z1_z2_pct < 60.0:
        return "grauzone_tempo"
    if z4_pct >= 30.0:
        return "schwellenlauf"
    if z1_z2_pct >= 60.0:
        return "ueberwiegend_aerob"
    return "gemischte_intensitaet"


def evaluate_run_deep(
    metrics: Dict[str, Any], athlete_profile: Optional[Dict[str, Any]] = None
) -> Dict[str, Any]:
    """Generate a detailed, physiologically sound review of a single run."""
    hr_profile = (athlete_profile or {}).get("athlete", {}).get("heart_rate", {})
    z2_max = int(
        hr_profile.get("zones", {}).get("z2_aerobic", {}).get("max") or 158
    )
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

    # Determine assessment rating & text
    if z1_z2_pct >= 85.0:
        rating = "optimal"
        rating_label = "Optimaler Grundlagenlauf"
        summary = (
            f"Hervorragende aerobe Zonendisziplin ({z1_z2_pct}% in Z1/Z2). "
            f"Die Herzfrequenz blieb mit Ø {int(avg_hr)} bpm verlässlich unter der Z2-Grenze ({z2_max} bpm). "
            f"Z1: {z1_str}, Z2: {z2_str}."
        )
        tip = "Genau dieser Intensitätsbereich stärkt die Kapillarisierung und den Fettstoffwechsel."
    elif z1_z2_pct >= 60.0 or metrics["intensity_class"] == "ueberwiegend_aerob":
        rating = "solide"
        rating_label = "Überwiegend aerob mit Z3-Drift"
        if avg_hr <= z2_max:
            hr_comment = f"Ø Puls lag mit {int(avg_hr)} bpm im Soll (< {z2_max} bpm), jedoch kletterte der Maximalpuls auf {max_hr} bpm."
        else:
            hr_comment = f"Ø Puls ({int(avg_hr)} bpm) und Maximalpuls ({max_hr} bpm) lagen leicht über der Z2-Obergrenze ({z2_max} bpm)."
        summary = (
            f"Solider Grundlagenschwerpunkt ({z1_z2_pct}% in Z1/Z2), jedoch {z3_str} in Zone 3 abgedriftet. "
            f"{hr_comment}"
        )
        tip = "An Steigungen oder in der zweiten Hälfte früher Tempo herausnehmen, um nicht in Zone 3 abzudriften."
    elif metrics["intensity_class"] in ("schwellenlauf", "schwellen_intervall"):
        rating = "intensiv"
        rating_label = "Gezielter Schwellen- / Tempolauf"
        summary = (
            f"Intensive Belastung nahe der Laktatschwelle ({lthr} bpm). "
            f"{z4_str} in Zone 4 bei Ø {int(avg_hr)} bpm (Max: {max_hr} bpm). Training Load: {int(metrics['training_load'])}."
        )
        tip = "Wichtiger Reiz für Tempohärte – erfordert nun mindestens 48 Stunden lockere Regeneration."
    else:
        rating = "grauzone"
        rating_label = "Klassische Intensitätsfalle (Grauzone)"
        if avg_hr > z2_max:
            hr_part = f"Ø Puls {int(avg_hr)} bpm liegt über der Z2-Obergrenze ({z2_max} bpm)."
        else:
            hr_part = f"Spitzenpuls von {max_hr} bpm und {z3_str} in Zone 3 lagen über der Z2-Obergrenze ({z2_max} bpm)."
        summary = (
            f"Zu intensiv für Grundlagenausdauer, aber zu locker für gezielten Schwellenreiz. "
            f"Nur {z1_z2_pct}% in Z1/Z2, dafür {z3_str} in Zone 3 und {z4_str} in Zone 4. "
            f"{hr_part}"
        )
        tip = "Pace konsequent um 60–90 s/km drosseln, bis der Puls strikt unter 158 bpm bleibt."

    # Cadence assessment
    if cadence > 0:
        if cadence < 155.0:
            cadence_note = (
                f"Ø {int(cadence)} spm ist etwas niedrig. Schrittfrequenz mittelfristig "
                "durch kürzere Schritte behutsam Richtung 158–165 spm steigern."
            )
        elif cadence <= 172.0:
            cadence_note = f"Ø {int(cadence)} spm liegt im soliden, ökonomischen Freizeit-Bereich."
        else:
            cadence_note = f"Ø {int(cadence)} spm zeugt von hoher Schrittfrequenz und guter Fußarbeit."
    else:
        cadence_note = "Keine Schrittfrequenz-Sensordaten vorhanden."

    return {
        "rating": rating,
        "rating_label": rating_label,
        "summary": summary,
        "coach_tip": tip,
        "cadence_feedback": cadence_note,
        "zone_distribution_text": f"Z1: {z1_str} | Z2: {z2_str} | Z3: {z3_str} | Z4: {z4_str}",
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
    """Return runs that do not yet have a cached review."""
    # Sort reverse chronological (newest first)
    sorted_runs = sorted(runs, key=lambda r: str(r.get("start_date_local") or ""), reverse=True)
    if recent is not None and recent > 0:
        sorted_runs = sorted_runs[:recent]

    unreviewed = []
    for r in sorted_runs:
        run_id = str(r.get("id"))
        if force or run_id not in reviews:
            unreviewed.append(r)

    if limit is not None and limit > 0:
        return unreviewed[:limit]
    return unreviewed


def review_runs(
    activities_file: Path,
    reviews_file: Optional[Path] = None,
    athlete_file: Optional[Path] = None,
    limit: Optional[int] = None,
    recent: Optional[int] = None,
    force: bool = False,
) -> Dict[str, Any]:
    """Incrementally review unreviewed runs and persist into review cache."""
    activities = load_activities(activities_file)
    runs = filter_runs(activities)
    profile = load_athlete_profile(athlete_file)
    reviews_path = reviews_file or (PROJECT_ROOT / "data" / "run_reviews.json")
    reviews = load_run_reviews(reviews_path)

    unreviewed = find_unreviewed_runs(runs, reviews, limit=limit, recent=recent, force=force)
    now_iso = datetime.datetime.now().isoformat()

    added_count = 0
    for run in unreviewed:
        run_id = str(run.get("id"))
        metrics = extract_deep_run_metrics(run, profile)
        evaluation = evaluate_run_deep(metrics, profile)

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
            "z1_z2_pct": metrics["z1_z2_pct"],
            "zone_breakdown": metrics["zone_breakdown"],
            "intervals": metrics["intervals"],
            "intensity_class": metrics["intensity_class"],
            "review": evaluation,
            "analyzed_at": now_iso,
        }
        added_count += 1

    if added_count > 0:
        save_run_reviews(reviews, reviews_path)

    return reviews


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Extract deep physiological run metrics and maintain per-run review cache."
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
        print("All matching runs already reviewed. Cache is up to date.")
        return 0

    updated_reviews = review_runs(
        activities_file=args.input,
        reviews_file=args.reviews,
        athlete_file=args.athlete,
        limit=args.limit,
        recent=args.recent,
        force=args.force,
    )

    print(f"Successfully processed {len(unreviewed)} runs into cache: {args.reviews}")
    # Print summary of reviewed runs
    for run in unreviewed[:5]:
        rid = str(run.get("id"))
        rev = updated_reviews.get(rid, {})
        r_info = rev.get("review", {})
        print(f"- {rev.get('date')} | {rev.get('name')}: {r_info.get('rating_label')} ({rev.get('z1_z2_pct')}% Z1/Z2)")

    return 0


if __name__ == "__main__":
    sys.exit(main())
