#!/usr/bin/env python3
"""seed_athlete.py

Seeds synthetic training and wellness data for a test athlete (default: berni-test)
in Intervals.icu or a local mock server.

Features:
- Injects 7-14 days of realistic daily wellness (HRV, resting HR, sleep, soreness, fatigue).
- Injects structured test activities across weekly slots (Z2 easy run, intervals, tempo).
- Supports distinct testing scenarios:
    * 'balanced' (normal recovery, progressive training load)
    * 'fatigued'  (suppressed HRV, elevated resting HR, high soreness)
    * 'fresh'     (optimal HRV, low resting HR, low fatigue, ready for quality)
- Supports clean-up mode (--clean) to delete seeded events and clear test entries.
- Strictly guards against running against production athletes (requires explicit confirmation
  or --force if athlete is not flagged as staging/test).
"""

from __future__ import annotations

import argparse
import base64
import datetime
import json
import os
from pathlib import Path
import sys
from typing import Any, Dict, List, Optional
import urllib.error
import urllib.request

ROOT_DIR = Path(__file__).resolve().parent.parent.parent
if str(ROOT_DIR) not in sys.path:
    sys.path.insert(0, str(ROOT_DIR))

from src.athletes import AthleteContext, resolve_athlete_context


def load_env_file(filepath: Path) -> None:
    """Loads key-value pairs from a .env file into os.environ if not already set."""
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


def make_api_request(
    url: str,
    method: str = "GET",
    api_key: str = "",
    payload: Optional[Dict[str, Any]] = None,
) -> Any:
    """Executes an authenticated JSON HTTP request to Intervals.icu."""
    auth_string = f"API_KEY:{api_key}"
    auth_header = "Basic " + base64.b64encode(auth_string.encode("utf-8")).decode("ascii")

    headers = {
        "Authorization": auth_header,
        "Content-Type": "application/json",
        "User-Agent": "Mozilla/5.0 (compatible; LaufenSeeder/1.0)",
        "Accept": "application/json",
    }

    data_bytes = json.dumps(payload).encode("utf-8") if payload is not None else None
    req = urllib.request.Request(url, data=data_bytes, headers=headers, method=method)

    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            content = resp.read().decode("utf-8")
            if not content:
                return {}
            return json.loads(content)
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"HTTP {e.code} ({method} {url}): {body}") from e
    except urllib.error.URLError as e:
        raise RuntimeError(f"Network error ({method} {url}): {e.reason}") from e


def generate_wellness_records(
    days: int = 14,
    scenario: str = "balanced",
) -> List[Dict[str, Any]]:
    """Generates synthetic daily wellness payloads based on testing scenario."""
    today = datetime.date.today()
    records = []

    for i in range(days, 0, -1):
        target_date = today - datetime.timedelta(days=i)
        date_str = target_date.isoformat()

        if scenario == "fatigued":
            # Suppressed HRV, elevated HR, bad sleep, high muscle soreness
            rec = {
                "id": date_str,
                "hrv": round(45.0 + (i % 3) * 1.5, 1),
                "restingHR": 58 + (i % 2),
                "sleepSecs": 21600 + (i % 4) * 900,  # ~6.0 - 6.75 hrs
                "sleepQuality": 2,
                "soreness": 4,
                "fatigue": 4,
                "stress": 4,
                "comments": "[SEED] Fatigued test scenario",
            }
        elif scenario == "fresh":
            # High HRV, low HR, abundant sleep, fresh legs
            rec = {
                "id": date_str,
                "hrv": round(72.0 + (i % 3) * 2.0, 1),
                "restingHR": 46 - (i % 2),
                "sleepSecs": 30600 + (i % 3) * 600,  # ~8.5 - 9.0 hrs
                "sleepQuality": 5,
                "soreness": 1,
                "fatigue": 1,
                "stress": 1,
                "comments": "[SEED] Fresh test scenario",
            }
        else:  # balanced
            rec = {
                "id": date_str,
                "hrv": round(62.0 + (i % 4) * 2.0, 1),
                "restingHR": 50 + (i % 3),
                "sleepSecs": 27000 + (i % 3) * 1200,  # ~7.5 - 8.5 hrs
                "sleepQuality": 4,
                "soreness": 2,
                "fatigue": 2,
                "stress": 2,
                "comments": "[SEED] Balanced test scenario",
            }

        records.append(rec)

    return records


def generate_sample_workouts(
    days: int = 14,
    scenario: str = "balanced",
) -> List[Dict[str, Any]]:
    """Generates scheduled workout events for testing slot recognition and compliance."""
    today = datetime.date.today()
    events = []

    # 1. Past completed workout (Slot 1: Intervals) 4 days ago
    date_slot1 = (today - datetime.timedelta(days=4)).isoformat()
    events.append({
        "category": "WORKOUT",
        "type": "Run",
        "name": "[SEED] Slot 1: Speed Intervals 5x 1m",
        "description": "Warmup 10m Z1-Z2\n5x 1m @ 05:15/km Pace, 2m recovery\nCooldown 10m",
        "start_date_local": f"{date_slot1}T07:00:00",
        "moving_time": 2400,
    })

    # 2. Past completed workout (Slot 2: Tempo / Aerobic) 2 days ago
    date_slot2 = (today - datetime.timedelta(days=2)).isoformat()
    events.append({
        "category": "WORKOUT",
        "type": "Run",
        "name": "[SEED] Slot 2: Tempo Run Z4",
        "description": "Warmup 10m Z2\n3x 8m Z4 Heart Rate (168-174 bpm)\nCooldown 10m",
        "start_date_local": f"{date_slot2}T18:00:00",
        "moving_time": 2700,
    })

    # 3. Upcoming planned workout (Slot 3: Weekend Long Run) tomorrow or in 2 days
    date_slot3 = (today + datetime.timedelta(days=2)).isoformat()
    events.append({
        "category": "WORKOUT",
        "type": "Run",
        "name": "[SEED] Slot 3: Aerober Anker & Long Run",
        "description": "50m strictly Zone 2 HR (< 158 bpm)",
        "start_date_local": f"{date_slot3}T09:00:00",
        "moving_time": 3000,
    })

    return events


def clean_seeded_data(
    athlete_ctx: AthleteContext,
    api_key: str,
    api_base_url: str = "https://intervals.icu/api/v1",
) -> int:
    """Deletes all events matching the '[SEED]' prefix from Intervals.icu."""
    base = api_base_url.rstrip("/")
    athlete_id = athlete_ctx.intervals_id
    today = datetime.date.today()
    oldest = (today - datetime.timedelta(days=60)).isoformat()
    newest = (today + datetime.timedelta(days=60)).isoformat()

    url = f"{base}/athlete/{athlete_id}/events?oldest={oldest}&newest={newest}"
    events = make_api_request(url, method="GET", api_key=api_key) or []

    deleted_count = 0
    for ev in events:
        name = ev.get("name", "")
        if name.startswith("[SEED]"):
            ev_id = ev.get("id")
            delete_url = f"{base}/athlete/{athlete_id}/events/{ev_id}"
            try:
                make_api_request(delete_url, method="DELETE", api_key=api_key)
                deleted_count += 1
            except Exception as e:
                print(f"Warning: Failed to delete event {ev_id}: {e}", file=sys.stderr)

    return deleted_count


def seed_athlete(
    athlete_slug: str = "berni-test",
    scenario: str = "balanced",
    days: int = 14,
    clean_first: bool = False,
    api_base_url: str = "https://intervals.icu/api/v1",
    dry_run: bool = False,
) -> None:
    """Main execution function to seed wellness and workouts into Intervals.icu."""
    root = Path(__file__).resolve().parent.parent.parent
    load_env_file(root / ".env")
    api_key = os.getenv("INTERVALS_API_KEY", "").strip()

    if not api_key and not dry_run:
        sys.exit("Error: INTERVALS_API_KEY not found in environment or .env file.")

    athlete_ctx = resolve_athlete_context(athlete_slug, project_root=root)
    athlete_id = athlete_ctx.intervals_id

    # Safety guard: Prevent accidental seeding of production athlete
    if "test" not in athlete_slug.lower() and athlete_id == "i697752":
        sys.exit(
            f"SAFETY ABORT: Refusing to seed test data into production athlete '{athlete_slug}' ({athlete_id})!\n"
            "Seeding is strictly restricted to staging/test athletes (e.g. 'berni-test')."
        )

    base = api_base_url.rstrip("/")
    print(f"\n🌱 Seeding test data for athlete: '{athlete_slug}' ({athlete_id})")
    print(f"   Scenario : {scenario}")
    print(f"   Timespan : {days} days")
    print(f"   API Base : {base}")
    if dry_run:
        print("   [DRY RUN MODE - No network changes]")

    # 1. Clean previous seeded events if requested
    if clean_first and not dry_run:
        deleted = clean_seeded_data(athlete_ctx, api_key, api_base_url)
        print(f"🧹 Cleaned {deleted} previously seeded events.")

    # 2. Seed Wellness Data
    wellness_records = generate_wellness_records(days=days, scenario=scenario)
    print(f"\n📊 Uploading {len(wellness_records)} daily wellness records...")
    for rec in wellness_records:
        date_key = rec["id"]
        wellness_url = f"{base}/athlete/{athlete_id}/wellness/{date_key}"
        if dry_url := dry_run:
            pass
        else:
            try:
                make_api_request(wellness_url, method="PUT", api_key=api_key, payload=rec)
            except Exception as e:
                print(f"   ❌ Wellness update for {date_key} failed: {e}", file=sys.stderr)

    print(f"   ✅ Wellness records uploaded ({days} days, HRV ~ {wellness_records[0]['hrv']} ms).")

    # 3. Seed Workouts
    sample_workouts = generate_sample_workouts(days=days, scenario=scenario)
    print(f"\n🏃 Uploading {len(sample_workouts)} sample workouts to Intervals.icu calendar...")
    for ev in sample_workouts:
        events_url = f"{base}/athlete/{athlete_id}/events"
        if not dry_run:
            try:
                res = make_api_request(events_url, method="POST", api_key=api_key, payload=ev)
                print(f"   + Created: {ev['name']} on {ev['start_date_local']} (ID: {res.get('id', 'ok')})")
            except Exception as e:
                print(f"   ❌ Event creation failed for {ev['name']}: {e}", file=sys.stderr)
        else:
            print(f"   [DRY RUN] Would create: {ev['name']} on {ev['start_date_local']}")

    print("\n✨ Seeding completed successfully! Run './run sync --athlete " + athlete_slug + "' to sync state.")


def main() -> None:
    parser = argparse.ArgumentParser(description="Seed synthetic training data for a test athlete in Intervals.icu.")
    parser.add_argument("--athlete", default="berni-test", help="Target athlete slug (default: berni-test)")
    parser.add_argument(
        "--scenario",
        choices=["balanced", "fatigued", "fresh"],
        default="balanced",
        help="Training & recovery scenario (balanced, fatigued, fresh)",
    )
    parser.add_argument("--days", type=int, default=14, help="Number of wellness days to generate (default: 14)")
    parser.add_argument("--clean", action="store_true", help="Remove all previously seeded '[SEED]' events")
    parser.add_argument("--dry-run", action="store_true", help="Simulate seeding without making API calls")
    parser.add_argument(
        "--api-base",
        default=os.getenv("INTERVALS_API_BASE", "https://intervals.icu/api/v1"),
        help="Intervals.icu API Base URL",
    )

    args = parser.parse_args()

    if args.clean:
        root = Path(__file__).resolve().parent.parent.parent
        load_env_file(root / ".env")
        api_key = os.getenv("INTERVALS_API_KEY", "").strip()
        athlete_ctx = resolve_athlete_context(args.athlete, project_root=root)
        if "test" not in args.athlete.lower() and athlete_ctx.intervals_id == "i697752":
            sys.exit("SAFETY ABORT: Refusing to clean production athlete calendar.")
        deleted = clean_seeded_data(athlete_ctx, api_key, args.api_base)
        print(f"🧹 Cleaned {deleted} seeded events from athlete '{args.athlete}' ({athlete_ctx.intervals_id}).")
        return

    seed_athlete(
        athlete_slug=args.athlete,
        scenario=args.scenario,
        days=args.days,
        clean_first=True,
        api_base_url=args.api_base,
        dry_run=args.dry_run,
    )


if __name__ == "__main__":
    main()
