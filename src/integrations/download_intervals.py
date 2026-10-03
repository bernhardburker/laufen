#!/usr/bin/env python3
"""
download_intervals.py

Lädt Aktivitäten von Intervals.icu über die REST API herunter
und speichert sie lokal im Verzeichnis 'data/'.

Funktionen:
- Lädt standardmäßig den gesamten Aktivitätsverlauf herunter.
- Ermittelt API-Schlüssel aus System-Umgebungsvariable oder .env-Datei.
- Berechnet Pace (min/km) und formatiert Distanzen & Herzfrequenzen.
- Keine externen Python-Bibliotheken erforderlich (reine Standardbibliothek).
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
    """Liest Schlüssel-Wert-Paare aus einer .env-Datei in os.environ, falls noch nicht gesetzt."""
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
            # Nur setzen, wenn in os.environ noch nicht vorhanden
            if key not in os.environ and val:
                os.environ[key] = val


def format_duration(seconds: float | int | None) -> str:
    """Formatiert Sekunden in hh:mm:ss oder mm:ss."""
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
    """Konvertiert Geschwindigkeit (m/s) in Lauf-Pace (min/km)."""
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
) -> list[dict]:
    """Ruft Aktivitäten von der Intervals.icu API ab."""
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
        error_msg = e.read().decode("utf-8", errors="replace")
        if e.code == 401 or e.code == 403:
            sys.exit(f"Fehler {e.code}: Authentifizierung bei Intervals.icu fehlgeschlagen. Bitte INTERVALS_API_KEY prüfen.")
        sys.exit(f"HTTP-Fehler {e.code} beim Abruf der Aktivitäten: {error_msg}")
    except urllib.error.URLError as e:
        sys.exit(f"Netzwerkfehler beim Verbinden mit Intervals.icu: {e.reason}")


def main() -> None:
    # 1. Projektpfad & .env prüfen
    project_root = Path(__file__).resolve().parent.parent.parent
    load_env_file(project_root / ".env")

    # 2. Argumente parsen
    parser = argparse.ArgumentParser(
        description="Lädt Aktivitäten von Intervals.icu herunter und speichert sie lokal."
    )
    parser.add_argument(
        "--oldest",
        default="2000-01-01",
        help="Startdatum im Format YYYY-MM-DD (Standard: 2000-01-01 für alle Aktivitäten)",
    )
    parser.add_argument(
        "--newest",
        default=None,
        help="Enddatum im Format YYYY-MM-DD (Standard: morgen)",
    )
    parser.add_argument(
        "--output",
        default=str(project_root / "data" / "intervals_activities.json"),
        help="Zieldatei für den JSON-Export (Standard: data/intervals_activities.json)",
    )
    parser.add_argument(
        "--save-individual",
        action="store_true",
        help="Speichert jede Aktivität zusätzlich als separate JSON-Datei unter data/activities/",
    )
    parser.add_argument(
        "--athlete-id",
        default=os.getenv("INTERVALS_ATHLETE_ID", "0"),
        help="Athlete-ID auf Intervals.icu (Standard: '0' für den angemeldeten Benutzer)",
    )
    parser.add_argument(
        "--type-filter",
        default=None,
        help="Filtert nach Aktivitätstyp (z. B. 'Run')",
    )

    parser.add_argument(
        "--api-base",
        default=os.getenv("INTERVALS_API_BASE", "https://intervals.icu/api/v1"),
        help="Base-URL der Intervals.icu API (Standard: https://intervals.icu/api/v1)",
    )

    args = parser.parse_args()

    # 3. API-Key validieren
    api_key = os.getenv("INTERVALS_API_KEY")
    if not api_key:
        sys.exit(
            "Fehler: INTERVALS_API_KEY ist weder in der Umgebung noch in .env gesetzt.\n"
            "Bitte via Shell exportieren oder in .env eintragen."
        )

    print(f"Verbinde mit Intervals.icu (Athlete ID: {args.athlete_id})...")
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

    print(f"Erfolgreich {len(activities)} Aktivitäten geladen und gespeichert in:")
    print(f"-> {output_path}")

    if args.save_individual:
        single_dir = output_path.parent / "activities"
        single_dir.mkdir(parents=True, exist_ok=True)
        for act in activities:
            act_id = act.get("id", "unknown")
            with open(single_dir / f"{act_id}.json", "w", encoding="utf-8") as f:
                json.dump(act, f, indent=2, ensure_ascii=False)
        print(f"-> Einzeldateien abgelegt in: {single_dir}")

    # 4. Übersicht der letzten Aktivitäten ausgeben
    print("\nÜbersicht der letzten 10 Aktivitäten:")
    header = f"{'Datum':<12} | {'Typ':<6} | {'Distanz':>8} | {'Dauer':>8} | {'Pace':>9} | {'Ø Puls':>6} | {'Max Puls':>8} | {'Titel'}"
    print("-" * len(header))
    print(header)
    print("-" * len(header))

    # Neueste zuerst
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
        print(f"Gesamt Läufe (Run): {run_count} | Gesamtstrecke: {total_km:.1f} km | Gesamtzeit: {format_duration(total_run_time_s)} | Ø Pace: {overall_pace}")


if __name__ == "__main__":
    main()
