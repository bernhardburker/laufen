#!/usr/bin/env python3
"""Deterministic per-second HR-zone pace (GAP) and split analysis from activity streams."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

DEFAULT_MIN_RUNNING_SPEED_MPS = 1.67  # ~10:00/km; filters out walking pauses
DEFAULT_MIN_ZONE_SECONDS = 120        # 2 minutes minimum per zone to count as trend point
DEFAULT_HR_ZONE_BOUNDS: List[Tuple[int, str, int, int]] = [
    (1, "Zone 1", 0, 150),
    (2, "Zone 2", 151, 159),
    (3, "Zone 3", 160, 168),
    (4, "Zone 4", 169, 177),
    (5, "Zone 5", 178, 250),
]


def format_pace_seconds(sec_per_km: Optional[float]) -> str:
    """Formats seconds per km as MM:SS/km."""
    if sec_per_km is None or sec_per_km <= 0:
        return "--:--"
    total_s = int(round(sec_per_km))
    minutes = total_s // 60
    seconds = total_s % 60
    return f"{minutes:02d}:{seconds:02d}/km"


def resolve_hr_zone_bounds(athlete_profile: Optional[Dict[str, Any]] = None) -> List[Tuple[int, str, int, int]]:
    """Extracts ordered (zone_num, name, min_hr, max_hr) tuples from athlete.json."""
    if not athlete_profile:
        return list(DEFAULT_HR_ZONE_BOUNDS)

    hr_cfg = (athlete_profile.get("athlete") or {}).get("heart_rate") or {}
    zones_dict = hr_cfg.get("zones") or {}
    parsed: List[Tuple[int, str, int, int]] = []

    if isinstance(zones_dict, dict) and zones_dict:
        for z_info in zones_dict.values():
            if not isinstance(z_info, dict) or "zone" not in z_info:
                continue
            z_num = int(z_info["zone"])
            z_name = str(z_info.get("name") or f"Zone {z_num}")
            z_min = int(z_info.get("min") or 0)
            z_max = int(z_info.get("max") or 250)
            parsed.append((z_num, z_name, z_min, z_max))

    if parsed:
        parsed.sort(key=lambda x: x[0])
        # Ensure the highest zone catches any HR above configured max_hr
        last_num, last_name, last_min, last_max = parsed[-1]
        parsed[-1] = (last_num, last_name, last_min, max(last_max, 250))
        return parsed

    raw_zones = hr_cfg.get("raw_hr_zones") or []
    if isinstance(raw_zones, list) and len(raw_zones) >= 4:
        prev_max = -1
        for idx, bound in enumerate(raw_zones, start=1):
            z_max = 250 if idx == len(raw_zones) else int(bound)
            z_min = 0 if idx == 1 else prev_max + 1
            parsed.append((idx, f"Zone {idx}", z_min, z_max))
            prev_max = int(bound)
        return parsed

    return list(DEFAULT_HR_ZONE_BOUNDS)


def minetti_grade_factor(grade: float) -> float:
    """Returns Minetti energy cost ratio C(grade) / C(0) for Grade Adjusted Pace.

    Clamped to [-0.25, 0.25] to guard against GPS altitude spikes.
    """
    g = max(-0.25, min(0.25, float(grade)))
    cost_g = (
        155.4 * (g ** 5)
        - 30.4 * (g ** 4)
        - 43.3 * (g ** 3)
        + 46.3 * (g ** 2)
        + 19.5 * g
        + 3.6
    )
    return max(0.5, cost_g / 3.6)


def compute_gap_speeds(
    speeds: List[Optional[float]],
    altitudes: Optional[List[Optional[float]]],
    times: Optional[List[Optional[int]]] = None,
    window_half: int = 5,
) -> List[Optional[float]]:
    """Computes Grade Adjusted Speed (m/s) per sample from velocity_smooth & altitude."""
    n = len(speeds)
    if not altitudes or len(altitudes) != n:
        return [float(s) if s is not None else None for s in speeds]

    gap_speeds: List[Optional[float]] = []
    for i in range(n):
        v = speeds[i]
        if v is None or v <= 0:
            gap_speeds.append(None)
            continue

        lo = max(0, i - window_half)
        hi = min(n - 1, i + window_half)
        alt_lo = altitudes[lo]
        alt_hi = altitudes[hi]
        if alt_lo is None or alt_hi is None or hi == lo:
            gap_speeds.append(float(v))
            continue

        if times and len(times) == n and times[hi] is not None and times[lo] is not None:
            dt = max(1.0, float(times[hi] - times[lo]))
        else:
            dt = float(hi - lo)

        # Estimate horizontal distance over the window using local speeds
        win_speeds = [float(s) for s in speeds[lo : hi + 1] if s is not None and s > 0]
        avg_v = sum(win_speeds) / len(win_speeds) if win_speeds else float(v)
        dist = avg_v * dt
        if dist < 5.0:
            gap_speeds.append(float(v))
            continue

        grade = (float(alt_hi) - float(alt_lo)) / dist
        gap_speeds.append(float(v) * minetti_grade_factor(grade))

    return gap_speeds


def _classify_hr_zone(hr: float, bounds: List[Tuple[int, str, int, int]]) -> int:
    for z_num, _name, z_min, z_max in bounds:
        if z_min <= hr <= z_max:
            return z_num
    if hr < bounds[0][2]:
        return bounds[0][0]
    return bounds[-1][0]


def load_activity_stream(streams_dir: Optional[Path], activity_id: str) -> Optional[Dict[str, Any]]:
    """Loads cached stream JSON for an activity if present."""
    if not streams_dir or not activity_id:
        return None
    path = streams_dir / f"{activity_id}.json"
    if not path.is_file():
        return None
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else None
    except Exception:
        return None


def compute_run_zone_pace_stats(
    stream: Dict[str, Any],
    athlete_profile: Optional[Dict[str, Any]] = None,
    min_running_speed_mps: float = DEFAULT_MIN_RUNNING_SPEED_MPS,
    min_zone_seconds: int = DEFAULT_MIN_ZONE_SECONDS,
) -> Dict[str, Any]:
    """Computes per-HR-zone GAP/raw pace and 1st/2nd half split from activity streams."""
    hrs = stream.get("heartrate") or []
    speeds = stream.get("velocity_smooth") or []
    altitudes = stream.get("fixed_altitude") or stream.get("altitude")
    times = stream.get("time")

    n = min(len(hrs), len(speeds))
    if n == 0:
        return {"has_streams": False, "zones": {}}

    bounds = resolve_hr_zone_bounds(athlete_profile)
    gap_speeds = compute_gap_speeds(speeds[:n], altitudes[:n] if altitudes else None, times[:n] if times else None)

    zone_acc: Dict[int, Dict[str, float]] = {
        z_num: {"sec": 0.0, "hr_sum": 0.0, "raw_v_sum": 0.0, "gap_v_sum": 0.0}
        for z_num, _, _, _ in bounds
    }

    running_samples: List[Tuple[float, float, float]] = []  # (hr, raw_v, gap_v)
    walking_filtered = 0

    for i in range(n):
        hr = hrs[i]
        raw_v = speeds[i]
        gap_v = gap_speeds[i]
        if hr is None or raw_v is None or hr <= 0 or raw_v <= 0:
            continue

        dt = 1.0
        if times and len(times) >= n and i > 0 and times[i] is not None and times[i - 1] is not None:
            delta = float(times[i] - times[i - 1])
            if 0 < delta <= 10:
                dt = delta

        if float(raw_v) < min_running_speed_mps:
            walking_filtered += int(round(dt))
            continue

        eff_gap_v = float(gap_v) if gap_v is not None and gap_v > 0 else float(raw_v)
        z_num = _classify_hr_zone(float(hr), bounds)
        acc = zone_acc[z_num]
        acc["sec"] += dt
        acc["hr_sum"] += float(hr) * dt
        acc["raw_v_sum"] += float(raw_v) * dt
        acc["gap_v_sum"] += eff_gap_v * dt
        running_samples.append((float(hr), float(raw_v), eff_gap_v))

    zones_out: Dict[str, Dict[str, Any]] = {}
    for z_num, z_name, _, _ in bounds:
        acc = zone_acc[z_num]
        sec = int(round(acc["sec"]))
        if sec <= 0:
            continue
        raw_v_avg = acc["raw_v_sum"] / acc["sec"]
        gap_v_avg = acc["gap_v_sum"] / acc["sec"]
        raw_pace_s = 1000.0 / raw_v_avg if raw_v_avg > 0 else None
        gap_pace_s = 1000.0 / gap_v_avg if gap_v_avg > 0 else None
        zones_out[f"z{z_num}"] = {
            "zone": z_num,
            "name": z_name,
            "running_seconds": sec,
            "avg_hr": round(acc["hr_sum"] / acc["sec"], 1),
            "gap_speed_mps": round(gap_v_avg, 3),
            "gap_pace_s_per_km": round(gap_pace_s, 1) if gap_pace_s else None,
            "gap_pace_str": format_pace_seconds(gap_pace_s),
            "raw_speed_mps": round(raw_v_avg, 3),
            "raw_pace_s_per_km": round(raw_pace_s, 1) if raw_pace_s else None,
            "raw_pace_str": format_pace_seconds(raw_pace_s),
            "valid_for_trend": sec >= min_zone_seconds,
        }

    first_half_gap_pace: Optional[float] = None
    second_half_gap_pace: Optional[float] = None
    first_half_hr: Optional[float] = None
    second_half_hr: Optional[float] = None
    pace_split_diff_s: Optional[float] = None

    if len(running_samples) >= 20:
        mid = len(running_samples) // 2
        h1 = running_samples[:mid]
        h2 = running_samples[mid:]
        v1 = sum(x[2] for x in h1) / len(h1)
        v2 = sum(x[2] for x in h2) / len(h2)
        if v1 > 0 and v2 > 0:
            first_half_gap_pace = round(1000.0 / v1, 1)
            second_half_gap_pace = round(1000.0 / v2, 1)
            pace_split_diff_s = round(second_half_gap_pace - first_half_gap_pace, 1)
        first_half_hr = round(sum(x[0] for x in h1) / len(h1), 1)
        second_half_hr = round(sum(x[0] for x in h2) / len(h2), 1)

    return {
        "has_streams": True,
        "min_zone_seconds": min_zone_seconds,
        "total_running_seconds": len(running_samples),
        "walking_seconds_filtered": walking_filtered,
        "zones": zones_out,
        "first_half_gap_pace_s_per_km": first_half_gap_pace,
        "second_half_gap_pace_s_per_km": second_half_gap_pace,
        "first_half_avg_hr": first_half_hr,
        "second_half_avg_hr": second_half_hr,
        "pace_split_diff_s": pace_split_diff_s,
    }


def build_zone_pace_trends(
    run_reviews: Dict[str, Any],
    zones_to_include: Tuple[int, ...] = (1, 2, 3, 4),
    rolling_window: int = 5,
) -> Dict[str, Any]:
    """Builds chronological per-zone GAP pace trend series from cached run reviews."""
    entries = [
        v for v in (run_reviews or {}).values()
        if isinstance(v, dict) and isinstance(v.get("zone_pace_stats"), dict)
    ]
    entries.sort(key=lambda e: str(e.get("date") or ""))

    series: Dict[str, List[Dict[str, Any]]] = {f"z{z}": [] for z in zones_to_include}

    for entry in entries:
        z_stats = entry.get("zone_pace_stats") or {}
        if not z_stats.get("has_streams"):
            continue
        zones_map = z_stats.get("zones") or {}
        date_str = str(entry.get("date") or "")[:10]
        for z in zones_to_include:
            z_key = f"z{z}"
            z_data = zones_map.get(z_key)
            if not z_data or not z_data.get("valid_for_trend"):
                continue
            gap_s = z_data.get("gap_pace_s_per_km")
            if gap_s is None:
                continue
            series[z_key].append({
                "id": entry.get("id"),
                "date": date_str,
                "name": entry.get("name") or "Lauf",
                "gap_pace_s_per_km": gap_s,
                "gap_pace_str": z_data.get("gap_pace_str") or format_pace_seconds(gap_s),
                "raw_pace_s_per_km": z_data.get("raw_pace_s_per_km"),
                "raw_pace_str": z_data.get("raw_pace_str"),
                "avg_hr": z_data.get("avg_hr"),
                "running_seconds": z_data.get("running_seconds"),
            })

    summary: Dict[str, Dict[str, Any]] = {}
    for z_key, pts in series.items():
        for idx, pt in enumerate(pts):
            win = pts[max(0, idx - rolling_window + 1) : idx + 1]
            avg_s = sum(p["gap_pace_s_per_km"] for p in win) / len(win)
            pt["rolling_gap_pace_s_per_km"] = round(avg_s, 1)

        if len(pts) >= 2:
            k = min(3, max(1, len(pts) // 3))
            early_avg = sum(p["gap_pace_s_per_km"] for p in pts[:k]) / k
            recent_avg = sum(p["gap_pace_s_per_km"] for p in pts[-k:]) / k
            delta_s = round(recent_avg - early_avg, 1)  # negative = faster
            summary[z_key] = {
                "count": len(pts),
                "early_avg_s": round(early_avg, 1),
                "early_avg_str": format_pace_seconds(early_avg),
                "recent_avg_s": round(recent_avg, 1),
                "recent_avg_str": format_pace_seconds(recent_avg),
                "delta_s": delta_s,
            }
        elif len(pts) == 1:
            summary[z_key] = {
                "count": 1,
                "early_avg_s": pts[0]["gap_pace_s_per_km"],
                "early_avg_str": pts[0]["gap_pace_str"],
                "recent_avg_s": pts[0]["gap_pace_s_per_km"],
                "recent_avg_str": pts[0]["gap_pace_str"],
                "delta_s": 0.0,
            }
        else:
            summary[z_key] = {"count": 0, "delta_s": None}

    return {"series": series, "summary": summary}
