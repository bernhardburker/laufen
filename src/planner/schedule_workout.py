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


def delete_workout_event(
    event_id: str | int,
    api_key: str,
    athlete_id: str = "0",
    api_base_url: str = "https://intervals.icu/api/v1",
) -> bool:
    """Deletes a workout event from Intervals.icu calendar."""
    url = f"{api_base_url.rstrip('/')}/athlete/{athlete_id}/events/{event_id}"
    auth_string = f"API_KEY:{api_key}"
    auth_header = "Basic " + base64.b64encode(auth_string.encode("utf-8")).decode("ascii")

    req = urllib.request.Request(
        url,
        headers={
            "Authorization": auth_header,
            "User-Agent": "Mozilla/5.0 (compatible; LaufenSync/1.0)",
            "Accept": "application/json",
        },
        method="DELETE",
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return resp.status in (200, 204)
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return True
        print(f"Warning: Failed to delete event {event_id}: HTTP {e.code}")
        return False
    except Exception as e:
        print(f"Warning: Failed to delete event {event_id}: {e}")
        return False


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


def choose_adaptive_workout(
    activities: list[dict],
    athlete_profile: dict,
    ref_date: datetime.date,
    days_since_last_run: int,
    templates_dir: Path,
) -> tuple[str, str]:
    """Returns (workout_name, workout_description) adapted to current recovery state."""
    # 1. Long inactivity break -> gentle re-entry
    if days_since_last_run >= 10:
        name = "Easy Run - Sanfter Wiedereinstieg (30m)"
        desc = render_template(
            templates_dir / "easy_run.txt",
            {"warmup": "5m", "main": "20m", "cooldown": "5m", "target": "Z2 HR"},
        )
        return name, desc

    # 2. Inspect recent runs to determine balance between Quality and Base
    runs = [a for a in activities if a.get("type") == "Run"]
    sorted_runs = sorted(
        runs,
        key=lambda r: str(r.get("start_date_local") or r.get("date") or ""),
        reverse=True,
    )
    last_run = sorted_runs[0] if sorted_runs else None

    # Check if last run was quality
    last_was_quality = False
    if last_run:
        r_type = str(last_run.get("workout_type") or "").lower()
        r_name = str(last_run.get("name") or "").lower()
        if any(k in r_name or k in r_type for k in ("interval", "tempo", "schwelle", "threshold", "speed")):
            last_was_quality = True
        else:
            z_times = last_run.get("icu_hr_zone_times") or []
            if len(z_times) >= 5 and (sum(z_times[4:]) > 180 or (len(z_times) > 3 and z_times[3] > 480)):
                last_was_quality = True

    if last_was_quality:
        # After quality session, schedule Base Anchor (Slot 3)
        name = "Aerober Anker & Grundlagenlauf (45m Z2)"
        desc = render_template(
            templates_dir / "easy_run.txt",
            {"warmup": "10m", "main": "30m", "cooldown": "5m", "target": "Z2 HR"},
        )
        return name, desc
    else:
        # After base session, schedule Quality / Threshold (Slot 1 or 2)
        name = "Kontrollierter Schwellenlauf (3x 8m Z4)"
        desc = render_template(
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
        return name, desc


def sync_adaptive_plan(
    api_key: str,
    athlete_id: str = "0",
    athlete_profile: dict | None = None,
    activities: list[dict] | None = None,
    ref_date: datetime.date | None = None,
    dry_run: bool = False,
    api_base_url: str = "https://intervals.icu/api/v1",
    project_root: Path | None = None,
    clean_past: bool = True,
    activities_file: Path | None = None,
) -> dict:
    """Dynamically plans next workout, cleans up past uncompleted events, and respects rest days."""
    root = project_root or Path(__file__).resolve().parent.parent.parent
    today = ref_date or datetime.date.today()
    today_iso = today.isoformat()

    # Load profile
    profile = athlete_profile or {}
    if not profile:
        profile_path = root / "config" / "athlete.json"
        if profile_path.is_file():
            try:
                profile = json.loads(profile_path.read_text(encoding="utf-8"))
            except Exception:
                profile = {}

    # Load activities
    act_list = activities
    if act_list is None:
        act_file = activities_file or (root / "data" / "intervals_activities.json")
        if act_file.is_file():
            try:
                act_list = json.loads(act_file.read_text(encoding="utf-8"))
            except Exception:
                act_list = []
        else:
            act_list = []

    # Calculate days since last run
    runs = [a for a in act_list if a.get("type") == "Run"]
    run_dates = []
    for r in runs:
        d_str = str(r.get("start_date_local") or r.get("date") or "")[:10]
        if d_str:
            try:
                run_dates.append(datetime.date.fromisoformat(d_str))
            except ValueError:
                pass
    run_dates.sort(reverse=True)
    last_run_date = run_dates[0] if run_dates else None
    days_since_last_run = (today - last_run_date).days if last_run_date else 999

    # Fetch existing events
    oldest_str = (today - datetime.timedelta(days=7)).isoformat()
    newest_str = (today + datetime.timedelta(days=7)).isoformat()
    existing_events = []
    if api_key and not dry_run:
        existing_events = fetch_existing_events(
            api_key=api_key,
            athlete_id=athlete_id,
            oldest=oldest_str,
            newest=newest_str,
            api_base_url=api_base_url,
        )

    results: dict = {
        "cleaned": [],
        "scheduled": [],
        "skipped": [],
        "target_date": "",
        "is_rest_day": False,
    }

    print("\n" + "=" * 60)
    print("  Rollierender Adaptiver Trainingsplaner (Intervals.icu / Garmin)")
    print("=" * 60)

    # Step 1: Clean up past uncompleted workouts (strictly before today)
    remaining_events = []
    for ev in existing_events:
        ev_id = ev.get("id")
        ev_date = str(ev.get("start_date_local") or "")[:10]
        ev_cat = ev.get("category")
        is_paired = bool(ev.get("paired_activity_id"))

        if clean_past and ev_cat == "WORKOUT" and ev_date < today_iso and not is_paired:
            if not dry_run and api_key and ev_id:
                delete_workout_event(ev_id, api_key, athlete_id, api_base_url)
            print(f"[AUFGERÄUMT] Veraltetes unvollendetes Workout gelöscht: {ev_date} | {ev.get('name')} (ID: {ev_id})")
            results["cleaned"].append(ev)
        else:
            remaining_events.append(ev)

    # Step 2: Determine rest day vs training day
    # If athlete ran yesterday (or today), today is a rest day!
    is_rest_day = (days_since_last_run <= 1)
    results["is_rest_day"] = is_rest_day

    if is_rest_day:
        target_date = today + datetime.timedelta(days=1)
        print(f"[STATUS] Letzter Lauf: {last_run_date} ({days_since_last_run} Tag(e) her).")
        print(f"[STATUS] Heute ({today_iso}) ist Ruhetag. Nächstes Training wird für MORGEN ({target_date.isoformat()}) geplant.")
        # If today has an uncompleted planned workout, remove it as today is a rest day
        final_remaining = []
        for ev in remaining_events:
            ev_date = str(ev.get("start_date_local") or "")[:10]
            if clean_past and ev_date == today_iso and ev.get("category") == "WORKOUT" and not ev.get("paired_activity_id"):
                if not dry_run and api_key and ev.get("id"):
                    delete_workout_event(ev["id"], api_key, athlete_id, api_base_url)
                print(f"[AUFGERÄUMT] Heutiges Workout entfernt (da Ruhetag): {ev.get('name')}")
                results["cleaned"].append(ev)
            else:
                final_remaining.append(ev)
        remaining_events = final_remaining
    else:
        # Check if tomorrow already has a scheduled workout
        tomorrow_iso = (today + datetime.timedelta(days=1)).isoformat()
        existing_tomorrow = [
            ev for ev in remaining_events
            if str(ev.get("start_date_local") or "")[:10] == tomorrow_iso and ev.get("category") == "WORKOUT"
        ]
        if existing_tomorrow:
            ev_name = existing_tomorrow[0].get("name", "Workout")
            print(f"[STATUS] Für morgen ({tomorrow_iso}) ist bereits ein Training geplant: '{ev_name}'.")
            print(f"[STATUS] Heute ({today_iso}) bleibt Ruhetag vor dem geplanten Training.")
            results["skipped"].append(existing_tomorrow[0])
            results["target_date"] = tomorrow_iso
            print("=" * 60)
            return results

        target_date = today
        print(f"[STATUS] Letzter Lauf: {last_run_date} ({days_since_last_run} Tag(e) her).")
        print(f"[STATUS] Heute ({today_iso}) ist Trainingstag!")

    results["target_date"] = target_date.isoformat()
    target_date_iso = target_date.isoformat()

    # Step 3: Check if target_date already has a planned workout
    existing_on_target = [
        ev for ev in remaining_events
        if str(ev.get("start_date_local") or "")[:10] == target_date_iso and ev.get("category") == "WORKOUT"
    ]

    if existing_on_target:
        ev_name = existing_on_target[0].get("name", "Workout")
        print(f"[BEREITS GEPLANT] Für {target_date_iso} existiert bereits ein Workout: {ev_name}")
        results["skipped"].append(existing_on_target[0])
        print("=" * 60)
        return results

    # Step 4: Generate adaptive workout payload
    templates_dir = root / "workouts" / "templates"
    name, description = choose_adaptive_workout(
        act_list, profile, target_date, days_since_last_run, templates_dir
    )

    payload = {
        "category": "WORKOUT",
        "start_date_local": f"{target_date_iso}T07:00:00",
        "type": "Run",
        "name": name,
        "description": description,
    }

    if dry_run:
        print(f"[DRY-RUN] Plane adaptives Workout: {target_date_iso} | {name}")
        results["scheduled"].append(payload)
    else:
        try:
            res = send_workout_event(
                payload=payload,
                api_key=api_key,
                athlete_id=athlete_id,
                api_base_url=api_base_url,
            )
            ev_id = res.get("id", "neu")
            print(f"[ANGELEGT] {target_date_iso} | {name} (ID: {ev_id})")
            results["scheduled"].append(payload)
        except Exception as e:
            print(f"[FEHLER] Konnte adaptives Workout nicht anlegen: {e}")
            results.setdefault("errors", []).append({"payload": payload, "error": str(e)})

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
    parser.add_argument(
        "--adaptive-plan",
        "--adaptive",
        action="store_true",
        help="Dynamically plan workouts based on rest days and clean up uncompleted past workouts",
    )
    parser.add_argument(
        "--ref-date",
        help="Reference date for calculations (defaults to today, format YYYY-MM-DD)",
    )
    parser.add_argument(
        "--activities-file",
        help="Path to JSON file containing activities (defaults to data/intervals_activities.json)",
    )
    parser.add_argument(
        "--no-clean-past",
        action="store_true",
        help="Disable automatic deletion of uncompleted past workouts",
    )

    args, _ = parser.parse_known_args()

    if args.adaptive_plan:
        api_key = os.getenv("INTERVALS_API_KEY", "")
        if not api_key and not args.dry_run:
            sys.exit(
                "Fehler: INTERVALS_API_KEY ist weder in der Umgebung noch in .env gesetzt.\n"
                "Bitte via Shell exportieren oder in .env eintragen."
            )
        parsed_ref_date = None
        if args.ref_date:
            try:
                parsed_ref_date = datetime.date.fromisoformat(args.ref_date)
            except ValueError:
                sys.exit(f"Ungültiges Datumsformat für --ref-date: '{args.ref_date}'. Erwartet: YYYY-MM-DD.")
        sync_adaptive_plan(
            api_key=api_key,
            athlete_id=args.athlete_id,
            ref_date=parsed_ref_date,
            dry_run=args.dry_run,
            api_base_url=args.api_base,
            project_root=project_root,
            clean_past=not args.no_clean_past,
            activities_file=Path(args.activities_file) if args.activities_file else None,
        )
        return

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
