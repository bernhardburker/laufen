#!/usr/bin/env python3
"""download_intervals.py

Downloads running and fitness activities from Intervals.icu REST API
and saves them locally in the 'data/' directory.

Features:
- Downloads full activity history or customizable date ranges.
- Resolves API key from environment variable or .env file.
- Computes pace (min/km) and formats distances and heart rates.
- Uses only Python standard library (no external dependencies required).
"""

import argparse
import base64
import datetime
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path


def load_env_file(filepath: Path) -> None:
    """Loads key-value pairs from a .env file into os.environ if not already present."""
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


def format_duration(seconds: float | int | None) -> str:
    """Formats seconds into hh:mm:ss or mm:ss."""
    if seconds is None:
        return "--:--"
    total_secs = int(seconds)
    hours = total_secs // 3600
    minutes = (total_secs % 3600) // 60
    secs = total_secs % 60
    if hours > 0:
        return f"{hours:02d}:{minutes:02d}:{secs:02d}"
    return f"{minutes:02d}:{secs:02d}"


def format_pace(speed_m_per_s: float | None) -> str:
    """Converts speed (m/s) into running pace (min/km)."""
    if not speed_m_per_s or speed_m_per_s <= 0:
        return "--:--"
    sec_per_km = 1000.0 / speed_m_per_s
    minutes = int(sec_per_km // 60)
    seconds = int(sec_per_km % 60)
    return f"{minutes:02d}:{seconds:02d}/km"


def fetch_intervals_activities(
    api_key: str,
    athlete_id: str = "0",
    oldest: str = "2000-01-01",
    newest: str | None = None,
    api_base_url: str = "https://intervals.icu/api/v1",
    raise_on_error: bool = False,
) -> list[dict]:
    """Fetches activities from the Intervals.icu REST API."""
    if not newest:
        newest = (datetime.date.today() + datetime.timedelta(days=1)).isoformat()

    url = f"{api_base_url.rstrip('/')}/athlete/{athlete_id}/activities?oldest={oldest}&newest={newest}"

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
        if raise_on_error:
            raise
        error_msg = e.read().decode("utf-8", errors="replace")
        if e.code == 401 or e.code == 403:
            sys.exit(f"Error {e.code}: Authentication with Intervals.icu failed. Please verify INTERVALS_API_KEY.")
        sys.exit(f"HTTP Error {e.code} while fetching activities: {error_msg}")
    except urllib.error.URLError as e:
        if raise_on_error:
            raise
        sys.exit(f"Network error while connecting to Intervals.icu: {e.reason}")


def update_intervals_activity(
    activity_id: str,
    api_key: str,
    description: str | None = None,
    feel: int | None = None,
    perceived_exertion: int | None = None,
    api_base_url: str = "https://intervals.icu/api/v1",
    raise_on_error: bool = False,
) -> dict:
    """Updates activity metadata (description/notes, feel, RPE) on Intervals.icu via PUT /api/v1/activity/{id}."""
    clean_id = str(activity_id).strip()
    url = f"{api_base_url.rstrip('/')}/activity/{clean_id}"

    payload: dict = {}
    if description is not None:
        payload["description"] = description
    if feel is not None:
        payload["feel"] = int(feel)
    if perceived_exertion is not None:
        payload["perceived_exertion"] = int(perceived_exertion)

    auth_string = f"API_KEY:{api_key}"
    auth_header = "Basic " + base64.b64encode(auth_string.encode("utf-8")).decode("ascii")

    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=data,
        headers={
            "Authorization": auth_header,
            "Content-Type": "application/json",
            "Accept": "application/json",
            "User-Agent": "Mozilla/5.0 (compatible; LaufenSync/1.0)",
        },
        method="PUT",
    )

    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            raw = resp.read().decode("utf-8")
            return json.loads(raw) if raw else {"ok": True, "id": clean_id, **payload}
    except urllib.error.HTTPError as e:
        if raise_on_error:
            raise
        error_msg = e.read().decode("utf-8", errors="replace")
        return {"error": f"HTTP {e.code}: {error_msg}", "status": e.code}
    except urllib.error.URLError as e:
        if raise_on_error:
            raise
        return {"error": f"Network error: {e.reason}"}


def main() -> None:
    project_root = Path(__file__).resolve().parent.parent.parent
    dotenv_path = os.getenv("DOTENV_PATH")
    load_env_file(Path(dotenv_path) if dotenv_path else project_root / ".env")

    parser = argparse.ArgumentParser(
        description="Download activities from Intervals.icu and persist locally as JSON."
    )
    parser.add_argument(
        "--oldest",
        default="2000-01-01",
        help="Start date in YYYY-MM-DD format (default: 2000-01-01 for all activities)",
    )
    parser.add_argument(
        "--newest",
        default=None,
        help="End date in YYYY-MM-DD format (default: tomorrow)",
    )
    parser.add_argument(
        "--output",
        default=str(project_root / "data" / "intervals_activities.json"),
        help="Destination path for JSON export (default: data/intervals_activities.json)",
    )
    parser.add_argument(
        "--save-individual",
        action="store_true",
        help="Save each activity as individual JSON file in data/activities/",
    )
    parser.add_argument(
        "--athlete-id",
        default=os.getenv("INTERVALS_ATHLETE_ID", "0"),
        help="Athlete ID on Intervals.icu (default: '0' for authenticated user)",
    )
    parser.add_argument(
        "--type-filter",
        default=None,
        help="Filter by activity type (e.g. 'Run')",
    )
    parser.add_argument(
        "--api-base",
        default=os.getenv("INTERVALS_API_BASE", "https://intervals.icu/api/v1"),
        help="Base URL of Intervals.icu API (default: https://intervals.icu/api/v1)",
    )

    args = parser.parse_args()

    api_key = os.getenv("INTERVALS_API_KEY")
    if not api_key:
        sys.exit(
            "Error: INTERVALS_API_KEY is not set in environment or .env file.\n"
            "Please export it in your shell or add it to .env."
        )

    print(f"Connecting to Intervals.icu (Athlete ID: {args.athlete_id})...")
    activities = fetch_intervals_activities(
        api_key=api_key,
        athlete_id=args.athlete_id,
        oldest=args.oldest,
        newest=args.newest,
        api_base_url=args.api_base,
    )

    if args.type_filter:
        activities = [a for a in activities if str(a.get("type", "")).lower() == args.type_filter.lower()]

    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(activities, f, indent=2, ensure_ascii=False)

    print(f"Successfully downloaded {len(activities)} activities and saved to:")
    print(f"-> {output_path}")

    if args.save_individual:
        single_dir = output_path.parent / "activities"
        single_dir.mkdir(parents=True, exist_ok=True)
        for act in activities:
            act_id = act.get("id", "unknown")
            with open(single_dir / f"{act_id}.json", "w", encoding="utf-8") as f:
                json.dump(act, f, indent=2, ensure_ascii=False)
        print(f"-> Individual activity files saved in: {single_dir}")

    print("\nSummary of recent 10 activities:")
    header = f"{'Date':<12} | {'Type':<6} | {'Distance':>8} | {'Duration':>8} | {'Pace':>9} | {'Avg HR':>6} | {'Max HR':>8} | {'Title'}"
    print("-" * len(header))
    print(header)
    print("-" * len(header))

    sorted_acts = sorted(activities, key=lambda x: str(x.get("start_date_local", "")), reverse=True)

    total_run_dist_m = 0.0
    total_run_time_s = 0.0
    run_count = 0

    for act in sorted_acts[:10]:
        date_str = str(act.get("start_date_local", ""))[:10]
        act_type = str(act.get("type", "--"))[:6]
        dist_m = act.get("distance")
        dist_km_str = f"{dist_m / 1000.0:.2f} km" if dist_m is not None else "-- km"
        dur_str = format_duration(act.get("moving_time") or act.get("elapsed_time"))
        pace_str = format_pace(act.get("average_speed"))
        avg_hr = str(act.get("average_heartrate") or "--")
        max_hr = str(act.get("max_heartrate") or "--")
        name = str(act.get("name", ""))[:25]

        print(f"{date_str:<12} | {act_type:<6} | {dist_km_str:>8} | {dur_str:>8} | {pace_str:>9} | {avg_hr:>6} | {max_hr:>8} | {name}")

    for act in activities:
        if str(act.get("type", "")).lower() == "run":
            dist = act.get("distance")
            time_s = act.get("moving_time")
            if dist:
                total_run_dist_m += dist
            if time_s:
                total_run_time_s += time_s
            run_count += 1

    print("-" * len(header))
    if run_count > 0:
        total_km = total_run_dist_m / 1000.0
        avg_run_speed = total_run_dist_m / total_run_time_s if total_run_time_s > 0 else 0
        overall_pace = format_pace(avg_run_speed)
        print(f"Total Runs: {run_count} | Total Distance: {total_km:.1f} km | Total Duration: {format_duration(total_run_time_s)} | Avg Pace: {overall_pace}")


if __name__ == "__main__":
    main()
