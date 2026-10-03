#!/usr/bin/env python3
"""
schedule_workout.py

Schedules structured workouts on Intervals.icu via REST API.
Intervals.icu automatically syncs upcoming workouts to Garmin Connect
(and Wahoo, Coros, Suunto) if "Upload planned workouts" is enabled.

Key Capabilities:
- Template-driven generation for workout types (easy_run, intervals, tempo_run, etc.)
- Direct raw workout text or file support
- Relative dates ('today', 'tomorrow') or ISO dates ('YYYY-MM-DD')
- Dry-run mode for inspection without network calls
- Pure standard library (no external dependencies)
"""

import argparse
import base64
import datetime
import json
import os
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path


def load_env_file(filepath: Path) -> None:
    """Loads environment variables from a .env file if not already set."""
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


def parse_minutes(val: str) -> int:
    """Parses a time string like '45m', '45min', '1h', or '45' into minutes."""
    val = val.strip().lower()
    if val.endswith("h"):
        return int(float(val[:-1]) * 60)
    if val.endswith("min"):
        return int(val[:-3])
    if val.endswith("m"):
        return int(val[:-1])
    return int(val)


def parse_target_date(date_str: str) -> str:
    """Resolves relative date strings ('today', 'tomorrow') or validates YYYY-MM-DD."""
    date_str = date_str.strip().lower()
    today = datetime.date.today()
    if date_str == "today":
        return today.isoformat()
    if date_str == "tomorrow":
        return (today + datetime.timedelta(days=1)).isoformat()

    # Validate ISO format YYYY-MM-DD
    try:
        parsed = datetime.date.fromisoformat(date_str)
        return parsed.isoformat()
    except ValueError:
        raise ValueError(f"Invalid date format: '{date_str}'. Expected 'today', 'tomorrow' or 'YYYY-MM-DD'.")


def render_template(template_path: Path, variables: dict[str, str]) -> str:
    """Reads a template file and interpolates variables formatted as {var_name}."""
    if not template_path.is_file():
        raise FileNotFoundError(f"Template not found at: {template_path}")
    content = template_path.read_text(encoding="utf-8")
    for key, val in variables.items():
        content = content.replace(f"{{{key}}}", str(val))
    return content.strip() + "\n"


def build_workout_payload(args: argparse.Namespace, project_root: Path) -> dict:
    """Constructs the event dictionary containing name, description, and start date."""
    target_date = parse_target_date(args.date)
    start_time = args.time if ":" in args.time else f"{args.time}:00"
    if len(start_time.split(":")) == 2:
        start_time = f"{start_time}:00"
    start_date_local = f"{target_date}T{start_time}"

    templates_dir = project_root / "workouts" / "templates"

    name = args.name
    description = ""

    if args.raw_text:
        description = args.raw_text.strip() + "\n"
        if not name:
            name = "Workout"
    elif args.raw_file:
        file_path = Path(args.raw_file)
        if not file_path.is_file():
            sys.exit(f"Error: Raw workout file not found: {file_path}")
        description = file_path.read_text(encoding="utf-8").strip() + "\n"
        if not name:
            name = file_path.stem.replace("_", " ").title()
    elif args.template:
        tmpl_file = Path(args.template)
        description = tmpl_file.read_text(encoding="utf-8").strip() + "\n"
        if not name:
            name = tmpl_file.stem.replace("_", " ").title()
    elif args.type:
        w_type = args.type.lower()
        if w_type == "easy_run":
            warmup = args.warmup or "10m"
            cooldown = args.cooldown or "5m"
            target = args.target or "Z2 HR"
            total_dur = args.duration or "45m"
            
            tot_min = parse_minutes(total_dur)
            warm_min = parse_minutes(warmup)
            cool_min = parse_minutes(cooldown)
            main_min = max(5, tot_min - warm_min - cool_min)
            main_str = f"{main_min}m"

            if not name:
                name = f"Easy Run ({total_dur})"
            description = render_template(
                templates_dir / "easy_run.txt",
                {"warmup": warmup, "main": main_str, "cooldown": cooldown, "target": target}
            )

        elif w_type == "recovery_run":
            duration = args.duration or "30m"
            target = args.target or "Z1 HR"
            if not name:
                name = f"Recovery Run ({duration})"
            description = render_template(
                templates_dir / "recovery_run.txt",
                {"duration": duration, "target": target}
            )

        elif w_type == "long_run":
            warmup = args.warmup or "10m"
            cooldown = args.cooldown or "5m"
            target = args.target or "Z2 HR"
            total_dur = args.duration or "75m"

            tot_min = parse_minutes(total_dur)
            warm_min = parse_minutes(warmup)
            cool_min = parse_minutes(cooldown)
            main_min = max(10, tot_min - warm_min - cool_min)
            main_str = f"{main_min}m"

            if not name:
                name = f"Long Run ({total_dur})"
            description = render_template(
                templates_dir / "long_run.txt",
                {"warmup": warmup, "main": main_str, "cooldown": cooldown, "target": target}
            )

        elif w_type == "intervals":
            warmup = args.warmup or "12m"
            cooldown = args.cooldown or "10m"
            reps = args.reps or 4
            work = args.work or "1km"
            work_target = args.target or "Z4 HR"
            recovery = args.recovery or "2m"
            recovery_target = args.recovery_target or "Z1 HR"

            if not name:
                name = f"{reps}x {work} Intervals"
            description = render_template(
                templates_dir / "intervals.txt",
                {
                    "warmup": warmup,
                    "reps": reps,
                    "work": work,
                    "work_target": work_target,
                    "recovery": recovery,
                    "recovery_target": recovery_target,
                    "cooldown": cooldown,
                }
            )

        elif w_type == "tempo_run":
            warmup = args.warmup or "10m"
            cooldown = args.cooldown or "10m"
            target = args.target or "Z3-Z4 HR"
            main_str = args.duration or "20m"

            if not name:
                name = f"Tempo Run ({main_str})"
            description = render_template(
                templates_dir / "tempo_run.txt",
                {"warmup": warmup, "main": main_str, "cooldown": cooldown, "target": target}
            )

        elif w_type == "threshold_intervals":
            warmup = args.warmup or "12m"
            cooldown = args.cooldown or "10m"
            reps = args.reps or 3
            work = args.work or "10m"
            work_target = args.target or "Z4 HR"
            recovery = args.recovery or "3m"
            recovery_target = args.recovery_target or "Z1 HR"

            if not name:
                name = f"{reps}x {work} Threshold Intervals"
            description = render_template(
                templates_dir / "threshold_intervals.txt",
                {
                    "warmup": warmup,
                    "reps": reps,
                    "work": work,
                    "work_target": work_target,
                    "recovery": recovery,
                    "recovery_target": recovery_target,
                    "cooldown": cooldown,
                }
            )

        elif w_type == "fartlek":
            warmup = args.warmup or "10m"
            cooldown = args.cooldown or "5m"
            reps = args.reps or 6
            fast_dur = args.work or "1m"
            fast_target = args.target or "Z4 HR"
            slow_dur = args.recovery or "2m"
            slow_target = args.recovery_target or "Z1 HR"

            if not name:
                name = f"Fartlek {reps}x ({fast_dur}/{slow_dur})"
            description = render_template(
                templates_dir / "fartlek.txt",
                {
                    "warmup": warmup,
                    "reps": reps,
                    "fast_dur": fast_dur,
                    "fast_target": fast_target,
                    "slow_dur": slow_dur,
                    "slow_target": slow_target,
                    "cooldown": cooldown,
                }
            )
        else:
            sys.exit(f"Error: Unknown workout type: '{w_type}'.")
    else:
        sys.exit("Error: Must specify either --type, --raw-text, --raw-file, or --template.")

    return {
        "category": "WORKOUT",
        "start_date_local": start_date_local,
        "type": "Run",
        "name": name,
        "description": description,
    }


def send_workout_event(
    payload: dict,
    api_key: str,
    athlete_id: str = "0",
    api_base_url: str = "https://intervals.icu/api/v1",
) -> dict:
    """Sends the workout payload to POST /api/v1/athlete/{athlete_id}/events."""
    url = f"{api_base_url.rstrip('/')}/athlete/{athlete_id}/events"

    auth_string = f"API_KEY:{api_key}"
    auth_header = "Basic " + base64.b64encode(auth_string.encode("utf-8")).decode("ascii")

    req_data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=req_data,
        headers={
            "Authorization": auth_header,
            "Content-Type": "application/json",
            "User-Agent": "Mozilla/5.0 (compatible; LaufenSync/1.0)",
            "Accept": "application/json",
        },
        method="POST",
    )

    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = resp.read().decode("utf-8")
            return json.loads(data) if data else {}
    except urllib.error.HTTPError as e:
        error_msg = e.read().decode("utf-8", errors="replace")
        if e.code in (401, 403):
            sys.exit(f"Error {e.code}: Authentication with Intervals.icu failed. Please verify INTERVALS_API_KEY.")
        sys.exit(f"HTTP Error {e.code} while creating workout event: {error_msg}")
    except urllib.error.URLError as e:
        sys.exit(f"Network error connecting to Intervals.icu: {e.reason}")


def main() -> None:
    project_root = Path(__file__).resolve().parent.parent.parent
    load_env_file(project_root / ".env")

    parser = argparse.ArgumentParser(
        description="Creates and schedules structured workouts on Intervals.icu."
    )
    # Target scheduling
    parser.add_argument(
        "--date",
        default="tomorrow",
        help="Date for the workout ('today', 'tomorrow', or 'YYYY-MM-DD', default: 'tomorrow')",
    )
    parser.add_argument(
        "--time",
        default="07:00:00",
        help="Start time in HH:MM or HH:MM:SS format (default: '07:00:00')",
    )
    parser.add_argument(
        "--name",
        default=None,
        help="Custom title of the workout (default: auto-generated from type)",
    )

    # Workout specification
    parser.add_argument(
        "--type",
        choices=["easy_run", "recovery_run", "long_run", "intervals", "tempo_run", "threshold_intervals", "fartlek"],
        help="Predefined workout archetype",
    )
    parser.add_argument(
        "--duration",
        help="Overall or main duration (e.g., '45m', '60m', '1h', '20m')",
    )
    parser.add_argument(
        "--reps",
        type=int,
        help="Number of repetitions for intervals / fartlek",
    )
    parser.add_argument(
        "--work",
        help="Work interval duration or distance (e.g., '1km', '800mtr', '4m')",
    )
    parser.add_argument(
        "--recovery",
        help="Recovery interval duration or distance (e.g., '2m', '90s', '400mtr')",
    )
    parser.add_argument(
        "--target",
        help="Target heart rate zone or pace (e.g., 'Z2 HR', 'Z4 HR', '05:00/km Pace')",
    )
    parser.add_argument(
        "--recovery-target",
        help="Target for recovery segments (default: 'Z1 HR')",
    )
    parser.add_argument(
        "--warmup",
        help="Warmup duration (e.g., '10m', '12m')",
    )
    parser.add_argument(
        "--cooldown",
        help="Cooldown duration (e.g., '5m', '10m')",
    )

    # Raw custom inputs
    parser.add_argument(
        "--raw-text",
        help="Direct workout description in Intervals.icu text DSL",
    )
    parser.add_argument(
        "--raw-file",
        help="Path to file containing workout description",
    )
    parser.add_argument(
        "--template",
        help="Path to custom template file",
    )

    # API configuration
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print the generated workout and payload without sending it to Intervals.icu",
    )
    parser.add_argument(
        "--athlete-id",
        default=os.getenv("INTERVALS_ATHLETE_ID", "0"),
        help="Athlete ID on Intervals.icu (default: '0' for authenticated user)",
    )
    parser.add_argument(
        "--api-base",
        default=os.getenv("INTERVALS_API_BASE", "https://intervals.icu/api/v1"),
        help="Base URL of Intervals.icu API (default: https://intervals.icu/api/v1)",
    )

    args = parser.parse_args()

    payload = build_workout_payload(args, project_root)

    print("=" * 60)
    print(f"Workout: {payload['name']}")
    print(f"Datum  : {payload['start_date_local']}")
    print(f"Typ    : {payload['type']}")
    print("-" * 60)
    print("Workout-Struktur:")
    print(payload["description"].rstrip())
    print("=" * 60)

    if args.dry_run:
        print("[DRY-RUN] Kein Upload durchgeführt. Payload JSON:")
        print(json.dumps(payload, indent=2, ensure_ascii=False))
        return

    api_key = os.getenv("INTERVALS_API_KEY")
    if not api_key:
        sys.exit(
            "Fehler: INTERVALS_API_KEY ist weder in der Umgebung noch in .env gesetzt.\n"
            "Bitte via Shell exportieren oder in .env eintragen."
        )

    print(f"Übertrage Workout an Intervals.icu (Athlete ID: {args.athlete_id})...")
    res = send_workout_event(
        payload=payload,
        api_key=api_key,
        athlete_id=args.athlete_id,
        api_base_url=args.api_base,
    )

    event_id = res.get("id", "neu")
    print(f"Erfolgreich angelegt! Event-ID: {event_id}")
    print("-> Das Workout wird bei nächster Gelegenheit zur Garmin-Uhr synchronisiert.")


if __name__ == "__main__":
    main()
