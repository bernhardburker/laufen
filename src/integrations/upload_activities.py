#!/usr/bin/env python3
"""
upload_activities.py

Uploads activity files (FIT, TCX, GPX) from an unpacked Strava export
or archive directory to Intervals.icu via its REST API.

Features:
- Reads API key and Athlete ID from environment variables or .env file.
- Reads activities.csv to locate activity files and metadata.
- Automatically skips activities that already have complete data in Intervals.icu.
- Uploads files using standard multipart/form-data without external dependencies.
- Deduplicates and handles API responses (200 OK / 201 Created).
"""

import argparse
import base64
import csv
import datetime
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path


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


def parse_export_csv(activities_csv_path: Path) -> list[dict]:
    """Parses activities.csv from the Strava export and returns normalized records."""
    if not activities_csv_path.is_file():
        raise FileNotFoundError(f"Export CSV not found at: {activities_csv_path}")

    records = []
    with open(activities_csv_path, "r", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            # German Strava export column names
            act_id = row.get("Aktivitäts-ID") or row.get("Activity ID")
            act_date = row.get("Aktivitätsdatum") or row.get("Activity Date")
            act_name = row.get("Name der Aktivität") or row.get("Activity Name") or "Activity"
            act_type = row.get("Aktivitätsart") or row.get("Activity Type") or "Run"
            filename = row.get("Dateiname") or row.get("Filename")
            distance = row.get("Distanz") or row.get("Distance") or "0"

            if not act_id or not act_date:
                continue

            # Parse date (format: '02.10.2026, 05:33:11' or ISO)
            parsed_dt = None
            try:
                parsed_dt = datetime.datetime.strptime(act_date, "%d.%m.%Y, %H:%M:%S")
            except ValueError:
                try:
                    parsed_dt = datetime.datetime.fromisoformat(act_date.replace("Z", "+00:00"))
                except ValueError:
                    pass

            records.append({
                "id": str(act_id),
                "date_str": act_date,
                "datetime": parsed_dt,
                "name": act_name,
                "sport": act_type,
                "filename": filename,
                "distance": distance,
            })
    return records


def get_existing_interval_dates(intervals_json_path: Path) -> set[str]:
    """Returns a set of dates (YYYY-MM-DD) for activities that have valid data in Intervals."""
    if not intervals_json_path.is_file():
        return set()

    try:
        with open(intervals_json_path, "r", encoding="utf-8") as f:
            activities = json.load(f)
    except Exception:
        return set()

    existing_dates = set()
    for act in activities:
        # If distance or moving_time is present and > 0, the activity has real data
        dist = act.get("distance")
        moving = act.get("moving_time")
        if (dist and float(dist) > 0) or (moving and int(moving) > 0):
            start_local = act.get("start_date_local")
            if start_local:
                existing_dates.add(start_local[:10])
    return existing_dates


def upload_activity_file(
    file_path: Path,
    api_key: str,
    athlete_id: str = "0",
    api_base_url: str = "https://intervals.icu/api/v1",
    name: str | None = None,
) -> tuple[int, dict | str]:
    """Uploads an activity file (FIT, TCX, GPX, etc.) to Intervals.icu."""
    boundary = "----WebKitFormBoundary" + os.urandom(16).hex()
    file_bytes = file_path.read_bytes()
    filename = file_path.name

    body = (
        f"--{boundary}\r\n"
        f"Content-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n"
        f"Content-Type: application/octet-stream\r\n\r\n"
    ).encode("utf-8") + file_bytes + f"\r\n--{boundary}--\r\n".encode("utf-8")

    query = f"?name={urllib.parse.quote(name)}" if name else ""
    url = f"{api_base_url.rstrip('/')}/athlete/{athlete_id}/activities{query}"
    auth_header = "Basic " + base64.b64encode(f"API_KEY:{api_key}".encode("utf-8")).decode("ascii")

    req = urllib.request.Request(
        url,
        data=body,
        headers={
            "Authorization": auth_header,
            "Content-Type": f"multipart/form-data; boundary={boundary}",
            "User-Agent": "LaufenSync/1.0",
        },
        method="POST",
    )

    try:
        with urllib.request.urlopen(req, timeout=45) as resp:
            content = resp.read().decode("utf-8")
            try:
                data = json.loads(content)
            except Exception:
                data = content
            return resp.status, data
    except urllib.error.HTTPError as e:
        err_msg = e.read().decode("utf-8", errors="replace")
        return e.code, err_msg


def main() -> None:
    project_root = Path(__file__).resolve().parent.parent.parent
    dotenv_path = os.getenv("DOTENV_PATH")
    load_env_file(Path(dotenv_path) if dotenv_path else project_root / ".env")

    parser = argparse.ArgumentParser(
        description="Uploads Strava export activities to Intervals.icu via REST API."
    )
    parser.add_argument(
        "--export-dir",
        default=str(project_root / "data" / "strava_export"),
        help="Path to unpacked Strava export directory (default: data/strava_export)",
    )
    parser.add_argument(
        "--intervals-json",
        default=str(project_root / "data" / "intervals_activities.json"),
        help="Path to intervals_activities.json to skip already synced activities",
    )
    parser.add_argument(
        "--athlete-id",
        default=os.getenv("INTERVALS_ATHLETE_ID", "0"),
        help="Intervals.icu Athlete ID (default: '0')",
    )
    parser.add_argument(
        "--api-base",
        default=os.getenv("INTERVALS_API_BASE", "https://intervals.icu/api/v1"),
        help="Intervals.icu API Base URL",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=None,
        help="Limit number of uploaded files",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="Upload even if activity already appears synced in local cache",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Show what would be uploaded without sending files",
    )

    args = parser.parse_args()

    api_key = os.getenv("INTERVALS_API_KEY")
    if not api_key and not args.dry_run:
        sys.exit(
            "Fehler: INTERVALS_API_KEY ist weder in der Umgebung noch in .env gesetzt.\n"
            "Bitte via Shell exportieren oder in .env eintragen."
        )

    export_path = Path(args.export_dir)
    csv_path = export_path / "activities.csv"
    if not csv_path.exists():
        sys.exit(f"Fehler: Keine activities.csv unter {export_path} gefunden.")

    records = parse_export_csv(csv_path)
    existing_dates = get_existing_interval_dates(Path(args.intervals_json))

    # Filter candidates
    candidates = []
    skipped_existing = 0
    skipped_no_file = 0

    for r in records:
        if not r["filename"]:
            skipped_no_file += 1
            continue

        file_path = export_path / r["filename"]
        if not file_path.exists():
            skipped_no_file += 1
            continue

        dt = r["datetime"]
        date_str = dt.strftime("%Y-%m-%d") if dt else None

        if not args.force and date_str and date_str in existing_dates:
            skipped_existing += 1
            continue

        candidates.append((r, file_path))

    # Sort chronologically
    candidates.sort(key=lambda c: c[0]["datetime"] or datetime.datetime.min)

    if args.limit:
        candidates = candidates[: args.limit]

    print("======================================================================")
    print("  Intervals.icu - Activity File Uploader")
    print("======================================================================")
    print(f"Export-Verzeichnis:      {export_path}")
    print(f"Gefundene Einträge:      {len(records)}")
    print(f"Bereits vorhanden:       {skipped_existing}")
    print(f"Ohne Datei (manuell):    {skipped_no_file}")
    print(f"Zu übertragende Dateien: {len(candidates)}")
    print("======================================================================")

    if args.dry_run:
        print("\n[DRY RUN] Folgende Dateien würden hochgeladen:")
        for idx, (rec, fpath) in enumerate(candidates, 1):
            d_str = rec["datetime"].strftime("%Y-%m-%d %H:%M") if rec["datetime"] else rec["date_str"]
            print(f"  [{idx:02d}/{len(candidates):02d}] {d_str} | {rec['sport']}: {rec['name']} ({fpath.name})")
        return

    if not candidates:
        print("Keine neuen Aktivitäten zum Hochladen gefunden.")
        return

    uploaded_count = 0
    failed_count = 0

    for idx, (rec, fpath) in enumerate(candidates, 1):
        d_str = rec["datetime"].strftime("%Y-%m-%d %H:%M") if rec["datetime"] else rec["date_str"]
        print(f"[{idx:02d}/{len(candidates):02d}] Lade hoch: {d_str} - {rec['name']} ({fpath.name})... ", end="", flush=True)

        status, resp = upload_activity_file(
            file_path=fpath,
            api_key=api_key,
            athlete_id=args.athlete_id,
            api_base_url=args.api_base,
            name=rec["name"],
        )

        if status in (200, 201):
            print(f"OK (HTTP {status})")
            uploaded_count += 1
        else:
            print(f"FEHLER (HTTP {status}): {resp}")
            failed_count += 1

        # Gentle delay between uploads to be polite to Intervals rate limits
        if idx < len(candidates):
            time.sleep(0.3)

    print("======================================================================")
    print(f"Ergebnis: {uploaded_count} hochgeladen, {failed_count} fehlgeschlagen, {skipped_existing} übersprungen.")
    print("Tipp: Führe nun './run sync' aus, um die lokalen Daten zu aktualisieren.")
    print("======================================================================")


if __name__ == "__main__":
    main()
