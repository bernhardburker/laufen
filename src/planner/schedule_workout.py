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


def fetch_existing_events(
    api_key: str,
    athlete_id: str = "0",
    oldest: str = "2020-01-01",
    newest: str = "2030-01-01",
    api_base_url: str = "https://intervals.icu/api/v1",
) -> list[dict]:
    """Fetches planned events from Intervals.icu."""
    url = f"{api_base_url.rstrip('/')}/athlete/{athlete_id}/events?oldest={oldest}&newest={newest}"
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
            return json.loads(data) if data else []
    except Exception:
        return []


def find_next_date_for_days(allowed_days: list[str], start_date: datetime.date) -> datetime.date:
    """Finds the earliest date on or after start_date that matches one of the allowed day names."""
    allowed = [d.lower() for d in allowed_days]
    for i in range(7):
        candidate = start_date + datetime.timedelta(days=i)
        if candidate.strftime("%A").lower() in allowed:
            return candidate
    return start_date


def generate_weekly_plan_payloads(
    project_root: Path,
    athlete_profile: dict | None = None,
    ref_date: datetime.date | None = None,
) -> list[dict]:
    """Generates structured workout payloads for upcoming weekly slots based on athlete profile."""
    profile = athlete_profile or {}
    if not profile:
        profile_path = project_root / "config" / "athlete.json"
        if profile_path.is_file():
            try:
                profile = json.loads(profile_path.read_text(encoding="utf-8"))
            except Exception:
                profile = {}

    slots = profile.get("athlete", {}).get("weekly_slots", [])
    today = ref_date or datetime.date.today()
    templates_dir = project_root / "workouts" / "templates"
    payloads = []

    for slot in slots:
        slot_id = slot.get("slot") or slot.get("id") or 1
        role = slot.get("role", "base_anchor")
        allowed_days = slot.get("allowed_days", ["sunday"])
        target_date = find_next_date_for_days(allowed_days, today)
        target_date_iso = target_date.isoformat()

        if role == "quality":
            name = f"Slot {slot_id}: Tempo- & Intervalltraining (8x 1m)"
            desc = render_template(
                templates_dir / "intervals.txt",
                {
                    "warmup": "10m",
                    "reps": 8,
                    "work": "1m",
                    "work_target": "05:15/km Pace",
                    "recovery": "1m",
                    "recovery_target": "Z1 HR",
                    "cooldown": "10m",
                },
            )
            payloads.append({
                "category": "WORKOUT",
                "start_date_local": f"{target_date_iso}T07:00:00",
                "type": "Run",
                "name": name,
                "description": desc,
            })

        elif role in ("flexible", "adaptive_quality"):
            # Slot 2: Both options (Threshold and Easy Base) scheduled on the same date!
            name_a = f"Slot {slot_id}A: Kontrollierter Schwellenlauf (3x 8m Z4)"
            desc_a = render_template(
                templates_dir / "threshold_intervals.txt",
                {
                    "warmup": "10m",
                    "reps": 3,
                    "work": "8m",
                    "work_target": "Z4 HR",
                    "recovery": "2m",
                    "recovery_target": "Z1 HR",
                    "cooldown": "10m",
                },
            )
            payloads.append({
                "category": "WORKOUT",
                "start_date_local": f"{target_date_iso}T07:00:00",
                "type": "Run",
                "name": name_a,
                "description": desc_a,
            })

            name_b = f"Slot {slot_id}B: Aerobe Basis & Z2 Dauerlauf (45m)"
            desc_b = render_template(
                templates_dir / "easy_run.txt",
                {
                    "warmup": "10m",
                    "main": "30m",
                    "cooldown": "5m",
                    "target": "Z2 HR",
                },
            )
            payloads.append({
                "category": "WORKOUT",
                "start_date_local": f"{target_date_iso}T07:05:00",
                "type": "Run",
                "name": name_b,
                "description": desc_b,
            })

        elif role == "base_anchor":
            name = f"Slot {slot_id}: Aerober Anker & Long Run (50m Z2)"
            desc = render_template(
                templates_dir / "easy_run.txt",
                {
                    "warmup": "10m",
                    "main": "35m",
                    "cooldown": "5m",
                    "target": "Z2 HR",
                },
            )
            payloads.append({
                "category": "WORKOUT",
                "start_date_local": f"{target_date_iso}T07:00:00",
                "type": "Run",
                "name": name,
                "description": desc,
            })

    return payloads


def sync_weekly_plan(
    api_key: str,
    athlete_id: str = "0",
    athlete_profile: dict | None = None,
    ref_date: datetime.date | None = None,
    dry_run: bool = False,
    api_base_url: str = "https://intervals.icu/api/v1",
    project_root: Path | None = None,
) -> dict:
    """Synchronizes upcoming weekly workout slots to Intervals.icu calendar."""
    root = project_root or Path(__file__).resolve().parent.parent.parent
    payloads = generate_weekly_plan_payloads(root, athlete_profile, ref_date)

    if not payloads:
        return {"scheduled": [], "skipped": [], "errors": []}

    today = ref_date or datetime.date.today()
    oldest_str = today.isoformat()
    newest_str = (today + datetime.timedelta(days=8)).isoformat()

    existing_events = []
    if api_key and not dry_run:
        existing_events = fetch_existing_events(
            api_key=api_key,
            athlete_id=athlete_id,
            oldest=oldest_str,
            newest=newest_str,
            api_base_url=api_base_url,
        )

    existing_signatures = set()
    for ev in existing_events:
        d_str = str(ev.get("start_date_local") or "")[:10]
        ev_name = str(ev.get("name") or "").strip()
        if d_str and ev_name:
            existing_signatures.add((d_str, ev_name))

    results: dict = {"scheduled": [], "skipped": [], "errors": []}

    print("\n" + "=" * 60)
    print("  Wochen-Trainingsplan Synchronisation (Intervals.icu / Garmin)")
    print("=" * 60)

    for p in payloads:
        d_str = p["start_date_local"][:10]
        p_name = p["name"]
        if (d_str, p_name) in existing_signatures:
            print(f"[ÜBERSPRUNGEN] Bereits im Kalender: {d_str} | {p_name}")
            results["skipped"].append(p)
            continue

        if dry_run:
            print(f"[DRY-RUN] Plane Workout: {d_str} | {p_name}")
            results["scheduled"].append(p)
        else:
            try:
                res = send_workout_event(
                    payload=p,
                    api_key=api_key,
                    athlete_id=athlete_id,
                    api_base_url=api_base_url,
                )
                ev_id = res.get("id", "neu")
                print(f"[ANGELEGT] {d_str} | {p_name} (ID: {ev_id})")
                results["scheduled"].append(p)
            except Exception as e:
                print(f"[FEHLER] Konnte {p_name} nicht anlegen: {e}")
                results["errors"].append({"payload": p, "error": str(e)})

    print("=" * 60)
    return results


def main() -> None:
    project_root = Path(__file__).resolve().parent.parent.parent
    dotenv_path = os.getenv("DOTENV_PATH")
    load_env_file(Path(dotenv_path) if dotenv_path else project_root / ".env")

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
    parser.add_argument(
        "--sync-plan",
        action="store_true",
        help="Automatically generate and sync the weekly workout schedule to Intervals.icu calendar",
    )

    args, _ = parser.parse_known_args()

    if args.sync_plan:
        api_key = os.getenv("INTERVALS_API_KEY", "")
        if not api_key and not args.dry_run:
            sys.exit(
                "Fehler: INTERVALS_API_KEY ist weder in der Umgebung noch in .env gesetzt.\n"
                "Bitte via Shell exportieren oder in .env eintragen."
            )
        sync_weekly_plan(
            api_key=api_key,
            athlete_id=args.athlete_id,
            dry_run=args.dry_run,
            api_base_url=args.api_base,
            project_root=project_root,
        )
        return

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
