#!/usr/bin/env python3
"""Sync Athlete Profile and Heart Rate Zones from Intervals.icu.

Fetches the athlete profile (Resting HR, Max HR, LTHR, HR Zones) from the
Intervals.icu REST API and persists them into config/athlete.json.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any, Dict, List, Optional


def load_env_file(filepath: Path) -> None:
    """Load key-value pairs from a .env file into os.environ if not already present."""
    if not filepath.is_file():
        return
    with open(filepath, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, val = line.split("=", 1)
            key = key.strip()
            val = val.strip().strip("'\"")
            if key not in os.environ and val:
                os.environ[key] = val


def fetch_athlete_profile(
    api_key: str,
    athlete_id: str = "0",
    api_base_url: str = "https://intervals.icu/api/v1",
) -> Dict[str, Any]:
    """Fetch athlete profile data from the Intervals.icu API."""
    url = f"{api_base_url.rstrip('/')}/athlete/{athlete_id}"

    auth_string = f"API_KEY:{api_key}"
    auth_header = "Basic " + base64.b64encode(auth_string.encode("utf-8")).decode("ascii")

    req = urllib.request.Request(
        url,
        headers={
            "Authorization": auth_header,
            "User-Agent": "Mozilla/5.0 (compatible; LaufenSync/1.0)",
            "Accept": "application/json",
        },
        method="GET",
    )

    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = resp.read().decode("utf-8")
            return json.loads(data)
    except urllib.error.HTTPError as e:
        error_msg = e.read().decode("utf-8", errors="replace")
        if e.code in (401, 403):
            sys.exit(f"Error {e.code}: Authentication with Intervals.icu failed. Please check INTERVALS_API_KEY.")
        sys.exit(f"HTTP Error {e.code} while fetching athlete profile: {error_msg}")
    except urllib.error.URLError as e:
        sys.exit(f"Network error connecting to Intervals.icu: {e.reason}")


def build_athlete_config(
    profile_data: Dict[str, Any],
    existing_config: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    """Extract athlete heart rate metrics and zones from profile data."""
    name = profile_data.get("name") or "Runner"
    athlete_id = profile_data.get("id") or "0"
    resting_hr = profile_data.get("icu_resting_hr") or profile_data.get("resting_hr") or 55

    # Look for Run sportSettings
    run_settings: Optional[Dict[str, Any]] = None
    sport_settings: List[Dict[str, Any]] = profile_data.get("sportSettings") or []
    for setting in sport_settings:
        types = setting.get("types") or []
        if "Run" in types:
            run_settings = setting
            break

    # If no Run-specific setting found, fall back to first setting or top-level fields
    lthr = 168
    max_hr = 185
    hr_cutoffs: List[int] = []
    hr_zone_names: List[str] = []

    if run_settings:
        lthr = run_settings.get("lthr") or lthr
        max_hr = run_settings.get("max_hr") or max_hr
        hr_cutoffs = run_settings.get("hr_zones") or []
        hr_zone_names = run_settings.get("hr_zone_names") or []
    else:
        lthr = profile_data.get("lthr") or lthr
        max_hr = profile_data.get("max_hr") or max_hr

    # Construct zones dictionary
    zones: Dict[str, Dict[str, Any]] = {}
    if hr_cutoffs:
        prev_cutoff = 0
        for i, cutoff in enumerate(hr_cutoffs):
            z_num = i + 1
            z_name = hr_zone_names[i] if i < len(hr_zone_names) else f"Zone {z_num}"
            key = f"z{z_num}_{z_name.lower().replace(' ', '_')}"
            zones[key] = {
                "zone": z_num,
                "name": z_name,
                "min": prev_cutoff + 1 if prev_cutoff > 0 else 0,
                "max": cutoff,
            }
            prev_cutoff = cutoff
    else:
        # Standard fallback calculation based on LTHR if no cutoffs given
        zones = {
            "z1_recovery": {"zone": 1, "name": "Recovery", "min": 0, "max": int(lthr * 0.85)},
            "z2_aerobic": {"zone": 2, "name": "Aerobic", "min": int(lthr * 0.85) + 1, "max": int(lthr * 0.89)},
            "z3_tempo": {"zone": 3, "name": "Tempo", "min": int(lthr * 0.89) + 1, "max": int(lthr * 0.94)},
            "z4_threshold": {"zone": 4, "name": "Threshold", "min": int(lthr * 0.94) + 1, "max": int(lthr * 1.05)},
            "z5_anaerobic": {"zone": 5, "name": "Anaerobic", "min": int(lthr * 1.05) + 1, "max": max_hr},
        }

    # Retain existing pace targets if present, otherwise set standard defaults
    pace_targets = {
        "easy_run": "06:30 - 07:00",
        "long_run": "06:40 - 07:15",
        "tempo_run": "05:45 - 06:00",
        "intervals_1k": "05:00 - 05:20",
    }
    if existing_config and "athlete" in existing_config:
        existing_pace = existing_config["athlete"].get("pace_targets_min_per_km")
        if existing_pace:
            pace_targets = existing_pace

    return {
        "athlete": {
            "id": str(athlete_id),
            "name": name,
            "heart_rate": {
                "resting_hr": resting_hr,
                "max_hr": max_hr,
                "lthr": lthr,
                "zones": zones,
                "raw_hr_zones": hr_cutoffs,
                "raw_zone_names": hr_zone_names,
            },
            "pace_targets_min_per_km": pace_targets,
        }
    }


def main() -> None:
    project_root = Path(__file__).resolve().parent.parent.parent
    dotenv_path = os.getenv("DOTENV_PATH")
    load_env_file(Path(dotenv_path) if dotenv_path else project_root / ".env")

    parser = argparse.ArgumentParser(
        description="Sync athlete profile and heart rate zones from Intervals.icu."
    )
    parser.add_argument(
        "--output",
        "-o",
        type=Path,
        default=project_root / "config" / "athlete.json",
        help="Target JSON path (default: config/athlete.json)",
    )
    parser.add_argument(
        "--athlete-id",
        default=os.getenv("INTERVALS_ATHLETE_ID", "0"),
        help="Intervals.icu athlete ID (default: '0' or INTERVALS_ATHLETE_ID)",
    )
    parser.add_argument(
        "--api-base",
        default=os.getenv("INTERVALS_API_BASE", "https://intervals.icu/api/v1"),
        help="API Base URL (for testing or proxying)",
    )
    parser.add_argument(
        "--quiet",
        "-q",
        action="store_true",
        help="Suppress output summary",
    )

    args = parser.parse_args()

    api_key = os.getenv("INTERVALS_API_KEY")
    if not api_key:
        sys.exit(
            "Error: INTERVALS_API_KEY is not set. Please define it in your environment or in .env."
        )

    profile = fetch_athlete_profile(
        api_key=api_key,
        athlete_id=args.athlete_id,
        api_base_url=args.api_base,
    )

    existing_config = None
    if args.output.is_file():
        try:
            with open(args.output, "r", encoding="utf-8") as f:
                existing_config = json.load(f)
        except Exception:
            pass

    athlete_config = build_athlete_config(profile, existing_config=existing_config)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with open(args.output, "w", encoding="utf-8") as f:
        json.dump(athlete_config, f, indent=2, ensure_ascii=False)

    if not args.quiet:
        ath = athlete_config["athlete"]
        hr = ath["heart_rate"]
        print(f"Successfully synced profile for '{ath['name']}' (ID: {ath['id']})")
        print(f"Saved configuration to: {args.output}")
        print(f"Resting HR: {hr['resting_hr']} bpm | LTHR: {hr['lthr']} bpm | Max HR: {hr['max_hr']} bpm")
        print("\nHeart Rate Zones:")
        for z_key, z_val in hr["zones"].items():
            print(f"  - {z_val['name']:<18} ({z_val['min']:>3} - {z_val['max']:>3} bpm)")


if __name__ == "__main__":
    main()
